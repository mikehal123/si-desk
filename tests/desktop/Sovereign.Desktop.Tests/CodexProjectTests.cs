using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Sovereign.Desktop.Client;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class CodexProjectTests
{
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static object Thread(string id, string? parent = null) => new { id, parentThreadId = parent, agentNickname = parent is null ? "" : "Backend", agentRole = "worker", preview = "Build API", turns = Array.Empty<object>() };
    private static string Notify(string method, object p) => JsonSerializer.Serialize(new { method, @params = p });
    private static string Completed(string thread = "main", string turn = "turn1") => Notify("turn/completed", new { threadId = thread, turn = new { id = turn, status = "completed" } });
    private static string Message(string thread = "main", string text = "Final answer") => Notify("item/agentMessage/delta", new { threadId = thread, turnId = thread == "main" ? "turn1" : "childturn", itemId = "answer", delta = text });

    [Theory]
    [InlineData(CursorProjectMode.Plan, CursorPermissionPolicy.AlwaysAllow, "never", "readOnly")]
    [InlineData(CursorProjectMode.Agent, CursorPermissionPolicy.AlwaysAsk, "untrusted", "workspaceWrite")]
    [InlineData(CursorProjectMode.Agent, CursorPermissionPolicy.AlwaysAllow, "never", "workspaceWrite")]
    public void Native_mode_permissions_and_sandbox_match_the_composer(CursorProjectMode mode, CursorPermissionPolicy permission, string approval, string sandbox)
    {
        var options = new CursorProjectOptions { Mode = mode, Permissions = permission };
        Assert.Equal(approval, CodexAppSession.ApprovalPolicy(options));
        var policy = Json(CodexAppSession.SandboxPolicy("project", options));
        Assert.Equal(sandbox, policy.GetProperty("type").GetString());
        Assert.Equal(mode == CursorProjectMode.Agent && permission == CursorPermissionPolicy.AlwaysAllow, policy.GetProperty("networkAccess").GetBoolean());
        if (sandbox == "workspaceWrite")
            Assert.Equal(new[] { "project" }, policy.GetProperty("writableRoots").EnumerateArray().Select(item => item.GetString()).ToArray());
        var flags = CodexChatClient.SessionArguments(options);
        Assert.Contains("agents.enabled=true", flags);
        Assert.Contains("features.shell_tool=true", flags);
        Assert.Contains("agents.max_concurrent_threads_per_session=3", flags);
        Assert.Contains("features.multi_agent_v2.max_concurrent_threads_per_session=4", flags);
        Assert.DoesNotContain("features.multi_agent_v2.enabled=true", flags);
        Assert.Contains("windows.sandbox=\"unelevated\"", flags);
        Assert.DoesNotContain("danger-full-access", flags);
    }

    [Fact]
    public void Ordinary_chat_stays_read_only_and_uses_private_auth_home_outside_project()
    {
        Assert.Contains("features.shell_tool=false", CodexChatClient.SessionArguments(null));
        Assert.Contains("agents.enabled=false", CodexChatClient.SessionArguments(null));
        Assert.Contains("features.multi_agent_v2.enabled=false", CodexChatClient.SessionArguments(null));
        Assert.Equal("never", CodexAppSession.ApprovalPolicy(null));
        var workspace = Path.Combine(Path.GetTempPath(), "project"); var profile = Path.Combine(Path.GetTempPath(), "profile");
        using var client = new CodexChatClient(workspace, new(), profile: profile);
        Assert.Equal(Path.Combine(profile, "chat-workspace", "codex-home"), client.PrivateHome);
        Assert.False(client.PrivateHome.StartsWith(workspace, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_opt_out_overrides_an_enabled_V2_user_default_in_process_and_thread_config(bool ordinaryChat)
    {
        var options = ordinaryChat ? null : new CursorProjectOptions { EnableSubagents = false };
        var flags = CodexChatClient.SessionArguments(options);
        Assert.Contains("features.multi_agent_v2.enabled=false", flags);
        var reader = new Reader(); var writer = new Writer(reader) { Tail = [Message(), Completed()] };
        using var client = new CodexChatClient(Path.GetTempPath(), reader, writer, options);
        await client.SendAsync("gpt-6.1-sol", "Reply", _ => { }, default).WaitAsync(TimeSpan.FromSeconds(5));
        var start = writer.Frames.Single(f => f.TryGetProperty("method", out var m) && m.GetString() == "thread/start");
        var effective = new Dictionary<string, object> { ["features.multi_agent_v2.enabled"] = true };
        foreach (var entry in start.GetProperty("params").GetProperty("config").EnumerateObject())
            effective[entry.Name] = entry.Value.ValueKind == JsonValueKind.Number ? entry.Value.GetInt32() : entry.Value.GetBoolean();
        Assert.Equal(false, effective["features.multi_agent_v2.enabled"]);
        Assert.Equal(false, effective["agents.enabled"]);
        Assert.Equal(4, effective["features.multi_agent_v2.max_concurrent_threads_per_session"]);
    }

    [Fact]
    public void Native_child_messages_reasoning_tools_and_terminal_state_stay_separate()
    {
        var observed = new List<CursorActivityEvent>(); var main = "";
        var state = new CodexProtocolState(Path.GetTempPath(), new(), observed.Add); state.AssignParent("main");
        state.Observe("thread/started", Json(new { thread = Thread("child", "main") }), x => main += x);
        state.Observe("turn/started", Json(new { threadId = "child", turn = new { id = "childturn" } }), x => main += x);
        state.Observe("item/reasoning/summaryTextDelta", Json(new { threadId = "child", itemId = "thought", delta = "Inspecting API" }), x => main += x);
        state.Observe("item/agentMessage/delta", Json(new { threadId = "child", itemId = "answer", delta = "Helper result" }), x => main += x);
        state.Observe("item/started", Json(new { threadId = "child", item = new { type = "commandExecution", id = "tool", command = "dotnet test", commandActions = Array.Empty<object>(), status = "inProgress" } }), x => main += x);
        state.Observe("item/commandExecution/outputDelta", Json(new { threadId = "child", itemId = "tool", delta = "Passed 3 tests" }), x => main += x);
        state.Observe("item/completed", Json(new { threadId = "child", item = new { type = "commandExecution", id = "tool", command = "dotnet test", commandActions = Array.Empty<object>(), status = "completed", aggregatedOutput = "Passed 3 tests" } }), x => main += x);
        state.Observe("turn/completed", Json(new { threadId = "child", turn = new { id = "childturn", status = "completed" } }), x => main += x);
        var child = observed.Last().Subagent!;
        Assert.Equal("", main); Assert.Equal("Backend", child.Name); Assert.Equal("Helper result", child.Output); Assert.Equal("Inspecting API", child.Reasoning);
        Assert.Equal("completed", child.State); Assert.Equal("child:tool", child.Tool!.Id); Assert.Equal("Passed 3 tests", child.Tool.Output);
        Assert.DoesNotContain(observed, e => e.Kind is "tool" or "reasoning" or "message");
        Assert.Empty(state.ActiveTurns());
    }

    [Fact]
    public void Native_subagent_activity_registers_before_child_turn_and_keeps_task_path_label()
    {
        var observed = new List<CursorActivityEvent>(); var subscriptions = new List<string>(); var output = "";
        var state = new CodexProtocolState(Path.GetTempPath(), new(), observed.Add) { ChildSpawned = subscriptions.Add }; state.AssignParent("main");
        state.Observe("turn/started", Json(new { threadId = "main", turn = new { id = "parentturn" } }), _ => { });
        var spawn = Json(new { threadId = "main", turnId = "parentturn", item = new { type = "subAgentActivity", id = "call_spawn", kind = "started", agentThreadId = "child", agentPath = "/root/backend" } });
        state.Observe("item/started", spawn, _ => { });
        state.Observe("turn/started", Json(new { threadId = "child", turn = new { id = "childturn" } }), _ => { });
        state.Observe("item/started", Json(new { threadId = "child", turnId = "childturn", item = new { type = "commandExecution", id = "read", command = "Get-Content fixture.txt", commandActions = new[] { new { type = "read" } }, status = "inProgress" } }), _ => { });
        state.Observe("item/agentMessage/delta", Json(new { threadId = "child", turnId = "childturn", itemId = "answer", delta = "Helper result" }), x => output += x);
        state.Hydrate(Json(new { thread = Thread("child", "main") }));
        state.Observe("turn/completed", Json(new { threadId = "child", turn = new { id = "childturn", status = "completed" } }), _ => { });
        state.Observe("item/completed", spawn, _ => { });
        var child = observed.Last().Subagent!;
        Assert.Equal(new[] { "child" }, subscriptions); Assert.Equal("backend", child.Name);
        Assert.Equal("completed", child.State); Assert.Equal("Helper result", child.Output); Assert.Equal("child:read", child.Tool!.Id); Assert.Equal("", output);
    }

    [Fact]
    public void Native_subagent_terminal_activity_settles_without_a_separate_child_turn_event()
    {
        var observed = new List<CursorActivityEvent>(); var state = new CodexProtocolState(Path.GetTempPath(), new(), observed.Add); state.AssignParent("main");
        state.Observe("item/started", Json(new { threadId = "main", item = new { type = "subAgentActivity", id = "spawn", kind = "started", agentThreadId = "child", agentPath = "/root/qa" } }), _ => { });
        state.Observe("item/completed", Json(new { threadId = "main", item = new { type = "subAgentActivity", id = "done", kind = "completed", agentThreadId = "child", agentPath = "/root/qa" } }), _ => { });
        Assert.Equal("completed", observed.Last().Subagent!.State);
        Assert.True(state.WaitForChildrenAsync(default).IsCompleted);
        Assert.Throws<IOException>(() => state.Observe("item/started", Json(new { threadId = "child", item = new { type = "subAgentActivity", id = "nested", kind = "started", agentThreadId = "nested", agentPath = "/root/qa/nested" } }), _ => { }));
    }

    [Theory]
    [InlineData("00000000-0000-7000-8000-000000000001", "00000000-0000-7000-8000-000000000002", "00000000-0000-7000-8000-000000000003")]
    [InlineData("00000000-0000-7000-8000-000000000004", "00000000-0000-7000-8000-000000000005", "00000000-0000-7000-8000-000000000006")]
    public void Recorded_direct_helper_report_to_root_is_interaction_not_nested_spawn(string parent, string helper, string turn)
    {
        // Native field shape/order from two failed 0.160.0 historical rollouts;
        // identities are anonymous and private messages are not replayed.
        var observed = new List<CursorActivityEvent>(); var subscriptions = new List<string>(); var answer = "";
        var state = new CodexProtocolState(Path.GetTempPath(), new(), observed.Add) { ChildSpawned = subscriptions.Add };
        state.AssignParent(parent);
        state.Observe("thread/started", Json(new { thread = Thread(helper, parent) }), _ => { });
        state.Observe("turn/started", Json(new { threadId = helper, turn = new { id = turn } }), _ => { });
        var report = Json(new { threadId = helper, turnId = turn, item = new { type = "subAgentActivity", id = "report", kind = "interacted", agentThreadId = parent, agentPath = "/root" } });
        state.Observe("item/started", report, _ => { });
        state.Observe("item/completed", report, _ => { });
        Assert.False(state.WaitForChildrenAsync(default).IsCompleted);
        Assert.Equal(new[] { helper }, subscriptions);
        Assert.DoesNotContain(observed, e => e.Subagent?.SessionId == parent);
        Assert.Equal("Reporting to parent", observed.Last().Subagent!.CurrentWork);
        state.Observe("item/agentMessage/delta", Json(new { threadId = helper, turnId = turn, itemId = "answer", delta = "Helper result" }), x => answer += x);
        state.Observe("turn/completed", Json(new { threadId = helper, turn = new { id = turn, status = "completed" } }), _ => { });
        Assert.Equal("", answer);
        Assert.Equal("Helper result", observed.Last().Subagent!.Output);
        Assert.Equal("completed", observed.Last().Subagent!.State);
        Assert.True(state.WaitForChildrenAsync(default).IsCompleted);
    }

    [Fact]
    public void Direct_known_ancestry_allows_namespaced_paths_but_unknown_nested_and_foreign_targets_stay_denied()
    {
        var observed = new List<CursorActivityEvent>(); var subscriptions = new List<string>();
        var state = new CodexProtocolState(Path.GetTempPath(), new(), observed.Add) { ChildSpawned = subscriptions.Add }; state.AssignParent("main");
        state.Observe("thread/started", Json(new { thread = new { id = "child", parentThreadId = "main", source = new { subAgent = new { thread_spawn = new { parent_thread_id = "main", depth = 1 } } } } }), _ => { });
        state.Observe("item/completed", Json(new { threadId = "main", item = new { type = "subAgentActivity", id = "known", kind = "started", agentThreadId = "child", agentPath = "/workspace/project/root/backend" } }), _ => { });
        Assert.Equal(new[] { "child" }, subscriptions);
        Assert.Equal("backend", observed.Last().Subagent!.Name);
        Assert.Throws<IOException>(() => state.Observe("item/started", Json(new { threadId = "main", item = new { type = "subAgentActivity", id = "new", kind = "started", agentThreadId = "unknown", agentPath = "/workspace/project/root/nested" } }), _ => { }));
        Assert.Throws<IOException>(() => state.Observe("item/completed", Json(new { threadId = "child", item = new { type = "subAgentActivity", id = "foreign", kind = "interacted", agentThreadId = "foreign", agentPath = "/foreign" } }), _ => { }));
        Assert.Throws<IOException>(() => state.Observe("item/completed", Json(new { threadId = "child", item = new { type = "subAgentActivity", id = "spawnParent", kind = "started", agentThreadId = "main", agentPath = "/root" } }), _ => { }));
        Assert.Throws<IOException>(() => state.Observe("item/completed", Json(new { threadId = "main", item = new { type = "collabAgentToolCall", id = "nested", tool = "spawnAgent", senderThreadId = "child", receiverThreadIds = new[] { "grandchild" }, agentsStates = new { } } }), _ => { }));
        Assert.False(state.Owns(Json(new { threadId = "foreign", turnId = "anything" })));
    }

    [Fact]
    public void Child_model_is_runtime_reported_and_active_reroute_is_not_overwritten_by_configuration_hydration()
    {
        var observed = new List<CursorActivityEvent>(); var state = new CodexProtocolState(Path.GetTempPath(), new(), observed.Add); state.AssignParent("main");
        state.Observe("item/completed", Json(new { threadId = "main", item = new { type = "collabAgentToolCall", id = "spawn", tool = "spawnAgent", model = "requested-only-model", receiverThreadIds = new[] { "child" }, agentsStates = new { } } }), _ => { });
        Assert.Null(observed.Last().Subagent!.ModelId);
        state.Hydrate(Json(new { thread = Thread("child", "main"), model = "gpt-6-sol", modelProvider = "openai" }));
        Assert.Null(observed.Last().Subagent!.ModelId);
        Assert.Equal("gpt-6-sol", observed.Last().Subagent!.RequestedModelId);
        Assert.Equal("runtime configuration", observed.Last().Subagent!.RequestedModelSource);
        state.Observe("turn/started", Json(new { threadId = "child", turn = new { id = "active" } }), _ => { });
        state.Observe("model/rerouted", Json(new { threadId = "child", turnId = "stale", toModel = "stale-model" }), _ => { });
        state.Observe("model/rerouted", Json(new { threadId = "foreign", turnId = "active", toModel = "foreign-model" }), _ => { });
        Assert.Null(observed.Last().Subagent!.ModelId);
        state.Observe("model/rerouted", Json(new { threadId = "child", turnId = "active", fromModel = "gpt-6-sol", toModel = "gpt-6.1-sol", reason = "highRisk" }), _ => { });
        state.Hydrate(Json(new { thread = new { id = "child", parentThreadId = "main", model = "gpt-6-sol", modelProvider = "openai" }, model = "gpt-6-sol", modelProvider = "openai" }));
        Assert.Equal("gpt-6.1-sol", observed.Last().Subagent!.ModelId);
        Assert.Equal("runtime reroute", observed.Last().Subagent!.ModelSource);
        Assert.Equal("gpt-6-sol", observed.Last().Subagent!.RequestedModelId);
        state.Observe("turn/completed", Json(new { threadId = "child", turn = new { id = "active", status = "completed" } }), _ => { });
        state.Observe("turn/started", Json(new { threadId = "child", turn = new { id = "next" } }), _ => { });
        Assert.Null(observed.Last().Subagent!.ModelId);
        Assert.Null(observed.Last().Subagent!.ModelSource);
        Assert.Equal("gpt-6-sol", observed.Last().Subagent!.RequestedModelId);
    }

    [Fact]
    public void Parent_completed_snapshot_before_child_final_message_preserves_result_and_terminal_clock_on_reopen()
    {
        var events = new List<CursorActivityEvent>(); var answer = "";
        var state = new CodexProtocolState(Path.GetTempPath(), new(), events.Add); state.AssignParent("main");
        state.Observe("thread/started", Json(new { thread = Thread("child", "main") }), _ => { });
        state.Observe("turn/started", Json(new { threadId = "child", turn = new { id = "owned" } }), _ => { });
        state.Observe("item/agentMessage/delta", Json(new { threadId = "child", turnId = "owned", itemId = "final", delta = "Partial " }), _ => { });
        state.Observe("item/completed", Json(new { threadId = "main", item = new { type = "collabAgentToolCall", id = "wait", tool = "wait", receiverThreadIds = new[] { "child" },
            agentsStates = new Dictionary<string, object> { ["child"] = new { status = "completed" } } } }), _ => { });
        var stopped = events.Last().Subagent!;
        state.Observe("item/agentMessage/delta", Json(new { threadId = "child", turnId = "owned", itemId = "final", delta = "RESULT" }), x => answer += x);
        state.Observe("item/completed", Json(new { threadId = "child", turnId = "owned", item = new { type = "agentMessage", id = "final", text = "Partial RESULT." } }), x => answer += x);
        var final = events.Last().Subagent!;
        Assert.Equal("Partial RESULT.", final.Output);
        Assert.Equal("completed", final.State);
        Assert.Equal(stopped.CompletedAt, final.CompletedAt);
        Assert.Equal(stopped.UpdatedAt, final.UpdatedAt);
        Assert.Equal("", answer);
        var saved = new SavedChatTurn("Fixture", "parent-model", "Parent continued", "completed", events.ToArray());
        var reopened = JsonSerializer.Deserialize<SavedChatTurn>(JsonSerializer.Serialize(saved))!.Activity.Last(e => e.Subagent is not null).Subagent!;
        Assert.Equal(JsonSerializer.Serialize(final), JsonSerializer.Serialize(reopened));
        state.Observe("turn/started", Json(new { threadId = "child", turn = new { id = "next" } }), _ => { });
        state.Observe("item/completed", Json(new { threadId = "child", turnId = "owned", item = new { type = "agentMessage", id = "stale", text = "Old injected output" } }), _ => { });
        state.Observe("item/agentMessage/delta", Json(new { threadId = "foreign", turnId = "next", itemId = "foreign", delta = "Foreign" }), _ => { });
        Assert.Equal("", events.Last().Subagent!.Output);
        Assert.Equal("running", events.Last().Subagent!.State);
    }

    [Fact]
    public void Subscription_hydrates_latest_completed_helper_result_without_replaying_parent_or_stale_turns()
    {
        var events = new List<CursorActivityEvent>(); var state = new CodexProtocolState(Path.GetTempPath(), new(), events.Add); state.AssignParent("main");
        state.Observe("thread/started", Json(new { thread = Thread("child", "main") }), _ => { });
        var snapshot = Json(new { thread = new { id = "child", parentThreadId = "main", turns = new[] {
            new { id = "old", status = "completed", items = new[] { new { type = "agentMessage", id = "old-answer", text = "Older result" } } },
            new { id = "latest", status = "completed", items = new[] { new { type = "agentMessage", id = "latest-answer", text = "Hydrated final RESULT" } } } } },
            model = "gpt-6.1-sol", modelProvider = "openai" });
        state.Hydrate(snapshot, "child");
        var final = events.Last().Subagent!;
        Assert.Equal("Hydrated final RESULT", final.Output);
        Assert.Equal("completed", final.State);
        Assert.Null(final.ModelId);
        Assert.Equal("gpt-6.1-sol", final.RequestedModelId);
        Assert.True(state.WaitForChildrenAsync(default).IsCompleted);
        state.Hydrate(snapshot, "child");
        Assert.Equal(final.CompletedAt, events.Last().Subagent!.CompletedAt);
        Assert.False(state.Owns(Json(new { threadId = "child", turnId = "latest" })));
        Assert.Throws<IOException>(() => state.Hydrate(Json(new { thread = Thread("foreign", "main") }), "child"));
        state.Observe("turn/started", Json(new { threadId = "child", turn = new { id = "next" } }), _ => { });
        state.Hydrate(snapshot, "child");
        Assert.Equal("running", events.Last().Subagent!.State);
        Assert.Equal("", events.Last().Subagent!.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Subscription_rejects_known_child_snapshot_with_foreign_ancestry_without_mutation(bool explicitParent)
    {
        var events = new List<CursorActivityEvent>(); var state = new CodexProtocolState(Path.GetTempPath(), new(), events.Add); state.AssignParent("main");
        state.Observe("thread/started", Json(new { thread = Thread("child", "main") }), _ => { });
        state.Observe("turn/started", Json(new { threadId = "child", turn = new { id = "owned" } }), _ => { });
        var before = events.Last().Subagent!; var count = events.Count;
        var snapshot = Json(new { thread = new { id = "child", parentThreadId = explicitParent ? "foreign" : null,
            source = new { subAgent = new { thread_spawn = new { parent_thread_id = "foreign", depth = 2 } } },
            turns = new[] { new { id = "owned", status = "completed", items = new[] { new { type = "agentMessage", id = "foreign-result", text = "Injected result" } } } } },
            model = "foreign-model", modelProvider = "foreign-provider" });
        state.Hydrate(snapshot, "child");
        Assert.Equal(count, events.Count);
        Assert.Equal(before, events.Last().Subagent);
        Assert.True(state.Owns(Json(new { threadId = "child", turnId = "owned" })));
        Assert.False(state.WaitForChildrenAsync(default).IsCompleted);
        state.Hydrate(Json(new { thread = new { id = "child", parentThreadId = "main",
            source = new { subAgent = new { thread_spawn = new { parent_thread_id = "foreign", depth = 2 } } },
            turns = new[] { new { id = "owned", status = "completed", items = new[] { new { type = "agentMessage", id = "conflicting-result", text = "Injected result" } } } } } }), "child");
        Assert.Equal(count, events.Count);
        Assert.Equal(before, events.Last().Subagent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Child_runtime_error_and_partial_final_result_stay_failed_without_inventing_backend_cause(bool turnError)
    {
        var events = new List<CursorActivityEvent>(); var state = new CodexProtocolState(Path.GetTempPath(), new(), events.Add); state.AssignParent("main");
        state.Observe("thread/started", Json(new { thread = Thread("child", "main") }), _ => { });
        state.Observe("turn/started", Json(new { threadId = "child", turn = new { id = "owned" } }), _ => { });
        if (turnError) state.Observe("turn/completed", Json(new { threadId = "child", turn = new { id = "owned", status = "failed", error = new { message = "Actual helper rate limit reached." } } }), _ => { });
        else state.Observe("error", Json(new { threadId = "child", turnId = "owned", willRetry = false, error = new { message = "Actual helper rate limit reached." } }), _ => { });
        var failed = events.Last().Subagent!;
        state.Observe("item/completed", Json(new { threadId = "child", turnId = "owned", item = new { type = "agentMessage", id = "final", text = "Partial actual result" } }), _ => { });
        var final = events.Last().Subagent!;
        Assert.Equal("failed", final.State);
        Assert.Equal("Actual helper rate limit reached.", final.Failure);
        Assert.Equal("Partial actual result", final.Output);
        Assert.Equal(failed.CompletedAt, final.CompletedAt);
        Assert.Equal(failed.UpdatedAt, final.UpdatedAt);
    }

    [Fact]
    public void Parent_transport_failure_records_actual_detail_and_late_result_remains_unconfirmed()
    {
        var events = new List<CursorActivityEvent>(); var state = new CodexProtocolState(Path.GetTempPath(), new(), events.Add); state.AssignParent("main");
        state.Observe("thread/started", Json(new { thread = Thread("child", "main") }), _ => { });
        state.Observe("turn/started", Json(new { threadId = "child", turn = new { id = "owned" } }), _ => { });
        state.Finish("Connection interrupted · completion unconfirmed", "Actual transport EOF while helper running.");
        state.Observe("item/completed", Json(new { threadId = "child", turnId = "owned", item = new { type = "agentMessage", id = "final", text = "Saved partial output" } }), _ => { });
        var child = events.Last().Subagent!;
        Assert.Equal("disconnected", child.State);
        Assert.Equal("Actual transport EOF while helper running.", child.Failure);
        Assert.Equal("Saved partial output", child.Output);
    }

    [Fact]
    public void File_change_keeps_the_native_patch_without_manufacturing_file_contents()
    {
        var observed = new List<CursorActivityEvent>(); var state = new CodexProtocolState(Path.GetTempPath(), new(), observed.Add); state.AssignParent("main");
        state.Observe("item/completed", Json(new { threadId = "main", item = new { type = "fileChange", id = "edit", status = "completed",
            changes = new[] { new { path = "answer.txt", diff = "@@ -1 +1 @@\n-before\n+after", kind = new { type = "update" } } } } }), _ => { });
        var change = Assert.Single(Assert.Single(observed).Tool!.Changes);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "answer.txt"), change.Path);
        Assert.Null(change.Before); Assert.Equal("", change.After); Assert.Contains("+after", change.UnifiedDiff);
    }

    [Fact]
    public void Native_collaboration_lifecycle_registers_direct_helpers_and_rejects_nested_or_disabled_spawns()
    {
        var observed = new List<CursorActivityEvent>(); var state = new CodexProtocolState(Path.GetTempPath(), new(), observed.Add); state.AssignParent("main");
        var collab = new { threadId = "main", item = new { type = "collabAgentToolCall", id = "spawn", tool = "spawnAgent", status = "completed",
            senderThreadId = "main", receiverThreadIds = new[] { "child" }, prompt = "Inspect API", agentsStates = new Dictionary<string, object> { ["child"] = new { status = "running" } } } };
        state.Observe("item/completed", Json(collab), _ => { });
        Assert.Equal("Inspect API", observed.Last().Subagent!.Task);
        state.Observe("item/completed", Json(new { threadId = "main", item = new { type = "collabAgentToolCall", id = "wait", tool = "wait", status = "completed",
            senderThreadId = "main", receiverThreadIds = new[] { "child" }, agentsStates = new Dictionary<string, object> { ["child"] = new { status = "completed", message = "Found issue" } } } }), _ => { });
        Assert.Equal("completed", observed.Last().Subagent!.State); Assert.Equal("Found issue", observed.Last().Subagent!.Output);
        Assert.Throws<IOException>(() => state.Observe("thread/started", Json(new { thread = Thread("nested", "child") }), _ => { }));
        var disabled = new CodexProtocolState(Path.GetTempPath(), new() { EnableSubagents = false }, _ => { }); disabled.AssignParent("main");
        Assert.Throws<IOException>(() => disabled.Observe("item/completed", Json(collab), _ => { }));
    }

    [Fact]
    public void Fast_completed_helper_can_receive_authoritative_result_after_subscription_missed_the_stream()
    {
        var observed = new List<CursorActivityEvent>(); var state = new CodexProtocolState(Path.GetTempPath(), new(), observed.Add); state.AssignParent("main");
        state.Observe("thread/started", Json(new { thread = Thread("child", "main") }), _ => { });
        state.Observe("turn/completed", Json(new { threadId = "child", turn = new { id = "a", status = "completed" } }), _ => { });
        state.Observe("item/completed", Json(new { threadId = "main", item = new { type = "collabAgentToolCall", id = "wait", tool = "wait", receiverThreadIds = new[] { "child" },
            agentsStates = new Dictionary<string, object> { ["child"] = new { status = "completed", message = "Authoritative result" } } } }), _ => { });
        Assert.Equal("completed", observed.Last().Subagent!.State); Assert.Equal("Authoritative result", observed.Last().Subagent!.Output);
    }

    [Fact]
    public void Native_shutdown_followed_by_turn_interrupted_does_not_change_terminal_state_or_fail_the_parent()
    {
        var observed = new List<CursorActivityEvent>(); var state = new CodexProtocolState(Path.GetTempPath(), new(), observed.Add); state.AssignParent("main");
        state.Observe("thread/started", Json(new { thread = Thread("child", "main") }), _ => { });
        state.Observe("item/completed", Json(new { threadId = "main", item = new { type = "collabAgentToolCall", id = "close", tool = "closeAgent", receiverThreadIds = new[] { "child" },
            agentsStates = new Dictionary<string, object> { ["child"] = new { status = "shutdown" } } } }), _ => { });
        state.Observe("turn/completed", Json(new { threadId = "child", turn = new { id = "a", status = "interrupted" } }), _ => { });
        Assert.Equal("disconnected", observed.Last().Subagent!.State);
    }

    [Fact]
    public void Spoofed_thread_output_and_wrong_turn_approval_do_not_enter_the_active_conversation()
    {
        var output = ""; var state = new CodexProtocolState(Path.GetTempPath(), new(), _ => { }); state.AssignParent("main");
        state.Observe("turn/started", Json(new { threadId = "main", turn = new { id = "actual" } }), _ => { });
        state.Observe("item/agentMessage/delta", Json(new { threadId = "foreign", delta = "Injected" }), x => output += x);
        Assert.Equal("", output);
        Assert.False(state.Owns(Json(new { threadId = "main", turnId = "wrong" })));
        Assert.True(state.Owns(Json(new { threadId = "main", turnId = "actual" })));
    }

    [Fact]
    public void Completed_message_fills_unstreamed_suffix_without_duplicating_text()
    {
        var text = ""; var state = new CodexProtocolState(Path.GetTempPath(), null, _ => { }); state.AssignParent("main");
        state.Observe("item/agentMessage/delta", Json(new { threadId = "main", itemId = "a", delta = "Hello" }), x => text += x);
        state.Observe("item/completed", Json(new { threadId = "main", item = new { type = "agentMessage", id = "a", text = "Hello world" } }), x => text += x);
        Assert.Equal("Hello world", text);
    }

    [Fact]
    public void Separate_parent_message_items_have_a_paragraph_break_and_reset_each_turn()
    {
        var text = ""; var state = new CodexProtocolState(Path.GetTempPath(), null, _ => { }); state.AssignParent("main");
        state.Observe("turn/started", Json(new { threadId = "main", turn = new { id = "a" } }), _ => { });
        state.Observe("item/agentMessage/delta", Json(new { threadId = "main", itemId = "commentary", delta = "I'll delegate." }), x => text += x);
        state.Observe("item/completed", Json(new { threadId = "main", item = new { type = "agentMessage", id = "commentary", text = "I'll delegate." } }), x => text += x);
        state.Observe("item/agentMessage/delta", Json(new { threadId = "main", itemId = "final", delta = "All three completed." }), x => text += x);
        state.Observe("item/completed", Json(new { threadId = "main", item = new { type = "agentMessage", id = "final", text = "All three completed." } }), x => text += x);
        Assert.Equal("I'll delegate.\n\nAll three completed.", text);
        text = "";
        state.Observe("turn/started", Json(new { threadId = "main", turn = new { id = "b" } }), _ => { });
        state.Observe("item/completed", Json(new { threadId = "main", item = new { type = "agentMessage", id = "next", text = "Next reply." } }), x => text += x);
        Assert.Equal("Next reply.", text);
    }

    [Fact]
    public void Stale_turn_messages_and_tools_do_not_change_the_new_turn()
    {
        var text = ""; var observed = new List<CursorActivityEvent>(); var state = new CodexProtocolState(Path.GetTempPath(), new(), observed.Add); state.AssignParent("main");
        state.Observe("turn/started", Json(new { threadId = "main", turn = new { id = "new" } }), _ => { });
        state.Observe("item/agentMessage/delta", Json(new { threadId = "main", turnId = "old", itemId = "a", delta = "Stale" }), x => text += x);
        state.Observe("item/completed", Json(new { threadId = "main", turnId = "old", item = new { type = "fileChange", id = "t", status = "completed", changes = Array.Empty<object>() } }), _ => { });
        Assert.Empty(observed); Assert.Equal("", text);
    }

    [Fact]
    public async Task Native_child_completion_between_followup_started_and_completed_is_not_reopened()
    {
        var observed = new List<CursorActivityEvent>(); var state = new CodexProtocolState(Path.GetTempPath(), new(), observed.Add); state.AssignParent("main");
        state.Observe("thread/started", Json(new { thread = Thread("child", "main") }), _ => { });
        state.Observe("turn/started", Json(new { threadId = "child", turn = new { id = "a" } }), _ => { });
        state.Observe("turn/completed", Json(new { threadId = "child", turn = new { id = "a", status = "completed" } }), _ => { });
        var follow = Json(new { threadId = "main", item = new { type = "collabAgentToolCall", id = "follow", tool = "followupTask", receiverThreadIds = new[] { "child" }, agentsStates = new { }, prompt = "Follow up" } });
        state.Observe("item/started", follow, _ => { });
        state.Observe("turn/started", Json(new { threadId = "child", turn = new { id = "b" } }), _ => { });
        state.Observe("turn/completed", Json(new { threadId = "child", turn = new { id = "b", status = "completed" } }), _ => { });
        state.Observe("item/completed", follow, _ => { });
        Assert.Equal("completed", observed.Last().Subagent!.State);
        await state.WaitForChildrenAsync(default).WaitAsync(TimeSpan.FromSeconds(1));
        state.Hydrate(Json(new { thread = new { id = "child", parentThreadId = "main", preview = "Followup", turns = new[] { new { id = "old", status = "inProgress" } } } }));
        Assert.Empty(state.ActiveTurns());
    }

    [Fact]
    public void Approval_callback_specific_scope_is_shown_and_decisions_respect_native_options()
    {
        var state = new CodexProtocolState(Path.GetTempPath(), new(), _ => { }); state.AssignParent("main");
        state.Observe("item/started", Json(new { threadId = "main", item = new { type = "commandExecution", id = "t", command = "prior", status = "inProgress", commandActions = Array.Empty<object>() } }), _ => { });
        var request = state.Permission(Json(new { threadId = "main", itemId = "t", kind = "writeStdin", command = "sendpassword", reason = "Interactive input", availableDecisions = new[] { "decline", "cancel" } }), false);
        Assert.Equal("Write to running command", request.Tool.Title);
        Assert.Contains("Interactive input", request.Tool.Input); Assert.Contains("sendpassword", request.Tool.Input);
        Assert.DoesNotContain(request.Options, o => o.Id == "accept");
    }

    private sealed class Reader : TextReader
    {
        private readonly Channel<string> _frames = Channel.CreateUnbounded<string>();
        public void Feed(params string[] frames) { foreach (var frame in frames) _frames.Writer.TryWrite(frame); }
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) => await _frames.Reader.ReadAsync(cancellationToken);
    }
    private sealed class Writer(Reader reader) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public readonly ConcurrentQueue<JsonElement> Frames = new();
        public Action<JsonElement>? OnWrite { get; set; }
        public string[] Tail { get; set; } = [];
        public object? ResumeResult { get; set; }
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            using var document = JsonDocument.Parse(buffer.ToString()); var frame = document.RootElement.Clone(); Frames.Enqueue(frame); OnWrite?.Invoke(frame);
            if (!frame.TryGetProperty("method", out var method) || !frame.TryGetProperty("id", out var id)) return Task.CompletedTask;
            object result = method.GetString() switch {
                "account/read" => new { account = new { type = "chatgpt" } },
                "model/list" => new { data = new[] { new { model = "gpt-6.1-sol", hidden = false } }, nextCursor = (string?)null },
                "thread/start" => new { thread = Thread("main") },
                "turn/start" => new { turn = new { id = "turn1", status = "inProgress" } },
                "thread/resume" => ResumeResult ?? new { thread = Thread(frame.GetProperty("params").GetProperty("threadId").GetString()!, "main") },
                _ => new { } };
            if (method.GetString() == "turn/start") reader.Feed(Notify("turn/started", new { threadId = "main", turn = new { id = "turn1", status = "inProgress" } }));
            reader.Feed(JsonSerializer.Serialize(new { id = id.Clone(), result }));
            if (method.GetString() == "turn/start") reader.Feed(Tail);
            return Task.CompletedTask;
        }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private static string Approval(string thread = "main", string method = "item/commandExecution/requestApproval") => JsonSerializer.Serialize(new {
        id = "approval", method, @params = new { threadId = thread, turnId = thread == "main" ? "turn1" : "childturn", itemId = "tool", command = "dotnet test" } });

    [Fact]
    public async Task Models_initializes_transport_and_preserves_the_advertised_exact_model()
    {
        var reader = new Reader(); var writer = new Writer(reader); using var client = new CodexChatClient(Path.GetTempPath(), reader, writer);
        Assert.Equal(new[] { "gpt-6.1-sol" }, await client.ModelsAsync(default));
        Assert.Equal("initialize", writer.Frames.First().GetProperty("method").GetString());
        Assert.Equal(DesktopClientVersion.Current, writer.Frames.First().GetProperty("params").GetProperty("clientInfo").GetProperty("version").GetString());
        Assert.NotEqual("0.3.13", DesktopClientVersion.Current);
    }

    [Theory]
    [InlineData(CursorProjectMode.Plan, CursorPermissionPolicy.AlwaysAllow, "cancel", false)]
    [InlineData(CursorProjectMode.Agent, CursorPermissionPolicy.AlwaysAsk, "accept", true)]
    [InlineData(CursorProjectMode.Agent, CursorPermissionPolicy.AlwaysAllow, "accept", false)]
    public async Task Approval_responses_apply_mode_and_permission_policy(CursorProjectMode mode, CursorPermissionPolicy permission, string expected, bool asks)
    {
        var prompted = false; var reader = new Reader(); var writer = new Writer(reader);
        writer.Tail = [Approval()];
        writer.OnWrite = frame => { if (frame.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            reader.Feed(Message(), Completed()); };
        using var client = new CodexChatClient(Path.GetTempPath(), reader, writer, new() { Mode = mode, Permissions = permission,
            RequestPermission = (_, _) => { prompted = true; return Task.FromResult<string?>("accept"); } });
        await client.SendAsync("gpt-6.1-sol", "Implement", _ => { }, default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(asks, prompted);
        var response = writer.Frames.Single(f => f.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String);
        Assert.Equal(expected, response.GetProperty("result").GetProperty("decision").GetString());
        var start = writer.Frames.Single(f => f.TryGetProperty("method", out var method) && method.GetString() == "turn/start");
        Assert.Equal(mode == CursorProjectMode.Plan ? "plan" : "default", start.GetProperty("params").GetProperty("collaborationMode").GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Child_approval_is_attributed_and_rejected_after_cancellation()
    {
        using var cancel = new CancellationTokenSource(); var reader = new Reader(); var writer = new Writer(reader); CursorPermissionRequest? observed = null;
        writer.Tail = [Notify("thread/started", new { thread = Thread("child", "main") }),
            Notify("turn/started", new { threadId = "child", turn = new { id = "childturn", status = "inProgress" } }), Approval("child")];
        using var client = new CodexChatClient(Path.GetTempPath(), reader, writer, new() { RequestPermission = (request, token) => {
            observed = request; cancel.Cancel(); return Task.FromCanceled<string?>(token); } });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync("gpt-6.1-sol", "Implement", _ => { }, cancel.Token));
        Assert.Equal("child", observed!.Subagent!.SessionId);
        Assert.DoesNotContain(writer.Frames, f => f.TryGetProperty("result", out var r) && r.TryGetProperty("decision", out var d) && d.GetString() == "accept");
        var interrupts = writer.Frames.Where(f => f.TryGetProperty("method", out var m) && m.GetString() == "turn/interrupt").ToArray();
        Assert.Contains(interrupts, f => f.GetProperty("params").GetProperty("threadId").GetString() == "main");
        Assert.Contains(interrupts, f => f.GetProperty("params").GetProperty("threadId").GetString() == "child");
    }

    [Fact]
    public async Task Parent_completion_waits_for_native_child_results_and_tools()
    {
        var reader = new Reader(); var writer = new Writer(reader); var observed = new List<CursorActivityEvent>();
        var parentEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        writer.Tail = [Notify("thread/started", new { thread = Thread("child", "main") }),
            Notify("turn/started", new { threadId = "child", turn = new { id = "childturn", status = "inProgress" } }), Message(), Completed()];
        using var client = new CodexChatClient(Path.GetTempPath(), reader, writer, new(), e => { observed.Add(e); });
        var send = client.SendAsync("gpt-6.1-sol", "Delegate", _ => parentEnded.TrySetResult(), default);
        await parentEnded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Yield(); Assert.False(send.IsCompleted);
        reader.Feed(Message("child", "Helper complete"), Completed("child", "childturn"));
        await send.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("completed", observed.Last(e => e.Subagent is not null).Subagent!.State);
        Assert.Equal("Helper complete", observed.Last(e => e.Subagent is not null).Subagent!.Output);
    }

    [Fact]
    public async Task Native_helper_report_to_root_does_not_disconnect_transport_or_lose_final_results()
    {
        var reader = new Reader(); var writer = new Writer(reader); var observed = new ConcurrentQueue<CursorActivityEvent>(); var answer = "";
        writer.Tail = [Notify("thread/started", new { thread = Thread("child", "main") }),
            Notify("turn/started", new { threadId = "child", turn = new { id = "childturn", status = "inProgress" } }),
            Notify("item/completed", new { threadId = "child", turnId = "childturn", item = new { type = "subAgentActivity", id = "report", kind = "interacted", agentThreadId = "main", agentPath = "/root" } }),
            Message("child", "Helper completed its work"), Completed("child", "childturn"), Message(text: "Parent final answer"), Completed()];
        using var client = new CodexChatClient(Path.GetTempPath(), reader, writer, new(), observed.Enqueue);
        await client.SendAsync("gpt-6.1-sol", "Delegate", text => answer += text, default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Parent final answer", answer);
        var child = observed.Last(e => e.Subagent is not null).Subagent!;
        Assert.Equal("completed", child.State);
        Assert.Equal("Helper completed its work", child.Output);
        Assert.DoesNotContain(observed, e => e.Subagent?.State == "disconnected");
        Assert.Single(writer.Frames, f => f.TryGetProperty("method", out var m) && m.GetString() == "thread/resume");
    }

    [Fact]
    public async Task First_child_approval_waits_for_subscription_to_confirm_its_active_turn()
    {
        var reader = new Reader(); var writer = new Writer(reader); var prompted = false;
        writer.ResumeResult = new { thread = new { id = "child", parentThreadId = "main", preview = "Task", turns = new[] { new { id = "childturn", status = "inProgress" } } } };
        writer.Tail = [Notify("item/started", new { threadId = "main", turnId = "turn1", item = new { type = "subAgentActivity", id = "spawn", kind = "started", agentThreadId = "child", agentPath = "/root/backend" } }), Approval("child")];
        writer.OnWrite = frame => { if (frame.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            reader.Feed(Message(), Completed(), Completed("child", "childturn")); };
        using var client = new CodexChatClient(Path.GetTempPath(), reader, writer, new() { RequestPermission = (_, _) => { prompted = true; return Task.FromResult<string?>("accept"); } });
        await client.SendAsync("gpt-6.1-sol", "Delegate", _ => { }, default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(prompted);
        var response = writer.Frames.Single(f => f.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String);
        Assert.Equal("accept", response.GetProperty("result").GetProperty("decision").GetString());
    }

    [Fact]
    public async Task Protocol_failure_while_settling_helpers_never_counts_as_parent_completion()
    {
        var reader = new Reader(); var writer = new Writer(reader); var events = new ConcurrentQueue<CursorActivityEvent>();
        writer.Tail = [Notify("thread/started", new { thread = Thread("child", "main") }),
            Notify("turn/started", new { threadId = "child", turn = new { id = "childturn", status = "inProgress" } }), Message(), Completed(), "{"];
        using var client = new CodexChatClient(Path.GetTempPath(), reader, writer, new(), events.Enqueue);
        var failure = await Assert.ThrowsAsync<IOException>(() => client.SendAsync("gpt-6.1-sol", "Delegate", _ => { }, default));
        Assert.IsAssignableFrom<JsonException>(failure.InnerException);
        var child = events.Last(e => e.Subagent is not null).Subagent!;
        Assert.Equal("disconnected", child.State);
        Assert.Equal(failure.InnerException!.Message, child.Failure);
    }
}
