using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Sovereign.Desktop.Client;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class CodexRuntimeExecutionTests
{
    private static readonly TimeSpan TestWait = TimeSpan.FromSeconds(5);
    private static string Notify(string method, object parameters) => JsonSerializer.Serialize(new { method, @params = parameters });
    private static string Message(string text = "Final answer") => Notify("item/agentMessage/delta", new { threadId = "main", turnId = "turn1", itemId = "answer", delta = text });
    private static string Completed() => Notify("turn/completed", new { threadId = "main", turn = new { id = "turn1", status = "completed" } });

    [Fact]
    public async Task Always_ask_accept_returns_the_requested_paths_and_network()
    {
        var root = Root();
        var inside = Path.Combine(root, "src", "note.txt");
        CursorPermissionRequest? seen = null;
        using var harness = Harness(root, new()
        {
            Permissions = CursorPermissionPolicy.AlwaysAsk,
            RequestPermission = (request, _) => { seen = request; return Task.FromResult<string?>("accept"); }
        });
        harness.Writer.Tail = [PermissionRequest(root, inside, network: true)];
        harness.Writer.OnResponse = () => harness.Reader.Feed(Message(), Completed());
        await harness.Client.SendAsync("gpt-6.1-sol", "Implement", _ => { }, default).WaitAsync(TestWait);
        Assert.NotNull(seen);
        Assert.Contains(inside, seen!.Tool.Input, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("network", seen.Tool.Input, StringComparison.OrdinalIgnoreCase);
        var grant = PermissionResponse(harness.Writer);
        Assert.Equal("turn", grant.GetProperty("scope").GetString());
        var entries = grant.GetProperty("permissions").GetProperty("fileSystem").GetProperty("entries").EnumerateArray().ToArray();
        Assert.NotEmpty(entries);
        Assert.Contains(entries, entry => entry.GetProperty("access").GetString() == "write" && entry.GetProperty("path").GetProperty("path").GetString() == inside);
        Assert.True(grant.GetProperty("permissions").GetProperty("network").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Plan_denies_writes_without_prompting()
    {
        var root = Root();
        var prompted = false;
        using var harness = Harness(root, new()
        {
            Mode = CursorProjectMode.Plan, Permissions = CursorPermissionPolicy.AlwaysAllow,
            RequestPermission = (_, _) => { prompted = true; return Task.FromResult<string?>("accept"); }
        });
        harness.Writer.Tail = [PermissionRequest(root, Path.Combine(root, "src", "note.txt"), write: true, network: true)];
        harness.Writer.OnResponse = () => harness.Reader.Feed(Message(), Completed());
        await harness.Client.SendAsync("gpt-6.1-sol", "Plan", _ => { }, default).WaitAsync(TestWait);
        Assert.False(prompted);
        var grant = PermissionResponse(harness.Writer);
        Assert.Equal("turn", grant.GetProperty("scope").GetString());
        Assert.All(grant.GetProperty("permissions").GetProperty("fileSystem").GetProperty("entries").EnumerateArray(),
            entry => Assert.Equal("deny", entry.GetProperty("access").GetString()));
        Assert.False(grant.GetProperty("permissions").GetProperty("network").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Always_allow_grants_in_project_paths_and_keeps_explicit_denials()
    {
        var root = Root();
        var inside = Path.Combine(root, "src", "note.txt");
        var denied = Path.Combine(root, "secret.txt");
        var prompted = false;
        using var harness = Harness(root, new()
        {
            Permissions = CursorPermissionPolicy.AlwaysAllow,
            RequestPermission = (_, _) => { prompted = true; return Task.FromResult<string?>("accept"); }
        });
        harness.Writer.Tail = [PermissionRequest(root, inside, deniedPath: denied, network: true)];
        harness.Writer.OnResponse = () => harness.Reader.Feed(Message(), Completed());
        await harness.Client.SendAsync("gpt-6.1-sol", "Implement", _ => { }, default).WaitAsync(TestWait);
        Assert.False(prompted);
        var grant = PermissionResponse(harness.Writer);
        var entries = grant.GetProperty("permissions").GetProperty("fileSystem").GetProperty("entries").EnumerateArray().ToArray();
        Assert.Contains(entries, entry => entry.GetProperty("access").GetString() == "write" && entry.GetProperty("path").GetProperty("path").GetString() == inside);
        Assert.Contains(entries, entry => entry.GetProperty("access").GetString() == "deny" && entry.GetProperty("path").GetProperty("path").GetString() == denied);
        Assert.True(grant.GetProperty("permissions").GetProperty("network").GetProperty("enabled").GetBoolean());
        Assert.Equal("turn", grant.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task Filesystem_root_is_not_auto_granted()
    {
        var root = Root();
        var inside = Path.Combine(root, "src", "note.txt");
        string? choice = "decline";
        using var harness = Harness(root, new()
        {
            Permissions = CursorPermissionPolicy.AlwaysAllow,
            RequestPermission = (_, _) => Task.FromResult<string?>(choice)
        });
        harness.Writer.Tail = [PermissionRequest(root, inside, specialRoot: true)];
        harness.Writer.OnResponse = () => harness.Reader.Feed(Message(), Completed());
        await harness.Client.SendAsync("gpt-6.1-sol", "Implement", _ => { }, default).WaitAsync(TestWait);
        var grant = PermissionResponse(harness.Writer);
        var entries = grant.GetProperty("permissions").GetProperty("fileSystem").GetProperty("entries").EnumerateArray().ToArray();
        Assert.Contains(entries, entry => entry.GetProperty("access").GetString() == "write" && entry.GetProperty("path").GetProperty("path").GetString() == inside);
        Assert.Contains(entries, entry => entry.GetProperty("access").GetString() == "deny" && entry.GetProperty("path").GetProperty("type").GetString() == "special");
    }

    [Fact]
    public async Task Command_exec_waits_past_the_short_rpc_timeout_while_diagnostics_keep_flowing()
    {
        using var harness = Harness(Root(), new());
        harness.Writer.Silent.Add("command/exec");
        harness.Writer.Silent.Add("model/list");
        await harness.Client.StartAsync(default);
        Assert.Equal(TimeSpan.FromSeconds(45), harness.Client.RpcTimeout("account/read"));
        Assert.Equal(Timeout.InfiniteTimeSpan, harness.Client.RpcTimeout("command/exec"));
        harness.Client.RpcTimeoutOverride = TimeSpan.FromMilliseconds(200);
        Assert.Equal(TimeSpan.FromMilliseconds(200), harness.Client.RpcTimeout("account/read"));
        Assert.Equal(Timeout.InfiniteTimeSpan, harness.Client.RpcTimeout("command/exec"));
        var stderr = new HeldReader();
        var drain = harness.Client.DrainDiagnosticsAsync(stderr);
        var exec = harness.Client.InvokeAsync("command/exec", new { command = new[] { "probe" } }, default);
        await Task.Delay(400);
        Assert.False(exec.IsCompleted);
        stderr.Release("api_key=supersecretvalue\nCreateProcessAsUserW failed: 5\n");
        var tailDeadline = DateTime.UtcNow.AddSeconds(2);
        while (!harness.Client.StderrTail.Contains("[redacted]", StringComparison.Ordinal) && DateTime.UtcNow < tailDeadline)
            await Task.Delay(20);
        Assert.Contains("[redacted]", harness.Client.StderrTail);
        Assert.DoesNotContain("supersecretvalue", harness.Client.StderrTail);
        Assert.False(exec.IsCompleted);
        await Assert.ThrowsAsync<TimeoutException>(() => harness.Client.InvokeAsync("model/list", new { }, default));
        Assert.False(exec.IsCompleted);
        harness.Reader.Feed(Notify("command/exec/outputDelta", new { processId = "probe", stream = "stdout", deltaBase64 = "YQ==", capReached = false }));
        var frame = harness.Writer.Frames.Single(item => item.TryGetProperty("method", out var method) && method.GetString() == "command/exec");
        harness.Reader.Feed(JsonSerializer.Serialize(new { id = frame.GetProperty("id").GetInt32(), result = new { exitCode = 0, stdout = "still-running", stderr = "" } }));
        var result = await exec.WaitAsync(TestWait);
        Assert.Equal(0, result.GetProperty("exitCode").GetInt32());
        Assert.Equal("still-running", result.GetProperty("stdout").GetString());
        Assert.Equal(1, harness.Writer.Frames.Count(item => item.TryGetProperty("method", out var method) && method.GetString() == "command/exec"));
        stderr.Finish();
        await drain.WaitAsync(TestWait);
    }

    [Fact]
    public async Task Steering_uses_the_active_parent_turn_and_queues_when_idle()
    {
        using var harness = Harness(Root(), new());
        Assert.False(await harness.Client.RequestSteeringAsync("later", default));
        Assert.DoesNotContain(harness.Writer.Frames, frame => frame.TryGetProperty("method", out var method) && method.GetString() == "turn/steer");
        harness.Writer.HoldTurn = true;
        var send = harness.Client.SendAsync("gpt-6.1-sol", "Implement", _ => { }, default);
        var deadline = DateTime.UtcNow.Add(TestWait);
        while (harness.Client.ActiveTurnId is null && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.Equal("main", harness.Client.ActiveThreadId);
        Assert.Equal("turn1", harness.Client.ActiveTurnId);
        Assert.True(await harness.Client.RequestSteeringAsync("use the fixture", default).WaitAsync(TestWait));
        Assert.Equal("turn1", harness.Client.ActiveTurnId);
        var steer = harness.Writer.Frames.Single(frame => frame.TryGetProperty("method", out var method) && method.GetString() == "turn/steer");
        Assert.Equal("main", steer.GetProperty("params").GetProperty("threadId").GetString());
        Assert.Equal("turn1", steer.GetProperty("params").GetProperty("expectedTurnId").GetString());
        var input = Assert.Single(steer.GetProperty("params").GetProperty("input").EnumerateArray());
        Assert.Equal("text", input.GetProperty("type").GetString());
        Assert.Equal("use the fixture", input.GetProperty("text").GetString());
        Assert.Single(harness.Writer.Frames, frame => frame.TryGetProperty("method", out var method) && method.GetString() == "turn/start");
        harness.Reader.Feed(Message(), Completed());
        await send.WaitAsync(TestWait);
    }

    [Fact]
    public async Task Steering_transport_failure_does_not_open_a_second_turn()
    {
        using var harness = Harness(Root(), new());
        harness.Writer.HoldTurn = true;
        harness.Writer.FailSteer = true;
        var send = harness.Client.SendAsync("gpt-6.1-sol", "Implement", _ => { }, default);
        var deadline = DateTime.UtcNow.Add(TestWait);
        while (harness.Client.ActiveTurnId is null && DateTime.UtcNow < deadline) await Task.Delay(20);
        var failure = await Assert.ThrowsAsync<IOException>(() => harness.Client.RequestSteeringAsync("correct that", default));
        Assert.Contains("steer transport failed", failure.Message);
        Assert.Equal("turn1", harness.Client.ActiveTurnId);
        Assert.Single(harness.Writer.Frames, frame => frame.TryGetProperty("method", out var method) && method.GetString() == "turn/start");
        harness.Reader.Feed(Message(), Completed());
        await send.WaitAsync(TestWait);
    }

    [Fact]
    public async Task Signed_in_state_stays_separate_from_project_tool_readiness()
    {
        using var ready = Harness(Root(), new() { Permissions = CursorPermissionPolicy.AlwaysAllow }, account: null, execExit: 0, execStdout: "sovereign-tools-ready\r\n", execStderr: "");
        Assert.False(await ready.Client.IsSubscriptionAsync(default));
        Assert.False(ready.Client.SubscriptionSignedIn);
        await ready.Client.EnsureProjectToolsAsync(default);
        Assert.Equal("ready", ready.Client.ProjectToolsStatus);
        Assert.Equal("unelevated", ready.Client.EffectiveSandbox);
        var execs = ready.Writer.Frames.Where(frame => frame.TryGetProperty("method", out var method) && method.GetString() == "command/exec").ToArray();
        var marker = Assert.Single(execs, frame => string.Join(" ", frame.GetProperty("params").GetProperty("command").EnumerateArray().Select(item => item.GetString())).Contains("sovereign-tools-ready", StringComparison.Ordinal));
        var shell = marker.GetProperty("params").GetProperty("command").EnumerateArray().First().GetString();
        Assert.Equal(CodexWindowsShell.SelectDefault(), shell);
        Assert.False(CodexWindowsShell.IsStorePowerShell(shell));
        Assert.Contains(execs, frame => string.Join(" ", frame.GetProperty("params").GetProperty("command").EnumerateArray().Select(item => item.GetString())).Contains("where.exe", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(CodexChatClient.SessionArguments(new CursorProjectOptions { Permissions = CursorPermissionPolicy.AlwaysAllow }), flag => flag.Contains("shell_path", StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual("dangerFullAccess", marker.GetProperty("params").GetProperty("sandboxPolicy").GetProperty("type").GetString());
        Assert.DoesNotContain(ready.Writer.Frames, frame => frame.TryGetProperty("method", out var method) && method.GetString() == "thread/shellCommand");

        using var failed = Harness(Root(), new(), account: "chatgpt", execExit: 1, execStdout: "", execStderr: "CreateProcessAsUserW failed: 5 (Access is denied.)");
        Assert.True(await failed.Client.IsSubscriptionAsync(default));
        Assert.True(failed.Client.SubscriptionSignedIn);
        Assert.Equal("not-requested", failed.Client.ProjectToolsStatus);
        await failed.Client.EnsureProjectToolsAsync(default);
        await failed.Client.EnsureProjectToolsAsync(default);
        Assert.StartsWith("failed:", failed.Client.ProjectToolsStatus);
        Assert.Contains("Project tools failed", failed.Client.ProjectToolsStatus);
        Assert.Contains("Exit 1", failed.Client.ProjectToolsStatus);
        Assert.Contains("CreateProcessAsUserW failed: 5", failed.Client.ProjectToolsStatus);
        Assert.Contains("Windows refused to start the WindowsApps pwsh.exe alias", failed.Client.ProjectToolsStatus);
        Assert.Contains("not started again", failed.Client.ProjectToolsStatus);
        Assert.Equal(1, failed.Writer.Frames.Count(frame => frame.TryGetProperty("method", out var method) && method.GetString() == "command/exec"));
        Assert.True(failed.Client.SubscriptionSignedIn);
    }

    [Fact]
    public async Task Readiness_rejects_a_store_pwsh_hit_instead_of_accepting_powershell_51()
    {
        using var harness = Harness(Root(), new(), execExit: 0, execStdout: "sovereign-tools-ready\r\n", execStderr: "");
        await harness.Client.IsSubscriptionAsync(default);
        harness.Writer.ExecOverride = static frame =>
        {
            var command = frame.GetProperty("params").GetProperty("command").EnumerateArray().Select(item => item.GetString() ?? "").ToArray();
            var joined = string.Join(" ", command);
            if (joined.Contains("where.exe", StringComparison.OrdinalIgnoreCase) && command[^1].Equals("pwsh", StringComparison.OrdinalIgnoreCase))
                return (0, @"C:\ProgramData\Microsoft\WindowsApps\pwsh.exe", "");
            if (joined.Contains("where.exe", StringComparison.OrdinalIgnoreCase))
                return (0, CodexWindowsShell.WindowsPowerShell, "");
            return (0, "sovereign-tools-ready\r\n", "");
        };
        await harness.Client.EnsureProjectToolsAsync(default);
        Assert.StartsWith("failed:", harness.Client.ProjectToolsStatus);
        Assert.Contains("WindowsApps alias", harness.Client.ProjectToolsStatus);
        Assert.Contains("not accepted", harness.Client.ProjectToolsStatus);
        Assert.DoesNotContain(harness.Writer.Frames, frame => frame.TryGetProperty("method", out var method) && method.GetString() == "command/exec"
            && string.Join(" ", frame.GetProperty("params").GetProperty("command").EnumerateArray().Select(item => item.GetString())).Contains("sovereign-tools-ready", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Readiness_marker_uses_the_shell_where_reported()
    {
        const string reported = @"D:\Tools\powershell\powershell.exe";
        using var harness = Harness(Root(), new(), execExit: 0, execStdout: "", execStderr: "");
        await harness.Client.IsSubscriptionAsync(default);
        harness.Writer.ExecOverride = static frame =>
        {
            var command = frame.GetProperty("params").GetProperty("command").EnumerateArray().Select(item => item.GetString() ?? "").ToArray();
            var joined = string.Join(" ", command);
            if (joined.Contains("where.exe", StringComparison.OrdinalIgnoreCase) && command[^1].Equals("pwsh", StringComparison.OrdinalIgnoreCase))
                return (1, "", "INFO: Could not find files for the given pattern(s).");
            if (joined.Contains("where.exe", StringComparison.OrdinalIgnoreCase))
                return (0, @"D:\Tools\powershell\powershell.exe", "");
            return (0, "sovereign-tools-ready\r\n", "");
        };
        await harness.Client.EnsureProjectToolsAsync(default);
        Assert.Equal("ready", harness.Client.ProjectToolsStatus);
        var marker = harness.Writer.Frames.Single(frame => frame.TryGetProperty("method", out var method) && method.GetString() == "command/exec"
            && string.Join(" ", frame.GetProperty("params").GetProperty("command").EnumerateArray().Select(item => item.GetString())).Contains("sovereign-tools-ready", StringComparison.Ordinal));
        Assert.Equal(reported, marker.GetProperty("params").GetProperty("command").EnumerateArray().First().GetString());
        Assert.False(CodexWindowsShell.IsStorePowerShell(reported));
    }

    [Fact]
    public async Task Observed_access_denied_command_is_recorded_once()
    {
        using var harness = Harness(Root(), new());
        harness.Writer.Tail = [Notify("item/completed", new
        {
            threadId = "main", item = new
            {
                type = "commandExecution", id = "tool", command = "pwsh", status = "failed", exitCode = 1,
                aggregatedOutput = "CreateProcessAsUserW failed: 5 (Access is denied.)"
            }
        }), Message(), Completed()];
        await harness.Client.SendAsync("gpt-6.1-sol", "Implement", _ => { }, default).WaitAsync(TestWait);
        Assert.StartsWith("failed:", harness.Client.ProjectToolsStatus);
        Assert.Contains("Access is denied", harness.Client.ProjectToolsStatus);
        Assert.DoesNotContain(harness.Writer.Frames, frame => frame.TryGetProperty("method", out var method) && method.GetString() == "command/exec");
    }

    private static string Root() => Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sov-codex-perm"));

    private static string PermissionRequest(string root, string inside, bool write = true, bool network = false, string? deniedPath = null, bool specialRoot = false)
    {
        var entries = new List<object> { new { access = write ? "write" : "read", path = new { type = "path", path = inside } } };
        if (deniedPath is not null) entries.Add(new { access = "deny", path = new { type = "path", path = deniedPath } });
        if (specialRoot) entries.Add(new { access = "write", path = new { type = "special", value = new { kind = "root" } } });
        return JsonSerializer.Serialize(new
        {
            id = "approval", method = "item/permissions/requestApproval",
            @params = new
            {
                cwd = root, itemId = "perm", startedAtMs = 1, threadId = "main", turnId = "turn1", reason = "Need project files",
                permissions = new { fileSystem = new { entries }, network = network ? new { enabled = true } : new { enabled = false } }
            }
        });
    }

    private static JsonElement PermissionResponse(ProtocolWriter writer)
    {
        var response = writer.Frames.Single(frame => frame.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String);
        return response.GetProperty("result");
    }

    private static Protocol Harness(string root, CursorProjectOptions options, string? account = "chatgpt", int execExit = 0, string execStdout = "", string execStderr = "")
    {
        var reader = new ProtocolReader();
        var writer = new ProtocolWriter(reader) { AccountType = account, ExecExit = execExit, ExecStdout = execStdout, ExecStderr = execStderr };
        var client = new CodexChatClient(root, reader, writer, options);
        return new(client, reader, writer);
    }

    private sealed record Protocol(CodexChatClient Client, ProtocolReader Reader, ProtocolWriter Writer) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }

    private sealed class ProtocolReader : TextReader
    {
        private readonly Channel<string> _frames = Channel.CreateUnbounded<string>();
        public void Feed(params string[] frames) { foreach (var frame in frames) _frames.Writer.TryWrite(frame); }
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) => await _frames.Reader.ReadAsync(cancellationToken);
    }

    private sealed class ProtocolWriter(ProtocolReader reader) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public readonly ConcurrentQueue<JsonElement> Frames = new();
        public string[] Tail { get; set; } = [];
        public Action? OnResponse { get; set; }
        public bool HoldTurn { get; set; }
        public bool FailSteer { get; set; }
        public HashSet<string> Silent { get; } = new(StringComparer.Ordinal);
        public string? AccountType { get; set; }
        public int ExecExit { get; set; }
        public string ExecStdout { get; set; } = "";
        public string ExecStderr { get; set; } = "";
        public Func<JsonElement, (int Exit, string Stdout, string Stderr)?>? ExecOverride { get; set; }
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            using var document = JsonDocument.Parse(buffer.ToString());
            var frame = document.RootElement.Clone();
            Frames.Enqueue(frame);
            if (frame.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String) OnResponse?.Invoke();
            if (!frame.TryGetProperty("method", out var method) || !frame.TryGetProperty("id", out var requestId)) return Task.CompletedTask;
            var name = method.GetString() ?? "";
            if (Silent.Contains(name)) return Task.CompletedTask;
            object? result = null;
            if (name == "command/exec" && ExecOverride?.Invoke(frame) is { } specialized)
                result = new { exitCode = specialized.Exit, stdout = specialized.Stdout, stderr = specialized.Stderr };
            result ??= name switch
            {
                "account/read" => AccountType is null ? new { account = (object?)null, requiresOpenaiAuth = true } : new { account = new { type = AccountType } },
                "model/list" => new { data = new[] { new { model = "gpt-6.1-sol", hidden = false } }, nextCursor = (string?)null },
                "thread/start" => new { thread = new { id = "main", turns = Array.Empty<object>() } },
                "turn/start" => new { turn = new { id = "turn1", status = "inProgress" } },
                "turn/steer" => FailSteer ? null : new { turnId = "turn1" },
                "config/read" => new { config = new { sandbox = "unelevated" } },
                "windowsSandbox/readiness" => new { status = "ready" },
                "command/exec" => new { exitCode = ExecExit, stdout = ExecStdout, stderr = ExecStderr },
                _ => new { }
            };
            if (name == "turn/steer" && FailSteer)
            {
                reader.Feed(JsonSerializer.Serialize(new { id = requestId.GetInt32(), error = new { message = "steer transport failed" } }));
                return Task.CompletedTask;
            }
            if (name == "turn/start") reader.Feed(Notify("turn/started", new { threadId = "main", turn = new { id = "turn1", status = "inProgress" } }));
            reader.Feed(JsonSerializer.Serialize(new { id = requestId.Clone(), result }));
            if (name == "turn/start" && !HoldTurn) reader.Feed(Tail);
            return Task.CompletedTask;
        }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class HeldReader : TextReader
    {
        private readonly TaskCompletionSource<string> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string _text = "";
        private int _pos = -1;
        public void Release(string text) { _text = text; _ready.TrySetResult(text); }
        public void Finish() { if (_pos < _text.Length) _pos = _text.Length; _ready.TrySetResult(_text); }
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            await _ready.Task.WaitAsync(cancellationToken);
            if (_pos < 0) _pos = 0;
            if (_pos >= _text.Length) return 0;
            var count = Math.Min(buffer.Length, _text.Length - _pos);
            _text.AsSpan(_pos, count).CopyTo(buffer.Span);
            _pos += count;
            return count;
        }
    }
}
