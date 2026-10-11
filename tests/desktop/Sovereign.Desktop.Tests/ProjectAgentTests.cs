using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sovereign.Desktop.Client;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class ProjectAgentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sovereign-agent-test-" + Guid.NewGuid());
    public ProjectAgentTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private static JsonElement Args(object args) => JsonSerializer.SerializeToElement(args);
    private ProjectToolHost Host(CursorProjectMode mode = CursorProjectMode.Agent, CursorPermissionPolicy policy = CursorPermissionPolicy.AlwaysAllow,
        Action<CursorToolActivity>? observe = null) => new(_root, new() { Mode = mode, Permissions = policy, ToolActivity = observe });

    [Fact]
    public async Task File_edits_require_current_hash_and_report_actual_changes()
    {
        var events = new List<CursorToolActivity>(); var host = Host(observe: events.Add);
        await host.ExecuteAsync("1", "create_file", Args(new { path = "hello.md", content = "before" }), default);
        var read = JsonNode.Parse(await host.ExecuteAsync("2", "read_file", Args(new { path = "hello.md" }), default))!;
        var hash = read["result"]!["sha256"]!.GetValue<string>();
        var result = await host.ExecuteAsync("3", "edit_file", Args(new { path = "hello.md", old_text = "before", new_text = "after", expected_sha256 = hash }), default);
        Assert.Contains("completed", result); Assert.Equal("after", File.ReadAllText(Path.Combine(_root, "hello.md")));
        Assert.Equal("before", events.Last().Changes.Single().Before);
        result = await host.ExecuteAsync("4", "edit_file", Args(new { path = "hello.md", old_text = "after", new_text = "oops", expected_sha256 = hash }), default);
        Assert.Contains("failed", result); Assert.Empty(events.Last().Changes);
        Assert.Contains("failed", await host.ExecuteAsync("5", "create_file", Args(new { path = "hello.md", content = "oops" }), default));
        Assert.Equal("after", File.ReadAllText(Path.Combine(_root, "hello.md")));
    }

    [Fact]
    public async Task Drive_root_project_can_list_read_and_edit_its_descendant()
    {
        var drive = Path.GetPathRoot(_root)!;
        var relative = Path.GetRelativePath(drive, _root);
        var host = new ProjectToolHost(drive, new() { Permissions = CursorPermissionPolicy.AlwaysAllow });
        var filename = Path.Combine(relative, "drive-root-proof.md");
        Assert.Contains("completed", await host.ExecuteAsync("create", "create_file", Args(new { path = filename, content = "root marker" }), default));
        Assert.Contains("drive-root-proof.md", await host.ExecuteAsync("list", "list_files", Args(new { path = relative }), default));
        var read = JsonNode.Parse(await host.ExecuteAsync("read", "read_file", Args(new { path = filename }), default))!;
        Assert.Equal("completed", read["status"]!.GetValue<string>());
        var hash = read["result"]!["sha256"]!.GetValue<string>();
        Assert.Contains("completed", await host.ExecuteAsync("edit", "edit_file", Args(new { path = filename, old_text = "root marker", new_text = "updated", expected_sha256 = hash }), default));
        Assert.Equal("updated", File.ReadAllText(Path.Combine(_root, "drive-root-proof.md")));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("..\\escape.txt")]
    [InlineData("C:\\escape.txt")]
    [InlineData("file.txt:stream")]
    public async Task File_tools_refuse_escaping_paths(string path)
    {
        var result = await Host().ExecuteAsync("x", "create_file", Args(new { path, content = "no" }), default);
        Assert.Contains("failed", result); Assert.Empty(Directory.GetFiles(_root));
    }

    [Theory]
    [InlineData("create_file")]
    [InlineData("edit_file")]
    [InlineData("terminal")]
    public async Task Plan_never_executes_mutating_tools_even_when_always_allow(string tool)
    {
        var result = await Host(CursorProjectMode.Plan).ExecuteAsync("x", tool, Args(new { path = "no.txt", content = "no", command = "'no' | Set-Content no.txt" }), default);
        Assert.Contains("blocked", result); Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task Ask_requires_explicit_grant_and_cancel_during_approval_never_writes()
    {
        Assert.Contains("blocked", await Host(policy: CursorPermissionPolicy.AlwaysAsk).ExecuteAsync("x", "create_file", Args(new { path = "no.txt", content = "no" }), default));
        using var ct = new CancellationTokenSource();
        var host = new ProjectToolHost(_root, new() { RequestPermission = (_, _) => { ct.Cancel(); return Task.FromResult<string?>("allow"); } });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.ExecuteAsync("x", "create_file", Args(new { path = "no.txt", content = "no" }), ct.Token));
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task Search_reports_matches_and_terminal_reports_exit_and_cancels()
    {
        File.WriteAllText(Path.Combine(_root, "fixture.md"), "Marker: INDIGO-582"); var host = Host();
        Assert.Contains("INDIGO-582", await host.ExecuteAsync("1", "search_files", Args(new { query = "indigo" }), default));
        Assert.Contains("fixture.md", await host.ExecuteAsync("2", "list_files", Args(new { path = "." }), default));
        var good = await host.ExecuteAsync("3", "terminal", Args(new { command = "Write-Output 'COMMAND-OK'" }), default);
        Assert.Contains("COMMAND-OK", good); Assert.Contains("completed", good);
        Assert.Contains("failed", await host.ExecuteAsync("4", "terminal", Args(new { command = "exit 7" }), default));
        using var ct = new CancellationTokenSource(500);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.ExecuteAsync("5", "terminal", Args(new { command = "Start-Sleep 30; 'bad' | Set-Content late.txt" }), ct.Token));
        Assert.False(File.Exists(Path.Combine(_root, "late.txt")));
        Assert.Contains("timed out", await host.ExecuteAsync("6", "terminal", Args(new { command = "Start-Sleep 30", timeout_seconds = 1 }), default));
    }

    private sealed class Handler(Func<JsonNode, JsonNode> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            return new(HttpStatusCode.OK) { Content = new StringContent(respond(body).ToJsonString()) };
        }
    }
    private static JsonNode Reply(string finish, JsonNode message) => new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["finish_reason"] = finish, ["message"] = message }) };
    private static JsonObject Call(string id, string name, object args) => new() { ["id"] = id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = name, ["arguments"] = JsonSerializer.Serialize(args) } };

    [Theory]
    [InlineData("glm")]
    [InlineData("kimi")]
    [InlineData("moonshot")]
    public async Task Provider_loop_preserves_tool_ids_reasoning_and_returns_actual_file_content(string provider)
    {
        File.WriteAllText(Path.Combine(_root, "proof.md"), "INDIGO-582"); var round = 0;
        using var client = new CodingPlanChatClient(provider, CodingPlanChatClient.Endpoints(provider)[0], "test-key", new Handler(body =>
        {
            Assert.NotNull(body["tools"]);
            if (++round == 1) return Reply("tool_calls", new JsonObject { ["role"] = "assistant", ["reasoning_content"] = "Need the file.", ["tool_calls"] = new JsonArray(Call("read-1", "read_file", new { path = "proof.md" })) });
            var messages = (JsonArray)body["messages"]!;
            Assert.Equal("Need the file.", messages[^2]!["reasoning_content"]!.GetValue<string>());
            Assert.Equal("read-1", messages[^1]!["tool_call_id"]!.GetValue<string>());
            Assert.Contains("INDIGO-582", messages[^1]!["content"]!.GetValue<string>());
            return Reply("stop", new JsonObject { ["role"] = "assistant", ["content"] = "Verified INDIGO-582." });
        }));
        var answer = "";
        await client.SendProjectAsync("model", [new("user", "Read proof.md")], _root, new() { Permissions = CursorPermissionPolicy.AlwaysAllow }, s => answer += s, default);
        Assert.Equal(2, round); Assert.Equal("Verified INDIGO-582.", answer);
    }

    [Theory]
    [InlineData("length", false)]
    [InlineData("tool_calls", true)]
    public async Task Incomplete_or_duplicate_calls_never_execute(string finish, bool duplicate)
    {
        var calls = new JsonArray(Call("same", "create_file", new { path = "no.txt", content = "no" }));
        if (duplicate) calls.Add(Call("same", "create_file", new { path = "no2.txt", content = "no" }));
        using var client = new CodingPlanChatClient("glm", CodingPlanChatClient.Endpoints("glm")[0], "test", new Handler(_ => Reply(finish, new JsonObject { ["role"] = "assistant", ["tool_calls"] = calls })));
        await Assert.ThrowsAsync<ProjectAgentException>(() => client.SendProjectAsync("model", [], _root, new() { Permissions = CursorPermissionPolicy.AlwaysAllow }, _ => { }, default));
        Assert.Empty(Directory.GetFiles(_root));
    }
}
