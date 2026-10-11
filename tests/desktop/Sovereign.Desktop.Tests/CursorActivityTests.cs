using System.Text.Json;
using Sovereign.Desktop.Client;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class CursorActivityTests
{
    [Fact]
    public void Clearing_reasoning_resets_display_budget_without_finishing_or_discarding_tools()
    {
        using var state = new CursorTurnActivity();
        state.Apply(new("tool", Tool: new("edit", "Edit file", "edit", "completed", "", [new("answer.py", "before", "after")])), TimeSpan.Zero);
        state.Apply(new("reasoning", new string('a', 33000)), TimeSpan.FromSeconds(5));
        Assert.True(state.ReasoningDisplayLimitReached);
        Assert.Contains("[Reasoning display limit reached]", state.Reasoning);
        var phase = state.Phase;
        var lastEvent = state.LastEventAt;
        state.ClearReasoningDisplay();
        Assert.Empty(state.Reasoning);
        Assert.False(state.HasDisplayedReasoning);
        Assert.False(state.ReasoningDisplayLimitReached);
        Assert.Equal("running", state.Result);
        Assert.Equal(phase, state.Phase);
        Assert.Equal(lastEvent, state.LastEventAt);
        Assert.Equal(1, state.CompletedTools);
        Assert.Equal("after", Assert.Single(state.Changes).After);
        state.Apply(new("reasoning", "New updates — 中文 😀"), TimeSpan.FromSeconds(6));
        Assert.Equal("New updates — 中文 😀", state.Reasoning);
        Assert.True(state.HasDisplayedReasoning);
        Assert.False(state.ReasoningDisplayLimitReached);
    }

    [Fact]
    public void Clearing_reasoning_is_repeatable_and_does_not_mutate_retained_history_or_another_turn()
    {
        var original = new CursorActivityEvent("reasoning", "Earlier saved reasoning");
        using var other = new CursorTurnActivity();
        other.Apply(original, TimeSpan.Zero);
        using var display = new CursorTurnActivity();
        display.Apply(original, TimeSpan.Zero);
        for (var cycle = 0; cycle < 3; cycle++)
        {
            display.Apply(new("reasoning", new string('b', 33000)), TimeSpan.Zero);
            Assert.True(display.ReasoningDisplayLimitReached);
            display.ClearReasoningDisplay();
            display.ClearReasoningDisplay();
            Assert.Empty(display.Reasoning);
        }
        Assert.Equal("Earlier saved reasoning", other.Reasoning);
        Assert.Equal("Earlier saved reasoning", original.Text);
        Assert.Equal(original.Text, Assert.Single(CursorActivityRetention.Compact([original]), e => e.Kind == "reasoning").Text);
        display.Finish("completed");
        display.ClearReasoningDisplay();
        Assert.Equal("completed", display.Result);
    }

    [Fact]
    public void Redirected_turn_preserves_edits_and_does_not_claim_pending_tools_completed()
    {
        var state = new CursorTurnActivity();
        state.Apply(new("tool", Tool: new("edit", "Edit file", "edit", "completed", "", [new("answer.py", "before", "after")])), TimeSpan.Zero);
        state.Apply(new("tool", Tool: new("command", "Run command", "execute", "in_progress", "", [])), TimeSpan.Zero);
        state.Finish("redirected");
        Assert.Equal("redirected", state.Result);
        Assert.Equal("after", Assert.Single(state.Changes).After);
        Assert.Equal("unconfirmed", state.Tools.Single(t => t.Id == "command").Status);
        Assert.Contains("Redirected", state.Phase);
    }

    [Theory]
    [InlineData("read", "Reading files")]
    [InlineData("search", "Searching")]
    [InlineData("execute", "Running a command")]
    [InlineData("edit", "Editing files")]
    public void Compact_status_keeps_tool_details_out_of_the_live_line(string kind, string expected)
    {
        var state = new CursorTurnActivity();
        state.Apply(new("tool", Tool: new("one", "verbose internal command and path", kind, "in_progress", "{}", [])), TimeSpan.FromSeconds(40));
        Assert.Equal(expected + " · 1 step · 42s", state.CompactStatus(TimeSpan.FromSeconds(42)));
        state.Apply(new("permission"), TimeSpan.FromSeconds(43));
        Assert.StartsWith("Waiting for your approval", state.CompactStatus(TimeSpan.FromSeconds(60)));
        Assert.DoesNotContain("verbose", state.CompactStatus(TimeSpan.FromSeconds(60)));
    }
    [Fact]
    public void Composer_flow_status_matches_the_terminal_wait_and_response_lines()
    {
        var waiting = new CursorTurnActivity("Waiting for provider");
        Assert.Equal("Waiting for response… 5s · 0 steps", waiting.ComposerFlowStatus(TimeSpan.FromSeconds(5)));
        waiting.Apply(new("reasoning", "Checking the tree."), TimeSpan.FromSeconds(8));
        Assert.Equal("Thinking… 25s · 0 steps", waiting.ComposerFlowStatus(TimeSpan.FromSeconds(25)));
        waiting.Apply(new("message"), TimeSpan.FromSeconds(26));
        Assert.Equal("Responding… 1m 3s · 0 steps", waiting.ComposerFlowStatus(TimeSpan.FromSeconds(63)));
        waiting.Apply(new("tool", Tool: new("read-1", "verbose path", "read", "in_progress", "{}", [])), TimeSpan.FromSeconds(64));
        Assert.Equal("Reading files… 1m 4s · 1 step", waiting.ComposerFlowStatus(TimeSpan.FromSeconds(64)));
        Assert.DoesNotContain("no new events", waiting.ComposerFlowStatus(TimeSpan.FromSeconds(90)));
        Assert.Contains("no new events for 20s", waiting.Status(TimeSpan.FromSeconds(84)));
    }
    [Theory]
    [InlineData("read", "Reading files")]
    [InlineData("search", "Searching")]
    [InlineData("execute", "Running a command")]
    [InlineData("edit", "Editing files")]
    [InlineData("fetch", "Reading sources")]
    public void Composer_flow_keeps_paths_private_and_approval_clock_first(string kind, string label)
    {
        using var state = new CursorTurnActivity();
        Assert.Equal("Waiting for response… 0s · 0 steps", state.ComposerFlowStatus(TimeSpan.Zero));
        state.Apply(new("tool", Tool: new("one", "private internal path", kind, "in_progress", "{}", [])), TimeSpan.FromSeconds(2));
        Assert.Equal(label + "… 1m 3s · 1 step", state.ComposerFlowStatus(TimeSpan.FromSeconds(63)));
        state.Apply(new("permission"), TimeSpan.FromSeconds(64));
        Assert.Equal("Waiting for your approval… 1m 5s · 1 step", state.ComposerFlowStatus(TimeSpan.FromSeconds(65)));
        Assert.DoesNotContain("private", state.ComposerFlowStatus(TimeSpan.FromSeconds(65)));
    }
    private static string Reply(int id, object result) => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result });
    private static string Notify(string kind, object update, string session = "s") => JsonSerializer.Serialize(new { jsonrpc = "2.0", method = kind, @params = new { sessionId = session, update } });
    private static (CursorAcpSession Client, StringWriter Sent) Session(List<CursorActivityEvent> events, bool project, params string[] tail)
    {
        string[] init = [Reply(1, new { protocolVersion = 1 }), Reply(2, new { sessionId = "s", models = new { availableModels = new[] { new { modelId = "m", name = "Model" } } } }), Reply(3, new { }), Reply(4, new { })];
        var sent = new StringWriter();
        return (new(new StringReader(string.Join('\n', init.Concat(tail))), sent, project ? new CursorProjectOptions() : null, events.Add), sent);
    }
    private static string Message(string text) => Notify("session/update", new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text } });

    [Fact]
    public void Saved_completed_execution_with_allowlist_refusal_is_blocked_and_visible()
    {
        var state = new CursorTurnActivity();
        var tool = new CursorToolActivity("blocked", "python tests", "execute", "completed", "", [], "Not in allowlist: python");
        state.Apply(new("tool", Tool: tool), TimeSpan.Zero);
        state.Apply(new("message"), TimeSpan.Zero);
        state.Finish("completed");
        Assert.Equal("blocked", Assert.Single(state.Tools).Status);
        Assert.Equal(1, state.BlockedTools);
        Assert.Contains("did not run", state.ExecutionNotice);
        Assert.Contains("unverified", state.Phase);
        Assert.Equal("completed", tool.Status); // Preserve the provider's original event.
    }

    [Theory]
    [InlineData("read", "completed", "Not in allowlist: python")]
    [InlineData("execute", "completed", "Tests cover the message Not in allowlist: python")]
    [InlineData("execute", "completed", "3 tests passed")]
    [InlineData("execute", "in_progress", "Not in allowlist: python")]
    public void Refusal_detection_does_not_invent_blocked_commands(string kind, string status, string output)
    {
        var state = new CursorTurnActivity();
        state.Apply(new("tool", Tool: new("t", "Test", kind, status, "", [], output)), TimeSpan.Zero);
        Assert.Equal(0, state.BlockedTools);
        Assert.Empty(state.ExecutionNotice);
    }

    [Fact]
    public async Task Real_reasoning_is_separate_from_answer_and_observer_does_not_enable_tools()
    {
        var events = new List<CursorActivityEvent>();
        var (client, sent) = Session(events, false,
            Notify("session/update", new { sessionUpdate = "agent_thought_chunk", content = new { type = "text", text = "Checking edge cases." } }),
            "{\"jsonrpc\":\"2.0\",\"id\":\"p\",\"method\":\"session/request_permission\",\"params\":{}}",
            Message("Answer"), Reply(5, new { stopReason = "end_turn" }));
        await client.ConnectAsync("workspace", default);
        var answer = ""; await client.SendAsync("m", "hello", s => answer += s, default);
        Assert.Equal("Answer", answer);
        Assert.Equal("Checking edge cases.", Assert.Single(events, e => e.Kind == "reasoning").Text);
        Assert.Contains("\"modeId\":\"ask\"", sent.ToString());
        Assert.Contains("\"outcome\":\"cancelled\"", sent.ToString());
    }

    [Fact]
    public async Task Foreign_reasoning_is_rejected_before_observer()
    {
        var events = new List<CursorActivityEvent>();
        var (client, _) = Session(events, false, Notify("session/update", new { sessionUpdate = "agent_thought_chunk", content = new { type = "text", text = "wrong" } }, "other"));
        await client.ConnectAsync("workspace", default);
        await Assert.ThrowsAsync<IOException>(() => client.SendAsync("m", "hello", _ => { }, default));
        Assert.Empty(events);
    }

    [Fact]
    public async Task Plans_merge_and_tool_output_survives_status_only_update()
    {
        var events = new List<CursorActivityEvent>();
        var (client, _) = Session(events, true,
            Notify("session/update", new { sessionUpdate = "plan", entries = new[] { new { content = "Read file", status = "in_progress" } } }),
            JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "cursor/update_todos", @params = new { todos = new[] { new { id = "0", content = "Read file", status = "completed" } }, merge = true } }),
            Notify("session/update", new { sessionUpdate = "tool_call", toolCallId = "t", title = "Read file", status = "in_progress", content = new[] { new { type = "content", content = new { type = "text", text = "file contents" } } } }),
            Notify("session/update", new { sessionUpdate = "tool_call_update", toolCallId = "t", status = "completed" }),
            Message("Done"), Reply(5, new { stopReason = "end_turn" }));
        await client.ConnectAsync("workspace", default); await client.SendAsync("m", "read", _ => { }, default);
        var state = new CursorTurnActivity(); foreach (var e in events) state.Apply(e, TimeSpan.FromSeconds(1));
        Assert.Equal("completed", Assert.Single(state.Plan).Status);
        var tool = Assert.Single(state.Tools); Assert.Equal("completed", tool.Status); Assert.Equal("file contents", tool.Output);
    }

    [Fact]
    public void Repeated_tool_updates_do_not_duplicate_steps_or_lose_original_diff()
    {
        var state = new CursorTurnActivity();
        var first = new CursorToolActivity("1", "Edit file", "edit", "completed", "", [new("a.py", "before", "middle")]);
        state.Apply(new("tool", Tool: first), TimeSpan.Zero);
        state.Apply(new("tool", Tool: first), TimeSpan.Zero);
        state.Apply(new("tool", Tool: first with { Id = "2", Changes = [new("a.py", "middle", "after")] }), TimeSpan.Zero);
        state.Apply(new("tool", Tool: first with { Id = "3", Status = "failed", Changes = [new("b.py", null, "not applied")] }), TimeSpan.Zero);
        Assert.Equal(3, state.Tools.Count); Assert.Equal(1, state.FailedTools);
        var change = Assert.Single(state.Changes); Assert.Equal("before", change.Before); Assert.Equal("after", change.After);
    }

    [Fact]
    public void Stopping_keeps_finished_edits_but_does_not_invent_tool_completion()
    {
        var state = new CursorTurnActivity();
        state.Apply(new("tool", Tool: new("1", "Edit", "edit", "in_progress", "", [])), TimeSpan.Zero);
        Assert.Contains("no new events for 20s", state.Status(TimeSpan.FromSeconds(20)));
        state.Finish("stopped");
        state.Apply(new("tool", Tool: new("1", "Edit", "edit", "completed", "", [new("a", "b", "c")])), TimeSpan.Zero);
        Assert.Equal("unconfirmed", Assert.Single(state.Tools).Status); Assert.Empty(state.Changes);
        Assert.DoesNotContain("no new events", state.Status(TimeSpan.FromSeconds(21)));
    }

    [Fact]
    public void Reasoning_is_bounded_and_approval_wait_is_explicit()
    {
        var state = new CursorTurnActivity();
        state.Apply(new("reasoning", new string('x', 50000)), TimeSpan.Zero);
        Assert.True(state.Reasoning.Length < 32100); Assert.Contains("display limit", state.Reasoning);
        state.Apply(new("permission"), TimeSpan.Zero);
        Assert.StartsWith("Waiting for your approval", state.Status(TimeSpan.FromSeconds(50)));
        Assert.DoesNotContain("no new events", state.Status(TimeSpan.FromSeconds(50)));
    }
}
