using System.Text.Json;
using Sovereign.Desktop.Client;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class CursorProjectTests
{
    [Fact]
    public void Always_allow_sets_session_autorun_without_disabling_sandbox_or_changing_other_modes()
    {
        Assert.Equal(["--sandbox", "enabled", "--force", "acp"], CursorChatClient.SessionArguments(new() { Permissions = CursorPermissionPolicy.AlwaysAllow }));
        Assert.Equal(["--sandbox", "enabled", "acp"], CursorChatClient.SessionArguments(new() { Permissions = CursorPermissionPolicy.AlwaysAsk }));
        Assert.Equal(["--sandbox", "enabled", "acp"], CursorChatClient.SessionArguments(new() { Mode = CursorProjectMode.Plan, Permissions = CursorPermissionPolicy.AlwaysAllow }));
        Assert.Equal(["acp"], CursorChatClient.SessionArguments(null));
    }

    private static string Reply(int id, object result) => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result });
    private static string Update(object update) => JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "session/update", @params = new { sessionId = "s", update } });
    private static string Permission(string session = "s") => JsonSerializer.Serialize(new { jsonrpc = "2.0", id = "permission", method = "session/request_permission",
        @params = new { sessionId = session, toolCall = new { toolCallId = "t" }, options = new[] {
            new { optionId = "one", name = "Allow once", kind = "allow_once" },
            new { optionId = "forever", name = "Always", kind = "allow_always" },
            new { optionId = "no", name = "Reject", kind = "reject_once" } } } });
    private static (CursorAcpSession Session, StringWriter Sent) Create(CursorProjectOptions options, params string[] tail)
    {
        string[] init = [Reply(1, new { protocolVersion = 1 }), Reply(2, new { sessionId = "s", models = new { availableModels = new[] { new { modelId = "model", name = "Model" } } } }), Reply(3, new { }), Reply(4, new { })];
        var sent = new StringWriter();
        return (new CursorAcpSession(new StringReader(string.Join('\n', init.Concat(tail))), sent, options), sent);
    }
    private static string Answer => Update(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "Changed the file." } });
    private static string Tool => Update(new { sessionUpdate = "tool_call", toolCallId = "t", title = "Edit math.py", kind = "edit", status = "pending", rawInput = new { path = "math.py" } });

    [Fact]
    public async Task Agent_mode_and_cwd_stream_completed_changes_without_relabeling_chat()
    {
        var activity = new List<CursorToolActivity>();
        var (session, sent) = Create(new() { ToolActivity = activity.Add }, Tool,
            Update(new { sessionUpdate = "tool_call_update", toolCallId = "t", status = "completed", content = new[] { new { type = "diff", path = "math.py", oldText = "a-b", newText = "a+b" } } }), Answer, Reply(5, new { stopReason = "end_turn" }));
        await session.ConnectAsync("project 测试", CancellationToken.None);
        await session.SendAsync("model", "fix add", _ => { }, CancellationToken.None);
        Assert.Equal(2, activity.Count);
        Assert.Equal("Edit math.py", activity[1].Title); Assert.Equal("edit", activity[1].Kind);
        Assert.Equal("a+b", Assert.Single(activity[1].Changes).After); Assert.Equal("a-b", activity[1].Changes[0].Before);
        var messages = sent.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => JsonDocument.Parse(x).RootElement.Clone()).ToArray();
        Assert.Equal("project 测试", messages[1].GetProperty("params").GetProperty("cwd").GetString());
        Assert.Equal("agent", messages[2].GetProperty("params").GetProperty("modeId").GetString());
        Assert.DoesNotContain("Do not call tools", messages[4].GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("one", true)]
    [InlineData("no", true)]
    [InlineData("forever", false)]
    [InlineData("invented", false)]
    [InlineData(null, false)]
    public async Task Permission_uses_only_explicit_advertised_single_request_choices(string? choice, bool selected)
    {
        CursorPermissionRequest? observed = null;
        var (session, sent) = Create(new() { RequestPermission = (request, _) => { observed = request; return Task.FromResult(choice); } },
            Tool, Permission(), Answer, Reply(5, new { stopReason = "end_turn" }));
        await session.ConnectAsync("workspace", CancellationToken.None); await session.SendAsync("model", "fix", _ => { }, CancellationToken.None);
        Assert.Equal("Edit math.py", observed!.Tool.Title); Assert.Contains("math.py", observed.Tool.Input);
        var response = sent.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => JsonDocument.Parse(x).RootElement.Clone())
            .Single(x => x.GetProperty("id").ValueKind == JsonValueKind.String);
        var outcome = response.GetProperty("result").GetProperty("outcome");
        Assert.Equal(selected ? "selected" : "cancelled", outcome.GetProperty("outcome").GetString());
        if (selected) Assert.Equal(choice, outcome.GetProperty("optionId").GetString());
    }

    [Fact]
    public async Task Foreign_permission_is_rejected_before_user_callback()
    {
        var prompted = false;
        var (session, _) = Create(new() { RequestPermission = (_, _) => { prompted = true; return Task.FromResult<string?>("one"); } }, Permission("wrong"));
        await session.ConnectAsync("workspace", CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(() => session.SendAsync("model", "fix", _ => { }, CancellationToken.None));
        Assert.False(prompted);
    }

    [Fact]
    public async Task Cancellation_waiting_for_permission_cannot_grant_it()
    {
        using var cancel = new CancellationTokenSource();
        var (session, sent) = Create(new() { RequestPermission = (_, ct) => { cancel.Cancel(); return Task.FromCanceled<string?>(ct); } }, Tool, Permission());
        await session.ConnectAsync("workspace", cancel.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SendAsync("model", "fix", _ => { }, cancel.Token));
        Assert.DoesNotContain("\"outcome\":\"selected\"", sent.ToString());
    }

    [Fact]
    public async Task Always_allow_uses_each_advertised_once_option_without_prompting_or_persistent_grants()
    {
        var (session, sent) = Create(new() { Permissions = CursorPermissionPolicy.AlwaysAllow,
            RequestPermission = (_, _) => throw new InvalidOperationException("Should not prompt") },
            Permission(), Permission(), Answer, Reply(5, new { stopReason = "end_turn" }));
        await session.ConnectAsync("project", CancellationToken.None);
        await session.SendAsync("model", "fix", _ => { }, CancellationToken.None);
        var replies = sent.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => JsonDocument.Parse(x).RootElement.Clone()).Where(x => x.GetProperty("id").ValueKind == JsonValueKind.String).ToArray();
        Assert.Equal(2, replies.Length);
        Assert.All(replies, x => Assert.Equal("one", x.GetProperty("result").GetProperty("outcome").GetProperty("optionId").GetString()));
        Assert.DoesNotContain("forever", sent.ToString());
    }

    [Fact]
    public async Task Plan_mode_stays_read_only_and_asks_even_when_given_always_allow()
    {
        var prompted = false;
        var (session, sent) = Create(new() { Mode = CursorProjectMode.Plan, Permissions = CursorPermissionPolicy.AlwaysAllow,
            RequestPermission = (_, _) => { prompted = true; return Task.FromResult<string?>("no"); } },
            Permission(), Answer, Reply(5, new { stopReason = "end_turn" }));
        await session.ConnectAsync("project", CancellationToken.None);
        await session.SendAsync("model", "plan", _ => { }, CancellationToken.None);
        Assert.True(prompted);
        Assert.Contains("\"modeId\":\"plan\"", sent.ToString());
        Assert.Contains("Stay read-only", sent.ToString());
        Assert.DoesNotContain("Make requested file changes", sent.ToString());
        Assert.Contains("\"optionId\":\"no\"", sent.ToString());
    }

    [Fact]
    public async Task Plan_proposal_is_returned_for_review_without_accepting_implementation()
    {
        var plan = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = "proposal", method = "cursor/create_plan",
            @params = new { sessionId = "s", plan = "Inspect then improve the parser.", todos = new[] { new { id = "1", content = "Inspect", status = "pending" } } } });
        var (session, sent) = Create(new() { Mode = CursorProjectMode.Plan }, plan, Reply(5, new { stopReason = "end_turn" }));
        var output = "";
        await session.ConnectAsync("project", CancellationToken.None);
        await session.SendAsync("model", "plan", s => output += s, CancellationToken.None);
        Assert.Contains("Inspect then improve the parser.", output);
        Assert.Contains("Choose Always ask or Always allow beside the message box", output);
        Assert.Contains("\"outcome\":\"rejected\"", sent.ToString());
        Assert.DoesNotContain("\"outcome\":\"accepted\"", sent.ToString());
    }

    [Fact]
    public async Task Always_allow_refuses_foreign_session_and_unadvertised_once_option()
    {
        var (foreign, foreignSent) = Create(new() { Permissions = CursorPermissionPolicy.AlwaysAllow }, Permission("wrong"));
        await foreign.ConnectAsync("project", CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(() => foreign.SendAsync("model", "fix", _ => { }, CancellationToken.None));
        Assert.DoesNotContain("\"outcome\":\"selected\"", foreignSent.ToString());
        var (missing, missingSent) = Create(new() { Permissions = CursorPermissionPolicy.AlwaysAllow },
            Permission().Replace("allow_once", "unsupported"), Answer, Reply(5, new { stopReason = "end_turn" }));
        await missing.ConnectAsync("project", CancellationToken.None);
        await missing.SendAsync("model", "fix", _ => { }, CancellationToken.None);
        Assert.Contains("\"outcome\":\"cancelled\"", missingSent.ToString());
    }

    [Fact]
    public async Task Mode_change_from_plan_to_agent_is_not_silently_accepted()
    {
        var (session, _) = Create(new() { Mode = CursorProjectMode.Plan },
            Update(new { sessionUpdate = "current_mode_update", currentModeId = "agent" }));
        await session.ConnectAsync("project", CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(() => session.SendAsync("model", "plan", _ => { }, CancellationToken.None));
    }

    [Fact]
    public async Task Opening_project_never_overwrites_its_cursor_config()
    {
        var root = Path.Combine(Path.GetTempPath(), "SovereignProjectTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await CursorChatClient.PrepareWorkspaceAsync(root, true, CancellationToken.None);
            Assert.False(Directory.Exists(Path.Combine(root, ".cursor")));
            Directory.CreateDirectory(Path.Combine(root, ".cursor"));
            var config = Path.Combine(root, ".cursor", "cli.json");
            await File.WriteAllTextAsync(config, "owner configuration");
            await CursorChatClient.PrepareWorkspaceAsync(root, true, CancellationToken.None);
            Assert.Equal("owner configuration", await File.ReadAllTextAsync(config));
        }
        finally { Directory.Delete(root, true); }
    }
}
