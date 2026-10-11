using System.Text.Json;
using Sovereign.Desktop.Client;
using Xunit;

namespace Sovereign.Desktop.Tests;

/// <summary>Controlled ACP streams only. Thousands of fixture events are not model/tool invocations.</summary>
public sealed class CursorActivityRetentionTests : IDisposable
{
    private readonly string _profile = Path.Combine(Path.GetTempPath(), "cursor-retention-tests-" + Guid.NewGuid().ToString("N"));
    private static string Reply(int id, object result) => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result });
    private static string Update(string session, object update) => JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "session/update", @params = new { sessionId = session, update } });
    private static string Spawn(string id) => Update("parent", new { sessionUpdate = "subagent_spawned", subagentSessionId = id, name = id, task = "Controlled fixture inspection" });
    private static string ChildState(string id, string state) => Update("parent", new { sessionUpdate = "subagent_state_update", subagentSessionId = id, state });
    private static string Message(string id, string text) => Update(id, new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text } });
    private static string Tool(string session, string id, string title, string status = "completed", string kind = "read", object[]? content = null)
        => Update(session, new { sessionUpdate = "tool_call", toolCallId = id, title, kind, status, rawInput = new { path = "protected-fixture.txt" }, content = content ?? [] });
    private static object Diff(string before, string after) => new { type = "diff", path = "shared-fixture.txt", oldText = before, newText = after };
    private static object Text(string value) => new { type = "content", content = new { type = "text", text = value } };
    private static string Permission(string session, string tool)
        => JsonSerializer.Serialize(new { jsonrpc = "2.0", id = "permission-" + session, method = "session/request_permission", @params = new {
            sessionId = session, toolCall = new { toolCallId = tool }, options = new[] { new { optionId = "once", name = "Allow once", kind = "allow_once" } } } });

    [Theory]
    [InlineData(80, 0)]
    [InlineData(0, 1100)]
    [InlineData(80, 1100)]
    public async Task Completed_history_pressure_keeps_three_helpers_results_parent_continuation_and_active_permissions(int helperCalls, int parentCalls)
    {
        var frames = new List<string>
        {
            Reply(1, new { protocolVersion = 1, agentCapabilities = new { sessionCapabilities = new { subagents = new { } } } }),
            Reply(2, new { sessionId = "parent", models = new { availableModels = new[] { new { modelId = "fixture-model", name = "Controlled fixture model" } } } }),
            Reply(3, new { }), Reply(4, new { }), Spawn("heavy"), Spawn("spare-a"), Spawn("spare-b"),
            Tool("heavy", "helper-pinned", "Read active helper fixture", "in_progress", content: [Text("Partial helper output retained")]),
            Tool("parent", "parent-pinned", "Read active parent fixture", "in_progress"),
            Tool("parent", "earliest-edit", "Original edit", kind: "edit", content: [Diff("original-before", "intermediate-after")])
        };
        for (var index = 0; index < helperCalls; index++) frames.Add(Tool("heavy", "read-" + index, "Controlled helper read " + index));
        for (var index = 0; index < parentCalls; index++) frames.Add(Tool("parent", "read-" + index, "Controlled parent read " + index));
        frames.Add(Permission("heavy", "helper-pinned"));
        frames.Add(Permission("parent", "parent-pinned"));
        frames.Add(Update("heavy", new { sessionUpdate = "tool_call_update", toolCallId = "helper-pinned", status = "completed" }));
        frames.Add(Update("parent", new { sessionUpdate = "tool_call_update", toolCallId = "parent-pinned", status = "completed" }));
        // An old terminal identity still correlates a status-only update after
        // compaction. It must not manufacture a new tool or lose its old diff.
        frames.Add(Update("parent", new { sessionUpdate = "tool_call_update", toolCallId = "earliest-edit", status = "completed" }));
        frames.Add(Tool("parent", "latest-edit", "Final edit", kind: "edit", content: [Diff("intermediate-after", "final-after") ]));
        frames.Add(Update("parent", new { sessionUpdate = "tool_call_update", toolCallId = "earliest-edit", status = "completed" }));
        foreach (var child in new[] { "heavy", "spare-a", "spare-b" }) { frames.Add(Message(child, child + "-ACTUAL-FIXTURE-RESULT")); frames.Add(ChildState(child, "completed")); }
        frames.Add(Message("parent", "PARENT-CONTINUED-AFTER-THREE-RESULTS")); frames.Add(Reply(5, new { stopReason = "end_turn" }));
        var observed = new List<CursorActivityEvent>(); var state = new CursorTurnActivity(); var permissions = new List<CursorPermissionRequest>();
        var writer = new StringWriter();
        var session = new CursorAcpSession(new StringReader(string.Join('\n', frames)), writer, new CursorProjectOptions
        {
            RequestPermission = (request, _) =>
            {
                permissions.Add(request);
                Assert.Equal(3, observed.Where(item => item.Subagent is not null).GroupBy(item => item.Subagent!.SessionId).Count(group => group.Last().Subagent!.State == "running"));
                Assert.Contains("protected-fixture.txt", request.Tool.Input);
                Assert.Equal("read", request.Tool.Kind);
                return Task.FromResult<string?>("once");
            }
        }, update => { observed.Add(update); state.Apply(update, TimeSpan.Zero); });
        await session.ConnectAsync("controlled-fixture-workspace", default);
        var parentAnswer = "";
        Assert.Equal(CursorTurnOutcome.Completed, await session.SendAsync("fixture-model", "Controlled stream; no tools actually execute.", piece => parentAnswer += piece, default));
        Assert.Equal("PARENT-CONTINUED-AFTER-THREE-RESULTS", parentAnswer);
        Assert.Equal(2, permissions.Count);
        Assert.Equal("Read active helper fixture", permissions[0].Tool.Title);
        Assert.Equal("Partial helper output retained", permissions[0].Tool.Output);
        Assert.Equal("heavy", permissions[0].Subagent!.SessionId);
        Assert.Null(permissions[1].Subagent);
        var children = observed.Where(item => item.Subagent is not null).GroupBy(item => item.Subagent!.SessionId).Select(group => group.Last().Subagent!).ToArray();
        Assert.Equal(3, children.Length);
        Assert.All(children, child => { Assert.Equal("completed", child.State); Assert.Equal(child.SessionId + "-ACTUAL-FIXTURE-RESULT", child.Output); });
        Assert.True(children.Single(child => child.SessionId == "heavy").Tools!.Length <= 64);
        var heavySummary = children.Single(child => child.SessionId == "heavy").ToolSummary!;
        Assert.Equal(helperCalls + 1, heavySummary.Total);
        Assert.Equal(helperCalls + 1, heavySummary.Completed);
        Assert.Equal(0, heavySummary.Active);
        Assert.Equal(parentCalls + helperCalls + 4, state.TotalTools);
        Assert.Equal(state.TotalTools, state.CompletedTools);
        Assert.Equal(0, state.ActiveTools);
        Assert.True(state.Tools.Count(tool => tool.Status is "completed" or "failed" or "blocked") <= 1000);
        if (helperCalls > 64) Assert.True(heavySummary.Compacted > 0);
        if (state.TotalTools > 1000) Assert.True(state.CompactedTools > 0);
        var change = Assert.Single(state.Changes);
        Assert.Equal("original-before", change.Before); Assert.Equal("final-after", change.After);
        var sent = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(text => JsonDocument.Parse(text).RootElement.Clone()).ToArray();
        Assert.Single(sent, frame => frame.TryGetProperty("method", out var method) && method.GetString() == "session/prompt");
        Assert.Equal(2, sent.Count(frame => frame.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && frame.GetProperty("result").GetProperty("outcome").GetProperty("outcome").GetString() == "selected"));
        RoundTrip(observed, parentAnswer);
    }

    [Fact]
    public void Active_helper_rows_exceed_recent_history_budget_and_permission_merge_can_immediately_retrieve_old_identity()
    {
        var children = new CursorSubagents();
        foreach (var id in new[] { "heavy", "spare-a", "spare-b" }) children.Observe("parent", "parent", J(new { sessionUpdate = "subagent_spawned", subagentSessionId = id, name = id, task = "Controlled fixture" }));
        for (var index = 0; index < 70; index++) children.ObserveTool("heavy", J(new { sessionUpdate = "tool_call", toolCallId = "active-" + index,
            title = "Pinned inspection " + index, kind = "read", status = "in_progress", rawInput = new { path = "original-active-file" },
            content = new[] { Text("Original partial output") } }));
        for (var index = 0; index < 90; index++) children.ObserveTool("heavy", J(new { sessionUpdate = "tool_call", toolCallId = "done-" + index, title = "Completed inspection", kind = "read", status = "completed" }));
        var active = children.Get("heavy")!;
        Assert.Equal(70, active.ToolSummary!.Active);
        Assert.True(active.Tools!.Length <= 64);
        Assert.True(active.Tools.Count(tool => tool.Status == "completed") <= 64);
        var merged = children.MergeTool("heavy", J(new { toolCallId = "active-0" }));
        var immediate = children.GetTool("heavy", "active-0")!;
        Assert.Equal(merged.Id, immediate.Id); Assert.Equal(merged.Title, immediate.Title); Assert.Equal(merged.Input, immediate.Input);
        Assert.Equal("Pinned inspection 0", immediate.Title); Assert.Equal("read", immediate.Kind);
        Assert.Contains("original-active-file", immediate.Input); Assert.Equal("Original partial output", immediate.Output);
        for (var index = 0; index < 70; index++) children.ObserveTool("heavy", J(new { sessionUpdate = "tool_call_update", toolCallId = "active-" + index, status = "completed" }));
        var finishedTools = children.Get("heavy")!;
        Assert.Equal(160, finishedTools.ToolSummary!.Total); Assert.Equal(160, finishedTools.ToolSummary.Completed);
        Assert.Equal(0, finishedTools.ToolSummary.Active); Assert.True(finishedTools.Tools!.Length <= 64);
        children.Observe("parent", "parent", J(new { sessionUpdate = "subagent_state_update", subagentSessionId = "heavy", state = "completed" }));
        Assert.True(children.HasRunning); // The other two helpers keep their real lifecycle.
        Assert.Throws<IOException>(() => children.MergeTool("heavy", J(new { toolCallId = "active-0" })));
    }

    [Fact]
    public void Active_parent_rows_are_not_pruned_by_the_recent_completed_history_budget()
    {
        var state = new CursorTurnActivity();
        var details = new Dictionary<string, CursorToolActivity>(); using var correlation = new CursorToolRetention(details, 1000);
        for (var index = 0; index < 1010; index++)
        {
            var tool = new CursorToolActivity("active-" + index, "Pending inspection " + index, "read", "pending", "owned input", []);
            correlation.Store(tool); state.Apply(new("tool", Tool: tool), TimeSpan.Zero);
        }
        for (var index = 0; index < 30; index++) state.Apply(new("tool", Tool: new("done-" + index, "Completed inspection", "read", "completed", "", [])), TimeSpan.Zero);
        Assert.Equal(1010, state.ActiveTools);
        Assert.True(state.Tools.Count <= 1000);
        Assert.True(details.Count <= 1000);
        var oldActive = correlation.Find("active-0")!;
        Assert.Equal("Pending inspection 0", oldActive.Title); Assert.Equal("read", oldActive.Kind);
        Assert.Equal("pending", oldActive.Status); Assert.Equal("owned input", oldActive.Input);
        Assert.Equal(1040, state.TotalTools); Assert.Equal(30, state.CompletedTools);
        state.Finish("failed");
        Assert.Equal(0, state.ActiveTools);
        Assert.Equal(1040, state.TotalTools); Assert.Equal(30, state.CompletedTools);
        Assert.DoesNotContain(state.Tools, tool => tool.Status is "pending" or "in_progress");
        Assert.Contains(state.Tools, tool => tool.Status == "unconfirmed");
    }

    [Fact]
    public void Evicted_terminal_identity_preserves_partial_metadata_diffs_and_exact_failure_tallies()
    {
        var children = new CursorSubagents(); children.Observe("parent", "parent", J(new { sessionUpdate = "subagent_spawned", subagentSessionId = "heavy", name = "Heavy", task = "Controlled fixture" }));
        children.ObserveTool("heavy", J(new { sessionUpdate = "tool_call", toolCallId = "original-edit", title = "Original attributed edit", kind = "edit", status = "completed",
            rawInput = new { path = "shared-fixture.txt", correlation = "exact-original-request" }, content = new[] { Diff("original-before", "intermediate-after"), Text("Original output retained") } }));
        children.ObserveTool("heavy", J(new { sessionUpdate = "tool_call", toolCallId = "original-failure", title = "Failed command", kind = "execute", status = "failed" }));
        children.ObserveTool("heavy", J(new { sessionUpdate = "tool_call", toolCallId = "original-block", title = "Blocked command", kind = "execute", status = "completed", content = new[] { Text("Not in allowlist: fixture") } }));
        for (var index = 0; index < 90; index++) children.ObserveTool("heavy", J(new { sessionUpdate = "tool_call", toolCallId = "done-" + index, title = "Completed inspection", kind = "read", status = "completed" }));
        children.ObserveTool("heavy", J(new { sessionUpdate = "tool_call", toolCallId = "final-edit", title = "Final edit", kind = "edit", status = "completed", content = new[] { Diff("intermediate-after", "final-after") } }));
        var before = children.Get("heavy")!.ToolSummary!;
        var merged = children.MergeTool("heavy", J(new { toolCallId = "original-edit", status = "completed" }));
        var immediate = children.GetTool("heavy", "original-edit")!;
        Assert.Equal(merged.Id, immediate.Id); Assert.Equal(merged.Input, immediate.Input); Assert.Equal(merged.Changes, immediate.Changes);
        Assert.Equal("heavy:original-edit", immediate.Id);
        Assert.Equal("Original attributed edit", immediate.Title); Assert.Equal("edit", immediate.Kind);
        Assert.Contains("exact-original-request", immediate.Input); Assert.Equal("Original output retained", immediate.Output);
        Assert.Equal("original-before", Assert.Single(immediate.Changes).Before);
        Assert.Equal("intermediate-after", immediate.Changes[0].After);
        children.ObserveToolActivity("heavy", merged);
        children.ObserveTool("heavy", J(new { sessionUpdate = "tool_call_update", toolCallId = "original-failure", status = "failed" }));
        children.ObserveTool("heavy", J(new { sessionUpdate = "tool_call_update", toolCallId = "original-block", status = "completed" }));
        var after = children.Get("heavy")!;
        Assert.Equal(before.Total, after.ToolSummary!.Total); Assert.Equal(1, after.ToolSummary.Failed); Assert.Equal(1, after.ToolSummary.Blocked);
        var net = Assert.Single(after.Changes!); Assert.Equal("original-before", net.Before); Assert.Equal("final-after", net.After);
        Assert.True(after.Tools!.Length <= 64);
    }

    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void Incremental_compaction_and_json_reopen_keep_exact_counts_changes_and_uncertain_pending_without_resend()
    {
        var first = new List<CursorActivityEvent>
        {
            new("tool", Tool: new("first-edit", "First edit", "edit", "completed", "", [new("shared-fixture.txt", "original-before", "intermediate-after")])),
            new("tool", Tool: new("failed", "Actual failure", "execute", "failed", "", [])),
            new("tool", Tool: new("blocked", "Actual refusal", "execute", "completed", "", [], "Not in allowlist: fixture"))
        };
        for (var index = 0; index < 1100; index++) first.Add(new("tool", Tool: new("read-" + index, "Completed inspection", "read", "completed", "", [])));
        var compacted = CursorActivityRetention.Compact(first);
        Assert.True(compacted.Length <= 1100);
        var later = new[]
        {
            new CursorActivityEvent("tool", Tool: new("final-edit", "Final edit", "edit", "completed", "", [new("shared-fixture.txt", "intermediate-after", "final-after")])),
            first[0], first[1], first[2], // Repeated old identities, not new requests or new file edits.
            new("subagent", Subagent: new("heavy", "Heavy", "Controlled result", "completed", "SAVED-HELPER-RESULT"))
        };
        var final = CursorActivityRetention.Compact(compacted.Concat(later), "completed");
        var summary = Assert.Single(final, item => item.Kind == "summary");
        Assert.Equal(1104, summary.Summary!.Total); Assert.Equal(1102, summary.Summary.Completed);
        Assert.Equal(1, summary.Summary.Failed); Assert.Equal(1, summary.Summary.Blocked);
        Assert.True(summary.Summary.Compacted > 0);
        var net = Assert.Single(summary.Changes!); Assert.Equal("original-before", net.Before); Assert.Equal("final-after", net.After);
        RoundTrip(final.ToList(), "Parent actual saved continuation");
        var json = JsonSerializer.Serialize(final);
        Assert.DoesNotContain("Continuation", json);
        var restored = JsonSerializer.Deserialize<CursorActivityEvent[]>(json)!;
        var state = new CursorTurnActivity(); foreach (var item in restored) state.Apply(item, TimeSpan.Zero);
        Assert.Equal(1104, state.TotalTools); Assert.Equal(1102, state.CompletedTools);
        Assert.Equal(1, state.FailedTools); Assert.Equal(1, state.BlockedTools);
        Assert.Equal("original-before", Assert.Single(state.Changes).Before); Assert.Equal("final-after", state.Changes.Single().After);
    }

    [Fact]
    public void More_than_512_file_changes_keep_exact_private_sidecar_after_json_reopen()
    {
        var events = new List<CursorActivityEvent>();
        for (var index = 0; index < 600; index++) events.Add(new("tool", Tool: new("edit-" + index, "Controlled edit", "edit", "completed", "",
            [new("fixture-" + index + ".txt", "original-" + index, "intermediate-" + index)])));
        events.Add(new("tool", Tool: new("final-zero", "Latest edit of old file", "edit", "completed", "", [new("fixture-0.txt", "intermediate-0", "final-zero")])));
        var artifactRoot = Path.Combine(_profile, "owned-change-sidecar");
        var final = CursorActivityRetention.Compact(events, "completed", artifactRoot);
        var summary = Assert.Single(final, item => item.Kind == "summary");
        Assert.Equal(600, summary.Summary!.ChangedFiles); Assert.True(summary.Summary.ChangeDetailsTruncated);
        Assert.True(summary.Changes!.Length <= 512);
        var archive = summary.ChangeArchive!;
        Assert.Equal(600, archive.Count);
        var all = CursorActivityRetention.AllChanges(archive, _profile).ToDictionary(change => change.Path);
        Assert.Equal(600, all.Count);
        Assert.Equal("original-0", all["fixture-0.txt"].Before); Assert.Equal("final-zero", all["fixture-0.txt"].After);
        Assert.Equal("original-599", all["fixture-599.txt"].Before); Assert.Equal("intermediate-599", all["fixture-599.txt"].After);
        var restored = JsonSerializer.Deserialize<CursorActivityEvent[]>(JsonSerializer.Serialize(final))!;
        using var state = new CursorTurnActivity(trustedRoot: _profile);
        foreach (var item in restored) state.Apply(item, TimeSpan.Zero);
        Assert.Equal(600, state.ChangedFiles); Assert.Equal(601, state.TotalTools);
        var reopened = CursorActivityRetention.AllChanges(state.ChangeArchive!, _profile).ToDictionary(change => change.Path);
        Assert.Equal(600, reopened.Count);
        Assert.Equal("original-0", reopened["fixture-0.txt"].Before); Assert.Equal("final-zero", reopened["fixture-0.txt"].After);
        Assert.Throws<ArgumentException>(() => CursorActivityRetention.AllChanges(archive, Path.Combine(_profile, "foreign-root")).ToArray());
    }

    [Fact]
    public void Private_spill_io_failure_is_nonfatal_bounded_and_disclosed_as_incomplete()
    {
        Directory.CreateDirectory(_profile);
        var blocker = Path.Combine(_profile, "file-instead-of-storage-folder"); File.WriteAllText(blocker, "Controlled IO denial fixture.");
        var rows = new Dictionary<string, CursorToolActivity>(); using var history = new CursorToolRetention(rows, 2);
        typeof(CursorToolRetention).GetField("_folder", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(history, blocker);
        var error = Record.Exception(() =>
        {
            for (var index = 0; index < 5; index++) history.Store(new("read-" + index, "Completed inspection", "read", "completed", "", []));
        });
        Assert.Null(error); Assert.True(rows.Count <= 2);
        Assert.NotNull(history.Summary.StorageError); Assert.True(history.Summary.CountsIncomplete);
        using var state = new CursorTurnActivity();
        state.Apply(new("summary", Summary: history.Summary, Changes: []), TimeSpan.Zero);
        Assert.Contains("could not be stored", state.ExecutionNotice);
        Assert.Contains("counts may be incomplete", state.ExecutionNotice);
        state.Finish("completed");
        Assert.Equal("completed", state.Result); Assert.NotNull(state.ToolSummary.StorageError);
    }

    [Fact]
    public void Display_retention_preserves_lifetime_failed_blocked_totals_and_earliest_before_latest_after()
    {
        var state = new CursorTurnActivity();
        state.Apply(new("tool", Tool: new("old-error", "Earlier failure", "execute", "failed", "", [])), TimeSpan.Zero);
        state.Apply(new("tool", Tool: new("old-blocked", "Earlier blocked command", "execute", "completed", "", [], "Not in allowlist: fixture")), TimeSpan.Zero);
        state.Apply(new("tool", Tool: new("first-edit", "Original edit", "edit", "completed", "", [new("shared-fixture.txt", "original-before", "intermediate-after")])), TimeSpan.Zero);
        state.Apply(new("tool", Tool: new("active", "Pending approval stays visible", "read", "pending", "captured input", [])), TimeSpan.Zero);
        for (var index = 0; index < 1100; index++) state.Apply(new("tool", Tool: new("read-" + index, "Completed read", "read", "completed", "", [])), TimeSpan.Zero);
        Assert.Contains(state.Tools, tool => tool.Id == "active" && tool.Status == "pending" && tool.Input == "captured input");
        state.Apply(new("tool", Tool: new("last-edit", "Final edit", "edit", "completed", "", [new("shared-fixture.txt", "intermediate-after", "final-after")])), TimeSpan.Zero);
        state.Apply(new("tool", Tool: new("late-error", "Later failure", "execute", "failed", "", [])), TimeSpan.Zero);
        state.Apply(new("tool", Tool: new("late-blocked", "Later blocked command", "execute", "completed", "", [], "Not in allowlist: fixture")), TimeSpan.Zero);
        Assert.Equal(2, state.FailedTools); Assert.Equal(2, state.BlockedTools);
        Assert.Contains("2 tool request(s) blocked", state.ExecutionNotice);
        var change = Assert.Single(state.Changes);
        Assert.Equal("original-before", change.Before); Assert.Equal("final-after", change.After);
        Assert.True(state.Tools.Count(tool => tool.Status is "completed" or "failed" or "blocked") <= 1000);
        Assert.Equal(1107, state.TotalTools);
        Assert.Equal(1102, state.CompletedTools);
        Assert.Equal(1, state.ActiveTools);
        Assert.True(state.CompactedTools > 0);
        state.Finish("completed");
        Assert.Equal("unconfirmed", state.Tools.Single(tool => tool.Id == "active").Status);
        Assert.Contains("unverified", state.Phase);
    }

    private void RoundTrip(List<CursorActivityEvent> observed, string answer)
    {
        var pending = new SavedChatInstruction("00000000000000000000000000000001", "Preserve my original heading; wait for explicit Send.", "fixture-model", [], true, true);
        var compacted = CursorActivityRetention.Compact(observed, "completed");
        Assert.True(compacted.Length <= 1100);
        var chat = ChatArchive.New(4, "controlled-fixture-workspace") with { Draft = "Unsent user draft remains unchanged", PendingInstructions = [pending],
            Turns = [new("Controlled original task", "fixture-model", answer, "completed", compacted)] };
        var store = new ChatArchive(_profile); store.Save(chat);
        var restored = Assert.Single(store.Load());
        Assert.Equal(chat.Draft, restored.Draft);
        var restoredPending = Assert.Single(restored.PendingInstructions!);
        Assert.Equal(pending.Id, restoredPending.Id); Assert.Equal(pending.Text, restoredPending.Text);
        Assert.Equal(pending.Model, restoredPending.Model); Assert.True(restoredPending.MayHaveBeenSent);
        Assert.Empty(restoredPending.Attachments);
        var queue = new ChatSteeringQueue(restored.PendingInstructions);
        Assert.False(queue.HasAutomaticPending); Assert.Empty(queue.BeginBatch());
        var restoredState = new CursorTurnActivity();
        foreach (var update in restored.Turns[0].Activity) restoredState.Apply(update, TimeSpan.Zero);
        var restoredChange = Assert.Single(restoredState.Changes);
        Assert.Equal("original-before", restoredChange.Before); Assert.Equal("final-after", restoredChange.After);
        var restoredChildren = restored.Turns[0].Activity.Where(item => item.Subagent is not null).GroupBy(item => item.Subagent!.SessionId).Select(group => group.Last().Subagent!).ToArray();
        var originalChildren = observed.Where(item => item.Subagent is not null).GroupBy(item => item.Subagent!.SessionId).Select(group => group.Last().Subagent!).ToArray();
        Assert.Equal(originalChildren.Length, restoredChildren.Length);
        foreach (var original in originalChildren) Assert.Equal(original.Output, restoredChildren.Single(child => child.SessionId == original.SessionId).Output);
        Assert.Equal(answer, restored.Turns[0].Reply);
    }

    public void Dispose() { if (Directory.Exists(_profile)) Directory.Delete(_profile, true); }
}
