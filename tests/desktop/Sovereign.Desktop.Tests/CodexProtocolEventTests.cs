using System.Text;
using System.Text.Json;
using Sovereign.Desktop.Client;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class CodexProtocolEventTests
{
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void Access_denied_startup_stays_unstarted_and_a_nonzero_exit_stays_failed()
    {
        var events = new List<CursorActivityEvent>();
        var state = new CodexProtocolState(Path.GetTempPath(), new(), events.Add);
        state.AssignParent("main");
        state.Observe("turn/started", Json(new { threadId = "main", turn = new { id = "turn-1" } }), _ => { });
        state.Observe("turn/completed", Json(new { threadId = "main", turn = new { id = "turn-1", status = "completed", items = new[] { new {
            type = "commandExecution", id = "denied", command = "Get-Location", status = "failed", exitCode = -1, durationMs = 0,
            source = "unifiedExecStartup", aggregatedOutput = "", stderr = "",
            stdout = "Failed to create unified exec process: CreateProcessAsUserW failed: 5 (Access is denied.)" } } } }), _ => { });
        var denied = events.Single(e => e.Tool?.Id.EndsWith(":denied") == true).Tool!;
        Assert.Equal("Windows could not start the WindowsApps PowerShell alias", denied.Title);
        Assert.Contains("execution alias", denied.Output);
        Assert.Equal("failed", denied.Status);
        Assert.Contains("did not start", denied.Output);
        Assert.DoesNotContain("Exit: -1", denied.Output);

        state.Observe("item/completed", Json(new { threadId = "main", item = new {
            type = "commandExecution", id = "ran", command = "dotnet test", commandActions = Array.Empty<object>(), status = "completed",
            exitCode = 1, durationMs = 31000, aggregatedOutput = "Failed 1 test", stdout = "Failed 1 test" } }), _ => { });
        var failed = events.Single(e => e.Tool?.Id.EndsWith(":ran") == true).Tool!;
        Assert.Equal("failed", failed.Status);
        Assert.Contains("Command failed — exit 1", failed.Output);
        Assert.Contains("Failed 1 test", failed.Output);
        Assert.DoesNotContain("did not start", failed.Output);

        state.Observe("item/agentMessage/delta", Json(new { threadId = "main", itemId = "prose", delta = "All tests passed." }), _ => { });
        Assert.Equal("failed", events.Single(e => e.Tool?.Id.EndsWith(":ran") == true).Tool!.Status);
        Assert.DoesNotContain(events, e => e.Tool?.Status == "completed" && (e.Tool.Output.Contains("tests passed", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Exec_and_wait_script_running_with_empty_output_is_not_success()
    {
        var events = new List<CursorActivityEvent>();
        var state = new CodexProtocolState(Path.GetTempPath(), new(), events.Add);
        state.AssignParent("main");
        state.Observe("item/completed", Json(new { threadId = "main", item = new {
            type = "dynamicToolCall", id = "exec1", tool = "exec", status = "completed", durationMs = 31000, arguments = new { },
            contentItems = new[] { new { type = "inputText", text = "Script running with cell ID 4\nWall time 31.0 seconds\nOutput:\n" } } } }), _ => { });
        var exec = events.Single(e => e.Tool?.Id.EndsWith(":exec1") == true).Tool!;
        Assert.Equal("in_progress", exec.Status);
        Assert.Equal("", exec.Output);
        Assert.Contains("cell 4", exec.Title);

        state.Observe("item/completed", Json(new { threadId = "main", item = new {
            type = "dynamicToolCall", id = "wait1", tool = "wait", status = "completed", durationMs = 1000, arguments = new { },
            contentItems = new[] { new { type = "inputText", text = "Script running with cell ID 4" } } } }), _ => { });
        var waiting = events.Single(e => e.Tool?.Id.EndsWith(":wait1") == true).Tool!;
        Assert.Equal("in_progress", waiting.Status);
        Assert.Equal("", waiting.Output);

        state.Observe("item/agentMessage/delta", Json(new { threadId = "main", itemId = "prose", delta = "Passed 3 tests." }), _ => { });
        Assert.DoesNotContain(events, e => e.Tool?.Status == "completed");
        Assert.Equal("in_progress", events.Last(e => e.Tool?.Id.EndsWith(":exec1") == true).Tool!.Status);

        state.Observe("item/completed", Json(new { threadId = "main", item = new {
            type = "dynamicToolCall", id = "exec1", tool = "exec", status = "failed", durationMs = 1000, arguments = new { },
            contentItems = new[] { new { type = "inputText", text = "Cell 4 terminated" } } } }), _ => { });
        var ended = events.Last(e => e.Tool?.Id.EndsWith(":exec1") == true).Tool!;
        Assert.Equal("failed", ended.Status);
        Assert.Contains("terminated", ended.Output);
        Assert.NotEqual("completed", ended.Status);

        state.Observe("item/completed", Json(new { threadId = "main", item = new {
            type = "dynamicToolCall", id = "wait1", tool = "wait", status = "interrupted", durationMs = 1000, arguments = new { },
            contentItems = new[] { new { type = "inputText", text = "Cell 4 terminated" } } } }), _ => { });
        var waitEnded = events.Last(e => e.Tool?.Id.EndsWith(":wait1") == true).Tool!;
        Assert.Equal("unconfirmed", waitEnded.Status);
        Assert.NotEqual("completed", waitEnded.Status);

        state.Observe("item/completed", Json(new { threadId = "main", item = new {
            type = "functionCallOutput", id = "exec-real", name = "exec", status = "completed",
            output = "Script running with cell ID 9\nWall time 1.0 seconds\nOutput:\nMARKER-9" } }), _ => { });
        var produced = events.Last(e => e.Tool?.Id.EndsWith(":exec-real") == true).Tool!;
        Assert.Equal("completed", produced.Status);
        Assert.Contains("MARKER-9", produced.Output);
    }

    [Fact]
    public void Unknown_item_is_a_diagnostic_and_not_completed()
    {
        var events = new List<CursorActivityEvent>();
        var state = new CodexProtocolState(Path.GetTempPath(), new(), events.Add);
        state.AssignParent("main");
        state.Observe("item/completed", Json(new { threadId = "main", item = new { type = "imageGeneration", id = "img", status = "completed", result = "picture" } }), _ => { });
        var diagnostic = Assert.Single(events).Tool!;
        Assert.NotEqual("completed", diagnostic.Status);
        Assert.Equal("unconfirmed", diagnostic.Status);
        Assert.Contains("imageGeneration", diagnostic.Title);
        Assert.Contains("not mapped", diagnostic.Output);
    }

    [Fact]
    public void One_helper_failure_leaves_the_other_running_without_invented_output()
    {
        var events = new List<CursorActivityEvent>();
        var state = new CodexProtocolState(Path.GetTempPath(), new(), events.Add);
        state.AssignParent("main");
        state.Observe("thread/started", Json(new { thread = new { id = "helper-a", parentThreadId = "main", agentNickname = "alpha", preview = "Inherited parent question", model = "gpt-configured", turns = Array.Empty<object>() } }), _ => { });
        state.Observe("thread/started", Json(new { thread = new { id = "helper-b", parentThreadId = "main", agentNickname = "beta", preview = "Inherited parent question", turns = Array.Empty<object>() } }), _ => { });
        state.Observe("item/completed", Json(new { threadId = "main", item = new { type = "collabAgentToolCall", id = "spawn-a", tool = "spawnAgent", senderThreadId = "main", receiverThreadIds = new[] { "helper-a" }, prompt = "Read the fixture", agentsStates = new Dictionary<string, object>() } }), _ => { });
        state.Observe("turn/started", Json(new { threadId = "helper-a", turn = new { id = "turn-a" } }), _ => { });
        state.Observe("turn/started", Json(new { threadId = "helper-b", turn = new { id = "turn-b" } }), _ => { });
        state.Observe("model/rerouted", Json(new { threadId = "helper-a", turnId = "turn-a", toModel = "gpt-actual" }), _ => { });
        state.Observe("item/completed", Json(new { threadId = "main", item = new { type = "collabAgentToolCall", id = "wait-ab", tool = "wait", status = "completed", senderThreadId = "main", receiverThreadIds = new[] { "helper-a", "helper-b" },
            agentsStates = new Dictionary<string, object> { ["helper-a"] = new { status = "errored", message = "Actual A result" }, ["helper-b"] = new { status = "running" } } } }), _ => { });

        var failed = events.Last(e => e.Subagent?.SessionId == "helper-a").Subagent!;
        var other = events.Last(e => e.Subagent?.SessionId == "helper-b").Subagent!;
        Assert.Equal("failed", failed.State);
        Assert.Equal("Actual A result", failed.Output);
        Assert.Equal("gpt-actual", failed.ModelId);
        Assert.Equal("gpt-configured", failed.RequestedModelId);
        Assert.Equal("Read the fixture", failed.Task);
        Assert.Equal("running", other.State);
        Assert.Equal("", other.Output);
        Assert.Equal("", other.Task);
        Assert.Null(other.ModelId);
        Assert.Null(other.RequestedModelId);
        Assert.NotEqual(failed.ModelId, other.ModelId);
        Assert.False(state.WaitForChildrenAsync(default).IsCompleted);
    }

    [Fact]
    public void Retention_keeps_an_active_file_change_after_the_tool_cap()
    {
        var events = new List<CursorActivityEvent>();
        var state = new CodexProtocolState(Path.GetTempPath(), new(), events.Add);
        state.AssignParent("main");
        state.Observe("item/started", Json(new { threadId = "main", item = new { type = "commandExecution", id = "live", command = "cargo test", commandActions = Array.Empty<object>(), status = "inProgress" } }), _ => { });
        state.Observe("item/started", Json(new { threadId = "main", item = new { type = "fileChange", id = "edit", status = "inProgress", changes = new[] { new { path = "answer.txt", diff = "@@ -1 +1 @@\n-before\n+after", kind = new { type = "update" } } } } }), _ => { });
        for (var i = 0; i < 1000; i++)
            state.Observe("item/completed", Json(new { threadId = "main", item = new { type = "commandExecution", id = "old-" + i, command = "echo " + i, commandActions = Array.Empty<object>(), status = "completed", exitCode = 0, durationMs = 5, aggregatedOutput = "ok" } }), _ => { });
        state.Observe("item/commandExecution/outputDelta", Json(new { threadId = "main", itemId = "live", delta = "still-live" }), _ => { });
        state.Observe("item/fileChange/outputDelta", Json(new { threadId = "main", itemId = "edit", delta = "kept-delta" }), _ => { });

        var live = events.Last(e => e.Tool?.Id.EndsWith(":live") == true).Tool!;
        Assert.Equal("in_progress", live.Status);
        Assert.Contains("still-live", live.Output);
        var edit = events.Last(e => e.Tool?.Id.EndsWith(":edit") == true).Tool!;
        Assert.Equal("in_progress", edit.Status);
        Assert.Contains("kept-delta", edit.Output);
        var change = Assert.Single(edit.Changes);
        Assert.EndsWith("answer.txt", change.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("+after", change.UnifiedDiff);
    }

    [Fact]
    public void Output_deltas_append_before_the_final_exit()
    {
        var events = new List<CursorActivityEvent>();
        var state = new CodexProtocolState(Path.GetTempPath(), new(), events.Add);
        state.AssignParent("main");
        state.Observe("item/started", Json(new { threadId = "main", item = new { type = "commandExecution", id = "run", command = "dotnet test", commandActions = Array.Empty<object>(), status = "inProgress", processId = "proc-1" } }), _ => { });
        state.Observe("item/commandExecution/outputDelta", Json(new { threadId = "main", itemId = "run", delta = "api_key=sekret\n" }), _ => { });
        state.Observe("command/exec/outputDelta", Json(new { processId = "proc-1", stream = "stdout", deltaBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("line-two\n")), capReached = false }), _ => { });
        var running = events.Last().Tool!;
        Assert.Equal("in_progress", running.Status);
        Assert.Contains("line-two", running.Output);
        Assert.Contains("api_key [redacted]", running.Output);
        Assert.DoesNotContain("sekret", running.Output);

        state.Observe("item/completed", Json(new { threadId = "main", item = new { type = "commandExecution", id = "run", command = "dotnet test", commandActions = Array.Empty<object>(), status = "completed", exitCode = 0, durationMs = 40, processId = "proc-1", aggregatedOutput = "" } }), _ => { });
        var done = events.Last().Tool!;
        Assert.Equal("completed", done.Status);
        Assert.Contains("line-two", done.Output);
        Assert.Contains("api_key [redacted]", done.Output);
        Assert.DoesNotContain("sekret", done.Output);
        Assert.Contains("Exit: 0", done.Output);
        Assert.DoesNotContain("did not start", done.Output);
    }

    [Fact]
    public void Turn_snapshot_keeps_a_helper_result_that_never_streamed()
    {
        var events = new List<CursorActivityEvent>();
        var state = new CodexProtocolState(Path.GetTempPath(), new(), events.Add);
        state.AssignParent("main");
        state.Observe("thread/started", Json(new { thread = new { id = "helper-snap", parentThreadId = "main", agentNickname = "snap" } }), _ => { });
        state.Observe("turn/completed", Json(new { threadId = "helper-snap", turn = new { id = "turn-snap", status = "completed", items = new object[] {
            new { type = "agentMessage", id = "result", text = "Snapshot only RESULT" },
            new { type = "collabAgentToolCall", id = "close", tool = "closeAgent", senderThreadId = "helper-snap", receiverThreadIds = new[] { "helper-snap" },
                agentsStates = new Dictionary<string, object> { ["helper-snap"] = new { status = "completed", message = "Snapshot only RESULT" } } } } } }), _ => { });
        var helper = events.Last(e => e.Subagent?.SessionId == "helper-snap").Subagent!;
        Assert.Equal("completed", helper.State);
        Assert.Equal("Snapshot only RESULT", helper.Output);
    }

    [Fact]
    public void In_progress_subscription_maps_existing_failures_without_finishing_the_helper()
    {
        var events = new List<CursorActivityEvent>();
        var state = new CodexProtocolState(Path.GetTempPath(), new(), events.Add);
        state.AssignParent("main");
        state.Observe("thread/started", Json(new { thread = new { id = "helper-live", parentThreadId = "main", agentNickname = "live" } }), _ => { });
        var snapshot = Json(new { thread = new { id = "helper-live", parentThreadId = "main", turns = new[] { new { id = "turn-live", status = "inProgress", items = new object[] {
            new { type = "commandExecution", id = "cmd", command = "dotnet test", commandActions = Array.Empty<object>(), status = "failed", exitCode = 1, durationMs = 20, aggregatedOutput = "Failed 1 test", stdout = "Failed 1 test" },
            new { type = "dynamicToolCall", id = "cell", tool = "exec", status = "failed", durationMs = 20, arguments = new { }, contentItems = new[] { new { type = "inputText", text = "Cell 4 terminated" } } },
            new { type = "agentMessage", id = "note", text = "Helper still working" } } } } } });
        state.Hydrate(snapshot, "helper-live");
        var helper = events.Last(e => e.Subagent?.SessionId == "helper-live").Subagent!;
        Assert.Equal("running", helper.State);
        Assert.Equal("Helper still working", helper.Output);
        var command = events.Last(e => e.Tool?.Id.EndsWith(":cmd") == true).Tool!;
        Assert.Equal("failed", command.Status);
        Assert.Contains("exit 1", command.Output);
        var cell = events.Last(e => e.Tool?.Id.EndsWith(":cell") == true).Tool!;
        Assert.Equal("failed", cell.Status);
        Assert.Contains("terminated", cell.Output);
        Assert.False(state.WaitForChildrenAsync(default).IsCompleted);
        var tools = events.Count(e => e.Tool is not null);
        state.Hydrate(snapshot, "helper-live");
        Assert.Equal(tools, events.Count(e => e.Tool is not null));
        var again = events.Last(e => e.Subagent?.SessionId == "helper-live").Subagent!;
        Assert.Equal("running", again.State);
        Assert.Equal("Helper still working", again.Output);
    }
}
