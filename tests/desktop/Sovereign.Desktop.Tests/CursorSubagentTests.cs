using System.Text.Json;
using Sovereign.Desktop.Client;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class CursorSubagentTests
{
    private static string Reply(int id, object result) => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result });
    private static string Update(string session, object update) => JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "session/update", @params = new { sessionId = session, update } });
    private static (CursorAcpSession, StringWriter) Protocol(bool enabled, bool supported, Action<CursorActivityEvent> observe, params string[] events)
        => ConfiguredProtocol(new() { Mode = CursorProjectMode.Plan, EnableSubagents = enabled,
            RequestPermission = (_, _) => throw new InvalidOperationException("Must not prompt for child edits") }, supported, observe, events);

    private static (CursorAcpSession, StringWriter) ConfiguredProtocol(CursorProjectOptions options, bool supported, Action<CursorActivityEvent> observe, params string[] events)
    {
        var init = new[] { Reply(1, new { protocolVersion = 1, agentCapabilities = new { sessionCapabilities = supported ? new Dictionary<string, object> { ["subagents"] = new { } } : new() } }),
            Reply(2, new { sessionId = "p", models = new { availableModels = new[] { new { modelId = "model", name = "Model" } } } }), Reply(3, new { }), Reply(4, new { }) };
        var writer = new StringWriter();
        return (new CursorAcpSession(new StringReader(string.Join('\n', init.Concat(events))), writer,
            options, observe), writer);
    }

    [Fact]
    public async Task Capability_is_negotiated_and_child_text_does_not_leak_into_main_reply()
    {
        var observed = new List<CursorActivityEvent>();
        var (session, writer) = Protocol(true, true, observed.Add,
            Update("p", Spawn("c")), Update("c", new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "private helper output" } }),
            Update("p", State("c", "completed")), Update("p", new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "parent summary" } }), Reply(5, new { stopReason = "end_turn" }));
        await session.ConnectAsync("fixture", default);
        var output = ""; await session.SendAsync("model", "inspect", x => output += x, default);
        Assert.Equal("parent summary", output);
        Assert.Equal("private helper output", observed.Last(x => x.Subagent is not null).Subagent!.Output);
        using var first = JsonDocument.Parse(writer.ToString().Split('\n')[0]);
        Assert.True(first.RootElement.GetProperty("params").GetProperty("clientCapabilities").GetProperty("_meta").GetProperty("subagents").GetBoolean());
    }

    [Theory]
    [InlineData("launch-backend")]
    [InlineData("launch-backend\nfc_backend_0")]
    public async Task Generic_native_helper_name_uses_the_correlated_task_description(string acpToolId)
    {
        var observed = new List<CursorActivityEvent>();
        var (session, _) = ConfiguredProtocol(new(), true, observed.Add,
            Update("p", new { sessionUpdate = "tool_call", toolCallId = acpToolId, title = "Task", kind = "other", status = "in_progress",
                rawInput = new { description = "Backend", prompt = "Build API" } }),
            Update("p", new { sessionUpdate = "subagent_spawned", subagentSessionId = "backend-child", name = "generalPurpose", task = "Build API",
                _meta = new { cursor = new { toolCallId = "launch-backend" } } }),
            Update("p", State("backend-child", "completed")), Message("p", "Summary"), Reply(5, new { stopReason = "end_turn" }));
        await session.ConnectAsync("fixture", default);
        await session.SendAsync("model", "implement", _ => { }, default);
        Assert.Equal("Backend", observed.First(e => e.Subagent is not null).Subagent!.Name);
    }

    [Fact]
    public async Task Unsupported_runtime_keeps_chat_working_but_disabled_spawn_fails_closed()
    {
        var (unsupported, sent) = Protocol(true, false, _ => { }, Message("p", "ordinary reply"), Reply(5, new { stopReason = "end_turn" }));
        await unsupported.ConnectAsync("fixture", default);
        Assert.False(unsupported.SupportsSubagents);
        await unsupported.SendAsync("model", "inspect", _ => { }, default);
        Assert.DoesNotContain("Native subagents are enabled", sent.ToString());
        var (disabled, _) = Protocol(false, true, _ => { }, Update("p", Spawn("c")));
        await disabled.ConnectAsync("fixture", default);
        await Assert.ThrowsAsync<IOException>(() => disabled.SendAsync("model", "inspect", _ => { }, default));
    }

    [Fact]
    public async Task Stop_notifies_the_parent_session_without_inventing_child_completion()
    {
        var (session, writer) = Protocol(true, true, _ => { });
        await session.ConnectAsync("fixture", default); await session.CancelAsync(default);
        using var message = JsonDocument.Parse(writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Last());
        Assert.Equal("session/cancel", message.RootElement.GetProperty("method").GetString());
        Assert.Equal("p", message.RootElement.GetProperty("params").GetProperty("sessionId").GetString());
        Assert.False(message.RootElement.TryGetProperty("id", out _));
        Assert.DoesNotContain("completed", writer.ToString());
    }

    [Fact]
    public async Task Child_edit_permission_is_cancelled_without_callback()
    {
        var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = "permission", method = "session/request_permission", @params = new { sessionId = "c",
            toolCall = new { toolCallId = "edit", title = "Edit fixture", kind = "edit", status = "pending" },
            options = new[] { new { optionId = "yes", name = "Allow", kind = "allow_once" } } } });
        var (session, writer) = Protocol(true, true, _ => { }, Update("p", Spawn("c")), request,
            Update("p", State("c", "failed")), Update("p", new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "Denied" } }), Reply(5, new { stopReason = "end_turn" }));
        await session.ConnectAsync("fixture", default); await session.SendAsync("model", "inspect", _ => { }, default);
        Assert.Contains("\"outcome\":\"cancelled\"", writer.ToString());
        Assert.DoesNotContain("\"optionId\":\"yes\"", writer.ToString());
    }

    [Fact]
    public void Preference_and_child_results_roundtrip_and_legacy_defaults_are_migrated()
    {
        var root = Path.Combine(Path.GetTempPath(), "Sovereign-subagent-" + Guid.NewGuid().ToString("N"));
        try
        {
            var archive = new ChatArchive(root);
            var chat = ChatArchive.New(4, "fixture") with { EnableSubagents = true, SubagentPreferenceVersion = 1, ProjectMode = CursorProjectMode.Plan,
                Turns = [new("inspect", "grok", "summary", "completed", [new("subagent", Subagent: new("c", "generalPurpose", "read", "completed", "finding"))])] };
            archive.Save(chat);
            var loaded = Assert.Single(archive.Load());
            Assert.True(loaded.SubagentsEnabled); Assert.Equal("finding", loaded.Turns[0].Activity[0].Subagent!.Output);
            Assert.True(ChatArchive.New(4, "fixture").SubagentsEnabled);
            archive.Save(chat with { EnableSubagents = false });
            Assert.False(Assert.Single(archive.Load()).SubagentsEnabled);
            archive.Save(chat with { EnableSubagents = false, SubagentPreferenceVersion = 0 });
            Assert.True(Assert.Single(archive.Load()).SubagentsEnabled);
            var withoutPreference = JsonSerializer.Serialize(chat).Replace("\"EnableSubagents\":true,", "").Replace("\"SubagentPreferenceVersion\":1", "\"SubagentPreferenceVersion\":0");
            Assert.True(JsonSerializer.Deserialize<SavedChat>(withoutPreference)!.SubagentsEnabled);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static JsonElement Spawn(string id) => JsonSerializer.SerializeToElement(new { sessionUpdate = "subagent_spawned", subagentSessionId = id, name = "explore", task = "Inspect fixture" });
    private static JsonElement State(string id, string state) => JsonSerializer.SerializeToElement(new { sessionUpdate = "subagent_state_update", subagentSessionId = id, state });
    private static string Message(string id, string text) => Update(id, new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text } });
    private static string Permission(string session, string kind = "edit", string selectedKind = "allow_once") => JsonSerializer.Serialize(new {
        jsonrpc = "2.0", id = "permission", method = "session/request_permission", @params = new { sessionId = session,
            toolCall = new { toolCallId = "edit", title = "Edit fixture", kind, status = "pending" },
            options = new[] { new { optionId = "yes", name = "Allow", kind = selectedKind } } } });

    [Theory]
    [InlineData(CursorPermissionPolicy.AlwaysAsk)]
    [InlineData(CursorPermissionPolicy.AlwaysAllow)]
    public async Task Agent_child_edits_inherit_parent_permissions_and_identify_the_helper(CursorPermissionPolicy policy)
    {
        CursorPermissionRequest? request = null;
        var observed = new List<CursorActivityEvent>();
        var (session, writer) = ConfiguredProtocol(new() { Permissions = policy, RequestPermission = (value, _) => {
            request = value; return Task.FromResult<string?>("yes"); } }, true, observed.Add,
            Update("p", Spawn("c")), Permission("c"), Update("p", State("c", "completed")),
            Message("p", "Parent result"), Reply(5, new { stopReason = "end_turn" }));
        await session.ConnectAsync("fixture", default);
        await session.SendAsync("model", "implement", _ => { }, default);
        Assert.Contains("\"optionId\":\"yes\"", writer.ToString());
        if (policy == CursorPermissionPolicy.AlwaysAsk)
        {
            Assert.Equal("c", request!.Subagent!.SessionId);
            Assert.Equal("c:edit", request.Tool.Id);
            Assert.Contains(observed, e => e.Subagent?.CurrentWork.StartsWith("Waiting for your approval") == true);
        }
        else Assert.Null(request);
        Assert.DoesNotContain(observed, e => e.Kind == "tool");
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("execute")]
    [InlineData("fetch")]
    [InlineData("other")]
    public async Task Plan_child_cannot_grant_mutating_or_unclassified_tools_even_with_always_allow(string kind)
    {
        var (session, writer) = ConfiguredProtocol(new() { Mode = CursorProjectMode.Plan, Permissions = CursorPermissionPolicy.AlwaysAllow,
            RequestPermission = (_, _) => throw new InvalidOperationException("Plan child mutations must not prompt") }, true, _ => { },
            Update("p", Spawn("c")), Permission("c", kind), Message("p", "Blocked"), Reply(5, new { stopReason = "end_turn" }));
        await session.ConnectAsync("fixture", default);
        await session.SendAsync("model", "plan", _ => { }, default);
        Assert.Contains("\"outcome\":\"cancelled\"", writer.ToString());
        Assert.DoesNotContain("\"optionId\":\"yes\"", writer.ToString());
    }

    [Theory]
    [InlineData("read")]
    [InlineData("search")]
    public async Task Plan_child_inspection_asks_for_permission_and_cannot_inherit_always_allow(string kind)
    {
        var prompted = false;
        var (session, writer) = ConfiguredProtocol(new() { Mode = CursorProjectMode.Plan, Permissions = CursorPermissionPolicy.AlwaysAllow,
            RequestPermission = (_, _) => { prompted = true; return Task.FromResult<string?>("yes"); } }, true, _ => { },
            Update("p", Spawn("c")), Permission("c", kind), Message("p", "Inspected"), Reply(5, new { stopReason = "end_turn" }));
        await session.ConnectAsync("fixture", default);
        await session.SendAsync("model", "plan", _ => { }, default);
        Assert.True(prompted);
        Assert.Contains("\"optionId\":\"yes\"", writer.ToString());
    }

    [Fact]
    public async Task Child_cannot_choose_persistent_or_invented_grants()
    {
        var (session, writer) = ConfiguredProtocol(new() { RequestPermission = (_, _) => Task.FromResult<string?>("yes") }, true, _ => { },
            Update("p", Spawn("c")), Permission("c", selectedKind: "allow_always"), Message("p", "Rejected"), Reply(5, new { stopReason = "end_turn" }));
        await session.ConnectAsync("fixture", default);
        await session.SendAsync("model", "implement", _ => { }, default);
        Assert.Contains("\"outcome\":\"cancelled\"", writer.ToString());
    }

    [Fact]
    public async Task Terminal_child_cannot_request_more_permissions()
    {
        var prompted = false;
        var (session, writer) = ConfiguredProtocol(new() { RequestPermission = (_, _) => { prompted = true; return Task.FromResult<string?>("yes"); } }, true, _ => { },
            Update("p", Spawn("c")), Update("p", State("c", "completed")), Permission("c"));
        await session.ConnectAsync("fixture", default);
        await Assert.ThrowsAsync<IOException>(() => session.SendAsync("model", "implement", _ => { }, default));
        Assert.False(prompted);
        Assert.DoesNotContain("\"optionId\":\"yes\"", writer.ToString());
    }

    [Fact]
    public async Task Child_thoughts_tools_and_permissions_are_separate_from_parent_tool_identity()
    {
        CursorPermissionRequest? parentPermission = null;
        var observed = new List<CursorActivityEvent>();
        var (session, _) = ConfiguredProtocol(new() { RequestPermission = (value, _) => {
            if (value.Subagent is null) parentPermission = value;
            return Task.FromResult<string?>("yes"); } }, true, observed.Add,
            Update("p", new { sessionUpdate = "tool_call", toolCallId = "edit", title = "Parent edit", kind = "edit", status = "pending" }),
            Update("p", Spawn("c")), Update("c", new { sessionUpdate = "agent_thought_chunk", content = new { type = "text", text = "Helper thought" } }),
            Permission("c"), Update("c", new { sessionUpdate = "tool_call_update", toolCallId = "edit", status = "completed", content = new[] {
                new { type = "diff", path = "helper.txt", oldText = "before", newText = "after" } } }),
            JsonSerializer.Serialize(new { jsonrpc = "2.0", id = "parent-permission", method = "session/request_permission", @params = new {
                sessionId = "p", toolCall = new { toolCallId = "edit" }, options = new[] { new { optionId = "yes", name = "Allow", kind = "allow_once" } } } }),
            Update("p", State("c", "completed")), Message("p", "Parent result"), Reply(5, new { stopReason = "end_turn" }));
        await session.ConnectAsync("fixture", default);
        var answer = ""; await session.SendAsync("model", "implement", x => answer += x, default);
        Assert.Equal("Parent result", answer);
        Assert.Equal("Parent edit", parentPermission!.Tool.Title);
        Assert.DoesNotContain(observed, e => e.Kind == "reasoning");
        var child = observed.Last(e => e.Subagent is not null).Subagent!;
        Assert.Equal("Helper thought", child.Reasoning);
        Assert.Equal("c:edit", child.Tool!.Id);
        Assert.Equal("after", Assert.Single(Assert.Single(child.Tools!).Changes).After);
        Assert.Contains(observed, e => e.Subagent is not null && e.Tool?.Changes.Length == 1);
        Assert.NotNull(child.StartedAt); Assert.NotNull(child.CompletedAt);
        Assert.True(child.CompletedAt >= child.StartedAt);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ended_or_disconnected_parent_does_not_leave_child_running(bool completed)
    {
        var observed = new List<CursorActivityEvent>();
        var tail = completed ? new[] { Message("p", "Parent result"), Reply(5, new { stopReason = "end_turn" }) } : Array.Empty<string>();
        var (session, _) = Protocol(true, true, observed.Add, new[] { Update("p", Spawn("c")) }.Concat(tail).ToArray());
        await session.ConnectAsync("fixture", default);
        if (completed) await session.SendAsync("model", "inspect", _ => { }, default);
        else await Assert.ThrowsAsync<IOException>(() => session.SendAsync("model", "inspect", _ => { }, default));
        var child = observed.Last(e => e.Subagent is not null).Subagent!;
        Assert.Equal("disconnected", child.State);
        Assert.Contains("unconfirmed", child.CurrentWork);
        Assert.NotNull(child.CompletedAt);
    }

    [Fact]
    public void Terminal_state_is_idempotent_and_cannot_regress()
    {
        var tracker = new CursorSubagents(); tracker.Observe("p", "p", Spawn("c"));
        var finished = tracker.Observe("p", "p", State("c", "completed"));
        Assert.Null(tracker.Observe("p", "p", State("c", "completed")));
        Assert.Null(tracker.Observe("p", "c", JsonSerializer.SerializeToElement(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "Late" } })));
        Assert.Equal(finished, tracker.Get("c"));
        Assert.Throws<IOException>(() => tracker.Observe("p", "p", State("c", "failed")));
    }

    [Fact]
    public async Task Child_mode_change_cannot_silently_leave_plan()
    {
        var (session, _) = Protocol(true, true, _ => { }, Update("p", Spawn("c")),
            Update("c", new { sessionUpdate = "current_mode_update", currentModeId = "agent" }));
        await session.ConnectAsync("fixture", default);
        await Assert.ThrowsAsync<IOException>(() => session.SendAsync("model", "plan", _ => { }, default));
    }

    [Fact]
    public async Task Steering_can_receive_late_native_child_confirmation_in_the_next_turn()
    {
        CursorAcpSession? active = null;
        var observed = new List<CursorActivityEvent>();
        var (session, _) = ConfiguredProtocol(new(), true, e => {
            observed.Add(e);
            if (e.Subagent?.State == "running") active!.RequestSteeringAsync(default).GetAwaiter().GetResult();
        }, Update("p", Spawn("c")), Reply(5, new { stopReason = "cancelled" }), Reply(6, new { }),
            Update("p", State("c", "cancelled")), Message("p", "Corrected answer"), Reply(7, new { stopReason = "end_turn" }));
        active = session;
        await session.ConnectAsync("fixture", default);
        Assert.Equal(CursorTurnOutcome.Redirected, await session.SendAsync("model", "first", _ => { }, default));
        Assert.Equal("disconnected", observed.Last(e => e.Subagent is not null).Subagent!.State);
        Assert.Equal(CursorTurnOutcome.Completed, await session.SendAsync("model", "correction", _ => { }, default));
        Assert.Equal("cancelled", observed.Last(e => e.Subagent is not null).Subagent!.State);
    }

    [Fact]
    public void Identity_limit_applies_even_when_children_have_finished()
    {
        var tracker = new CursorSubagents();
        for (var i = 0; i < 16; i++) { var id = "child" + i; tracker.Observe("p", "p", Spawn(id)); tracker.Observe("p", "p", State(id, "completed")); }
        Assert.Throws<IOException>(() => tracker.Observe("p", "p", Spawn("seventeen")));
    }

    [Fact]
    public void Saved_child_details_are_bounded_and_oversized_diffs_are_explicitly_omitted()
    {
        var tracker = new CursorSubagents(); tracker.Observe("p", "p", Spawn("c"));
        tracker.Observe("p", "c", JsonSerializer.SerializeToElement(new { sessionUpdate = "agent_thought_chunk", content = new { type = "text", text = new string('a', 10000) } }));
        var child = tracker.Observe("p", "c", JsonSerializer.SerializeToElement(new { sessionUpdate = "tool_call", toolCallId = "t", title = new string('b', 1000),
            status = "completed", kind = "edit", rawInput = new string('c', 4000), content = new[] { new { type = "diff", path = "large.txt", oldText = "", newText = new string('d', 40000) } } }))!;
        Assert.Equal(6000, child.Reasoning.Length);
        Assert.Equal(240, child.CurrentWork.Length);
        Assert.Equal(2000, child.Tool!.Input.Length);
        Assert.Empty(child.Tool.Changes);
        Assert.Contains("omitted", child.Tool.Output);
        Assert.Equal(40000, Assert.Single(tracker.GetTool("c", "t")!.Changes).After.Length);
    }

    [Fact]
    public void Subagents_default_on_in_every_project_mode_and_can_be_disabled()
    {
        Assert.True(new CursorProjectOptions().SubagentsAllowed);
        Assert.True(new CursorProjectOptions { Permissions = CursorPermissionPolicy.AlwaysAllow }.SubagentsAllowed);
        Assert.True(new CursorProjectOptions { Mode = CursorProjectMode.Plan }.SubagentsAllowed);
        Assert.False(new CursorProjectOptions { EnableSubagents = false }.SubagentsAllowed);
    }
    [Fact]
    public void Child_output_has_its_own_session_and_terminal_state()
    {
        var tracker = new CursorSubagents();
        Assert.Equal("running", tracker.Observe("parent", "parent", Spawn("child"))!.State);
        var result = tracker.Observe("parent", "child", JsonSerializer.SerializeToElement(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "Helper finding" } }));
        Assert.Equal("Helper finding", result!.Output);
        Assert.Equal("completed", tracker.Observe("parent", "parent", State("child", "completed"))!.State);
    }
    [Fact]
    public void Foreign_nested_and_duplicate_children_fail_closed()
    {
        var tracker = new CursorSubagents();
        Assert.Throws<IOException>(() => tracker.Observe("parent", "other", Spawn("child")));
        tracker.Observe("parent", "parent", Spawn("child"));
        Assert.Throws<IOException>(() => tracker.Observe("parent", "parent", Spawn("child")));
        Assert.Throws<IOException>(() => tracker.Observe("parent", "child", Spawn("grandchild")));
        Assert.Throws<IOException>(() => tracker.Observe("parent", "parent", State("stranger", "completed")));
    }
    [Fact]
    public void More_than_three_active_children_stops_session_but_finished_slot_is_reusable()
    {
        var tracker = new CursorSubagents();
        tracker.Observe("p", "p", Spawn("one")); tracker.Observe("p", "p", Spawn("two"));
        tracker.Observe("p", "p", Spawn("three"));
        Assert.Throws<IOException>(() => tracker.Observe("p", "p", Spawn("four")));
        tracker.Observe("p", "p", State("one", "completed"));
        Assert.NotNull(tracker.Observe("p", "p", Spawn("four")));
    }
    [Fact]
    public void Child_preview_is_bounded()
    {
        var tracker = new CursorSubagents(); tracker.Observe("p", "p", Spawn("c"));
        var result = tracker.Observe("p", "c", JsonSerializer.SerializeToElement(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = new string('a', 10000) } }));
        Assert.Equal(6000, result!.Output.Length);
    }
}
