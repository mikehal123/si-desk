using System.Text.Json;
using Sovereign.Desktop.Client;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class CodexCommandPresentationTests
{
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void Startup_denial_search_miss_and_command_failure_stay_distinct()
    {
        var events = new List<CursorActivityEvent>();
        var state = new CodexProtocolState(Path.GetTempPath(), new(), events.Add);
        state.AssignParent("main");
        state.Observe("item/completed", Json(new { threadId = "main", item = new {
            type = "commandExecution", id = "denied", command = "Get-Location", status = "failed", exitCode = -1, durationMs = 0,
            source = "unified_exec_startup", aggregatedOutput = "Failed to create unified exec process: CreateProcessAsUserW failed: 5 (Access is denied.) | api_key=sekret" } }), _ => { });
        var denied = Assert.Single(events).Tool!;
        Assert.Equal("Windows could not start the WindowsApps PowerShell alias", denied.Title);
        Assert.Contains("execution alias", denied.Output);
        Assert.DoesNotContain("Access denied (5)", denied.Title);
        Assert.Equal("failed", denied.Status);
        Assert.Contains("SI Desk did not automatically retry it", denied.Output);
        Assert.DoesNotContain("sign in", denied.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("api_key [redacted]", denied.Diagnostics);
        Assert.DoesNotContain("sekret", denied.Diagnostics);

        var missed = CommandPresentation.Describe(Json(new { command = "rg needle", status = "failed", exitCode = 1, durationMs = 40, aggregatedOutput = "" }), "failed", null);
        Assert.Equal("failed", missed.Status);
        Assert.StartsWith("No matches", missed.Output);
        Assert.True(missed.ProcessStarted);

        var failed = CommandPresentation.Describe(Json(new { command = "dotnet test", status = "failed", exitCode = 1, durationMs = 90, aggregatedOutput = "Failed 1 test" }), "failed", null);
        Assert.Contains("Command failed — exit 1", failed.Output);
        Assert.DoesNotContain("No matches", failed.Output);
        Assert.Equal("failed", failed.Status);
    }

    [Fact]
    public void Diagnostic_redaction_covers_json_and_quoted_multitoken_values()
    {
        var raw = JsonSerializer.Serialize(new { api_key = "json-secret-123", access_token = "token with spaces", ok = "useful diagnostic" });
        var redacted = CommandPresentation.Redact(raw);
        Assert.DoesNotContain("json-secret-123", redacted);
        Assert.DoesNotContain("token with spaces", redacted);
        Assert.Contains("useful diagnostic", redacted);
        Assert.DoesNotContain("another multiword secret", CommandPresentation.Redact("password='another multiword secret'"));
        Assert.DoesNotContain("sample-token-123", CommandPresentation.Redact("Authorization: Bearer sample-token-123"));
    }

    [Fact]
    public void Runtime_version_follows_future_zero_major_releases()
    {
        Assert.Equal("0.3.51", DesktopClientVersion.FromVersion(new Version(0, 3, 51, 0)));
        Assert.Equal("1.2.3", DesktopClientVersion.FromVersion(new Version(1, 2, 3, 4)));
    }

    [Fact]
    public void Inherited_preview_cannot_become_or_replace_the_delegated_task()
    {
        var observed = new List<CursorActivityEvent>();
        var state = new CodexProtocolState(Path.GetTempPath(), new(), observed.Add);
        state.AssignParent("main");
        state.Observe("thread/started", Json(new { thread = new { id = "child", parentThreadId = "main", agentNickname = "locate_source", preview = "Inherited parent question", turns = Array.Empty<object>() } }), _ => { });
        Assert.Equal("", observed.Last().Subagent!.Task);
        state.Observe("item/completed", Json(new { threadId = "main", item = new { type = "collabAgentToolCall", id = "spawn", tool = "spawnAgent",
            senderThreadId = "main", receiverThreadIds = new[] { "child" }, prompt = "SOVEREIGN_TEAM role=investigator\nRead fixture.txt",
            agentsStates = new Dictionary<string, object>() } }), _ => { });
        var tasked = observed.Last().Subagent!;
        Assert.True(TeamPrompt.TryReadRole(tasked.Task, out var role, out var body));
        Assert.Equal("investigator", role);
        Assert.Equal("Read fixture.txt", body);
        state.Observe("thread/started", Json(new { thread = new { id = "child", parentThreadId = "main", preview = "A later inherited message", turns = Array.Empty<object>() } }), _ => { });
        Assert.Equal(tasked.Task, observed.Last().Subagent!.Task);
        Assert.Equal("Investigator", SubagentCardText.RoleLine(tasked));
        Assert.Equal("Read fixture.txt", SubagentCardText.TaskLine(tasked));
    }

    [Fact]
    public void Configured_identity_reroute_and_tool_counts_use_one_line_each()
    {
        var child = new CursorSubagent("h", "investigator_retry", "", "running",
            RequestedModelId: "gpt-6.1-sol", RequestedModelProvider: "openai", RequestedModelSource: "runtime configuration");
        Assert.Equal("OpenAI · GPT 6.1 Sol · configured", SubagentCardText.IdentityLine(child));
        Assert.Contains("Execution model was not separately reported.", SubagentCardText.IdentityDetail(child));
        Assert.Equal("Role not reported", SubagentCardText.RoleLine(child));
        Assert.Equal("Task details not supplied by provider", SubagentCardText.TaskLine(child));
        Assert.DoesNotContain("Provider unknown", SubagentCardText.IdentityLine(child));
        Assert.DoesNotContain("Model unknown", SubagentCardText.IdentityDetail(child));
        var reported = child with { ModelId = "gpt-6.1-sol", ModelProvider = "openai", ModelSource = "runtime reroute" };
        Assert.Equal("OpenAI · GPT 6.1 Sol", SubagentCardText.IdentityLine(reported));
        Assert.Contains("Configured: OpenAI · GPT 6.1 Sol", SubagentCardText.IdentityDetail(reported));
        Assert.Equal("Finished · 2 tool errors", SubagentCardText.LifecycleLine(child with { State = "completed", ToolSummary = new(4, 0, 2, 2, 0, 0, 0, 0) }));
        Assert.Equal("Finished · 1 tool error · 1 unconfirmed", SubagentCardText.LifecycleLine(child with { State = "completed", ToolSummary = new(4, 0, 2, 1, 0, 1, 0, 0) }));
        Assert.Equal("Interrupted", SubagentCardText.LifecycleLine(child with { State = "disconnected" }));
        Assert.Equal("Working · reading files", SubagentCardText.LifecycleLine(child with { CurrentWork = "reading files" }));
    }
}
