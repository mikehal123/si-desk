using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Sovereign.Desktop.Client;

/// <summary>Official Codex app-server over private stdio. Auth stays in Codex; no API-key fallback.</summary>
public sealed partial class CodexChatClient : IDisposable
{
    private readonly string _workspace;
    private readonly string _home;
    private readonly CursorProjectOptions? _project;
    private readonly Action<CursorActivityEvent>? _activity;
    private readonly SemaphoreSlim _start = new(1, 1);
    private readonly SemaphoreSlim _send = new(1, 1);
    private Process? _process;
    private CodexAppSession? _session;
    private bool _disposed;
    private string[]? _models;
    private bool _injectedTransport;
    private string _stderrTail = "";
    internal string StderrTail => _stderrTail;
    /// <summary>True only after account/read reports a ChatGPT subscription. Independent of project tools.</summary>
    public bool SubscriptionSignedIn { get; private set; }
    /// <summary>not-requested, ready, or failed: reason. Set from sandbox and command/exec results.</summary>
    public string ProjectToolsStatus => _session?.ProjectToolsStatus ?? "not-requested";
    public string? EffectiveSandbox => _session?.EffectiveSandbox;
    public string? ActiveThreadId => _session?.ParentThread;
    public string SkillGuidance { get; set; } = "";
    public string? ActiveTurnId => _session?.ActiveTurnId;
    public CodexChatClient(string workspace, CursorProjectOptions? project = null, Action<CursorActivityEvent>? activity = null, string? profile = null)
    {
        _workspace = Path.GetFullPath(workspace); _project = project; _activity = activity;
        _home = profile is not null ? Path.Combine(profile, "chat-workspace", "codex-home")
            : project is null ? Path.Combine(_workspace, "codex-home") : Path.Combine(ConnectionSetup.Root, "codex-home");
    }
    internal CodexChatClient(string workspace, TextReader reader, TextWriter writer, CursorProjectOptions? project = null, Action<CursorActivityEvent>? activity = null)
        : this(workspace, project, activity) { _session = new(reader, writer, _workspace, project, activity); _injectedTransport = true; }
    public static string FindExecutable() => CodexRuntimeLocator.FindExecutable();
    internal string PrivateHome => _home;
    internal string? InternalThreadId => _session?.ParentThread;

    internal static string[] SessionArguments(CursorProjectOptions? project)
    {
        // codex-cli 0.162 has windows.sandbox and no shell_path key. An unknown -c value is ignored,
        // so the Store alias is kept off PATH in ApplyConnectionEnvironment instead.
        var arguments = new List<string>
        {
            "app-server", "--listen", "stdio://", "-c",
            "features.shell_tool=" + (project is null ? "false" : "true"), "-c", "web_search=\"" + (project is null ? "disabled" : "live") + "\"",
            "-c", "agents.enabled=" + (project?.SubagentsAllowed == true ? "true" : "false"), "-c", "features.multi_agent=" + (project?.SubagentsAllowed == true ? "true" : "false"),
            "-c", "agents.max_concurrent_threads_per_session=3", "-c", "features.multi_agent_v2.max_concurrent_threads_per_session=4"
        };
        if (project?.SubagentsAllowed != true)
        {
            arguments.Add("-c");
            arguments.Add("features.multi_agent_v2.enabled=false");
        }
        arguments.Add("-c");
        arguments.Add("windows.sandbox=\"unelevated\"");
        return arguments.ToArray();
    }

    internal static void ApplyConnectionEnvironment(ProcessStartInfo info, string home)
    {
        info.Environment["CODEX_HOME"] = home;
        foreach (var name in new[] { "OPENAI_API_KEY", "CODEX_API_KEY", "CODEX_ACCESS_TOKEN" }) info.Environment.Remove(name);
        if (!OperatingSystem.IsWindows()) return;
        var path = info.Environment.TryGetValue("PATH", out var current) ? current : Environment.GetEnvironmentVariable("PATH");
        info.Environment["PATH"] = CodexWindowsShell.WithoutStoreEntries(path);
    }

    public async Task StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _start.WaitAsync(ct);
        try
        {
            if (_session is null)
            {
                if (_project is not null && !Directory.Exists(_workspace)) throw new DirectoryNotFoundException("The selected project folder no longer exists.");
                Directory.CreateDirectory(_workspace); Directory.CreateDirectory(_home);
                var utf8 = new UTF8Encoding(false, true);
                var info = new ProcessStartInfo(FindExecutable()) { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = _workspace,
                    StandardInputEncoding = utf8, StandardOutputEncoding = utf8, StandardErrorEncoding = utf8 };
                ApplyConnectionEnvironment(info, _home);
                foreach (var argument in SessionArguments(_project)) info.ArgumentList.Add(argument);
                _process = Process.Start(info) ?? throw new IOException("Codex could not start.");
                _ = DrainStderrAsync(_process);
                _session = new(_process.StandardOutput, _process.StandardInput, _workspace, _project, _activity);
            }
            await _session.InitializeAsync(ct);
            if (!_injectedTransport && _project is not null && OperatingSystem.IsWindows()) await EnsureProjectToolsAsync(ct);
        }
        catch (Exception ex)
        {
            var tail = StderrTail;
            Dispose();
            if (tail.Length > 0 && ex is not OperationCanceledException)
                throw new IOException(ex.Message + "\nRuntime diagnostic: " + tail, ex);
            throw;
        }
        finally { _start.Release(); }
    }
    public async Task<bool> IsSubscriptionAsync(CancellationToken ct)
    {
        await StartAsync(ct);
        var result = await _session!.CallAsync("account/read", new { refreshToken = false }, ct);
        SubscriptionSignedIn = result.TryGetProperty("account", out var account) && account.ValueKind == JsonValueKind.Object &&
            account.TryGetProperty("type", out var type) && type.GetString() == "chatgpt";
        return SubscriptionSignedIn;
    }
    public async Task<string> LoginAsync(CancellationToken ct)
    {
        await StartAsync(ct);
        var result = await _session!.CallAsync("account/login/start", new { type = "chatgpt" }, ct);
        return result.GetProperty("authUrl").GetString() ?? throw new IOException("Codex returned no sign-in URL.");
    }
    public async Task<string[]> ModelsAsync(CancellationToken ct)
    {
        await StartAsync(ct);
        return _models = await ReadModelPagesAsync((cursor, token) => _session!.CallAsync("model/list", new { cursor, limit = 100, includeHidden = false }, token), ct);
    }
    internal static async Task<string[]> ReadModelPagesAsync(Func<string?, CancellationToken, Task<JsonElement>> page, CancellationToken ct)
    {
        var models = new List<string>(); var cursors = new HashSet<string>(StringComparer.Ordinal); string? cursor = null;
        for (var i = 0; i < 20; i++)
        {
            ct.ThrowIfCancellationRequested(); var result = await page(cursor, ct);
            foreach (var model in result.GetProperty("data").EnumerateArray())
            {
                if (model.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True) continue;
                var id = model.GetProperty("model").GetString();
                if (!string.IsNullOrWhiteSpace(id) && !models.Contains(id, StringComparer.Ordinal)) models.Add(id);
            }
            cursor = result.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
            if (string.IsNullOrEmpty(cursor)) return models.ToArray();
            if (!cursors.Add(cursor)) throw new IOException("Codex repeated a model-list page. Reconnect to refresh models.");
        }
        throw new IOException("Codex model catalog exceeded the page limit.");
    }
    public Task<bool> RequestSteeringAsync(string instruction, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(instruction)) throw new ArgumentException("A correction needs text.", nameof(instruction));
        var session = _session;
        if (session is null) return Task.FromResult(false);
        return session.RequestSteeringAsync(instruction, ct);
    }

    internal TimeSpan RpcTimeout(string method) => _session?.RpcTimeout(method) ?? TimeSpan.FromSeconds(45);
    internal TimeSpan? RpcTimeoutOverride { set { if (_session is not null) _session.RpcTimeoutOverride = value; } }
    internal Task<JsonElement> InvokeAsync(string method, object parameters, CancellationToken ct) => _session!.CallAsync(method, parameters, ct);
    internal Task DrainDiagnosticsAsync(TextReader reader) => DrainDiagnosticsCore(reader);
    internal async Task EnsureProjectToolsAsync(CancellationToken ct)
    {
        await _session!.EnsureProjectToolsAsync(ct);
        if (_session.ProjectToolsStatus.StartsWith("failed:", StringComparison.Ordinal) && _stderrTail.Length > 0
            && !_session.ProjectToolsStatus.Contains(_stderrTail, StringComparison.Ordinal))
            _session.AttachToolsDiagnostic(_stderrTail);
    }

    public async Task SendAsync(string model, string text, Action<string> delta, CancellationToken ct, IReadOnlyList<ChatImage>? images = null)
    {
        await _send.WaitAsync(ct);
        try
        {
            if (!await IsSubscriptionAsync(ct)) throw new InvalidOperationException("Sign in with ChatGPT first. This room does not use API-key billing.");
            var models = _models ?? await ModelsAsync(ct);
            if (!models.Contains(model, StringComparer.Ordinal)) throw new IOException("Choose a model advertised by your signed-in Codex account. No model fallback is used.");
            await _session!.SendAsync(model, SkillLibrary.ApplyGuidance(text, SkillGuidance), delta, ct, images);
        }
        catch
        {
            if (_session is not null)
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try { await _session.InterruptAsync(stop.Token); }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
            Dispose(); throw;
        }
        finally { _send.Release(); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _session?.Dispose();
        var process = _process; _process = null;
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        finally { process?.Dispose(); }
    }

    private Task DrainStderrAsync(Process process) => DrainDiagnosticsCore(process.StandardError);
    private async Task DrainDiagnosticsCore(TextReader reader)
    {
        try
        {
            var buffer = new char[512];
            var kept = new StringBuilder();
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory());
                if (read == 0) break;
                kept.Append(buffer, 0, read);
                if (kept.Length > 6000) kept.Remove(0, kept.Length - 4000);
                _stderrTail = CommandPresentation.Redact(kept.ToString());
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }
}

internal sealed class CodexAppSession(TextReader reader, TextWriter writer, string workspace, CursorProjectOptions? project,
    Action<CursorActivityEvent>? activity) : IDisposable
{
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CodexProtocolState _state = new(workspace, project, activity);
    private readonly ConcurrentDictionary<string, Task> _subscriptions = new();
    private int _nextId;
    private bool _initialized;
    private Task? _reading;
    private TaskCompletionSource<bool>? _turn;
    private CancellationToken _turnToken;
    private string? _activeTurn;
    private Action<string>? _delta;
    private int _length;
    private Exception? _failure;
    private TaskCompletionSource<bool>? _sandboxSetup;
    private CancellationTokenSource? _permissions;
    private int _toolsProbeStarted;
    private TimeSpan? _rpcTimeoutOverride;
    public string? ParentThread => _state.ParentThread;
    internal string? ActiveTurnId => _activeTurn;
    public string ProjectToolsStatus { get; private set; } = "not-requested";
    public string? EffectiveSandbox { get; private set; }
    internal TimeSpan? RpcTimeoutOverride { set => _rpcTimeoutOverride = value; }
    internal TimeSpan RpcTimeout(string method) => method == "command/exec" ? Timeout.InfiniteTimeSpan : _rpcTimeoutOverride ?? TimeSpan.FromSeconds(45);

    public async Task InitializeAsync(CancellationToken ct)
    {
        if (_initialized) return;
        _state.ChildSpawned = id => { _subscriptions[id] = SubscribeChildAsync(id); };
        _reading ??= ReadLoopAsync();
        await CallAsync("initialize", new { clientInfo = new { name = "sovereign_desktop", title = "SI Desk", version = DesktopClientVersion.Current },
            capabilities = new { experimentalApi = true } }, ct);
        await WriteAsync(new { method = "initialized" }, ct); _initialized = true;
    }
    public async Task EnsureProjectToolsAsync(CancellationToken ct)
    {
        if (project is null) { ProjectToolsStatus = "not-requested"; return; }
        if (ProjectToolsStatus == "ready" || ProjectToolsStatus.StartsWith("failed:", StringComparison.Ordinal)) return;
        if (Interlocked.Exchange(ref _toolsProbeStarted, 1) == 1) return;
        try
        {
            EffectiveSandbox = "unelevated";
            try
            {
                var config = await CallAsync("config/read", new { cwd = workspace, includeLayers = false }, ct);
                EffectiveSandbox = FindSandbox(config) ?? EffectiveSandbox;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException) { }
            if (OperatingSystem.IsWindows())
            {
                var readiness = await CallAsync("windowsSandbox/readiness", new { }, ct);
                var status = readiness.GetProperty("status").GetString();
                if (status != "ready")
                {
                    var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); _sandboxSetup = completion;
                    try
                    {
                        var started = await CallAsync("windowsSandbox/setupStart", new { mode = "unelevated", cwd = workspace }, ct);
                        if (!started.GetProperty("started").GetBoolean())
                            throw new IOException($"Project tools failed. Sandbox {EffectiveSandbox} setup did not start. Readiness {status}. No weaker sandbox was selected.");
                        bool success;
                        using (var setupLimit = CancellationTokenSource.CreateLinkedTokenSource(ct))
                        {
                            setupLimit.CancelAfter(TimeSpan.FromSeconds(45));
                            try { success = await completion.Task.WaitAsync(setupLimit.Token); }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                            {
                                throw new IOException($"Project tools failed. Sandbox {EffectiveSandbox} setup did not finish. Readiness {status}. It was not retried. No weaker sandbox was selected.");
                            }
                        }
                        if (!success)
                            throw new IOException($"Project tools failed. Sandbox {EffectiveSandbox} setup failed. Readiness {status}. No weaker sandbox was selected.");
                    }
                    finally { _sandboxSetup = null; }
                }
            }
            var where = Path.Combine(Environment.SystemDirectory, "where.exe");
            var pwshLookup = await ExecCapturedAsync(new[] { where, "pwsh" }, ct);
            var powershellLookup = await ExecCapturedAsync(new[] { where, "powershell" }, ct);
            var choice = CodexWindowsShell.Choose(pwshLookup.Stdout, powershellLookup.Stdout);
            if (choice.AliasStillVisible)
                throw new IOException("Project tools failed. Codex can still resolve pwsh to the WindowsApps alias. That file is a zero-byte execution alias, and the sandbox refuses to start it. Windows PowerShell 5.1 was not accepted in its place.");
            var shell = choice.Shell ?? CodexWindowsShell.SelectDefault();
            if (CodexWindowsShell.IsStorePowerShell(shell))
                throw new IOException("Project tools failed. The shell Codex would start is the WindowsApps pwsh.exe alias. That alias was not used for the readiness marker.");
            var marker = await ExecCapturedAsync(new[] { shell, "-NoProfile", "-NonInteractive", "-Command", "Write-Output 'sovereign-tools-ready'" }, ct);
            if (marker.Exit != 0 || !marker.Stdout.Contains("sovereign-tools-ready", StringComparison.Ordinal) || IsAccessDenied(marker.Stdout + marker.Stderr))
                throw new IOException(ToolsFailure(EffectiveSandbox, marker.Exit, marker.Stdout, marker.Stderr));
            ProjectToolsStatus = "ready";
        }
        catch (OperationCanceledException)
        {
            Interlocked.Exchange(ref _toolsProbeStarted, 0);
            throw;
        }
        catch (Exception ex)
        {
            var reason = ex.Message.Contains("Project tools failed", StringComparison.Ordinal) ? ex.Message : "Project tools failed. " + ex.Message;
            ProjectToolsStatus = "failed: " + CommandPresentation.Redact(reason);
        }
    }
    internal void AttachToolsDiagnostic(string stderr)
    {
        if (!ProjectToolsStatus.StartsWith("failed:", StringComparison.Ordinal) || stderr.Length == 0) return;
        ProjectToolsStatus = CommandPresentation.Redact(ProjectToolsStatus + "\n" + stderr);
    }
    private async Task<(int Exit, string Stdout, string Stderr)> ExecCapturedAsync(string[] command, CancellationToken ct)
    {
        var exec = await CallAsync("command/exec", new
        {
            command, cwd = workspace, timeoutMs = 60000, sandboxPolicy = SandboxPolicy(workspace, project)
        }, ct);
        var exit = exec.GetProperty("exitCode").GetInt32();
        var stdout = exec.TryGetProperty("stdout", out var so) ? so.GetString() ?? "" : "";
        var stderr = exec.TryGetProperty("stderr", out var se) ? se.GetString() ?? "" : "";
        if (IsAccessDenied(stdout + stderr))
            throw new IOException(ToolsFailure(EffectiveSandbox, exit, stdout, stderr));
        return (exit, stdout, stderr);
    }
    private async Task SubscribeChildAsync(string id)
    {
        try
        {
            var result = await CallAsync("thread/resume", new { threadId = id, excludeTurns = false }, _lifetime.Token);
            _state.Hydrate(result, id);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException)
        { _state.SubscriptionFailure(id, ex.Message); activity?.Invoke(new("notice", "Live helper activity subscription failed; lifecycle updates remain visible.")); }
    }
    internal static string ApprovalPolicy(CursorProjectOptions? options) => options is null || options.Mode == CursorProjectMode.Plan ? "never"
        : options.Permissions == CursorPermissionPolicy.AlwaysAllow ? "never" : "untrusted";
    internal static object SandboxPolicy(string workspace, CursorProjectOptions? options) => options is null || options.Mode == CursorProjectMode.Plan
        ? new { type = "readOnly", networkAccess = false }
        : (object)new { type = "workspaceWrite", writableRoots = new[] { workspace }, networkAccess = options.Permissions == CursorPermissionPolicy.AlwaysAllow };

    internal static Dictionary<string, object> AgentConfig(CursorProjectOptions? options)
    {
        var allowed = options?.SubagentsAllowed == true;
        var overrides = new Dictionary<string, object> { ["agents.enabled"] = allowed,
            ["features.multi_agent"] = allowed, ["agents.max_concurrent_threads_per_session"] = 3,
            // V2 counts the root; V1's public agents setting counts only helpers.
            ["features.multi_agent_v2.max_concurrent_threads_per_session"] = 4 };
        if (!allowed) overrides["features.multi_agent_v2.enabled"] = false;
        return overrides;
    }

    public async Task SendAsync(string model, string text, Action<string> delta, CancellationToken ct, IReadOnlyList<ChatImage>? images)
    {
        if (ParentThread is null)
        {
            var result = await CallAsync("thread/start", new { model, modelProvider = "openai", allowProviderModelFallback = false,
                cwd = workspace, sandbox = project is null || project.Mode == CursorProjectMode.Plan ? "read-only" : "workspace-write",
                approvalPolicy = ApprovalPolicy(project), approvalsReviewer = "user", developerInstructions = Instructions(project),
                config = AgentConfig(project),
                runtimeWorkspaceRoots = new[] { workspace } }, ct);
            _state.AssignParent(result.GetProperty("thread").GetProperty("id").GetString()!);
        }
        _delta = delta; _length = 0; _turnToken = ct;
        _permissions = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); _turn = completion;
        try
        {
            var result = await CallAsync("turn/start", new { threadId = ParentThread, model, cwd = workspace, approvalPolicy = ApprovalPolicy(project),
                approvalsReviewer = "user", sandboxPolicy = SandboxPolicy(workspace, project),
                collaborationMode = new { mode = project?.Mode == CursorProjectMode.Plan ? "plan" : "default", settings = new { model, developer_instructions = Instructions(project) } },
                input = new object[] { new { type = "text", text } }.Concat((images ?? []).Select(i => (object)new { type = "image", url = "data:" + i.MimeType + ";base64," + i.Data })).ToArray() }, ct);
            _activeTurn ??= result.GetProperty("turn").GetProperty("id").GetString();
            await completion.Task.WaitAsync(ct);
            try { await Task.WhenAll(_state.WaitForChildrenAsync(ct), Task.WhenAll(_subscriptions.Values)).WaitAsync(TimeSpan.FromSeconds(30), ct); }
            catch (Exception) when (_failure is not null) { throw new IOException("Codex disconnected before helper completion was confirmed.", _failure); }
            catch (TimeoutException ex) { throw new IOException("Codex finished the parent turn while helpers were still running. Their completion is unconfirmed; the response was stopped.", ex); }
            if (_failure is not null) throw new IOException("Codex disconnected before helper completion was confirmed.", _failure);
            if (_length == 0) throw new IOException("Codex returned no final answer. The response is incomplete.");
        }
        finally { _permissions.Cancel(); _permissions.Dispose(); _permissions = null; _delta = null; _turn = null; _activeTurn = null; }
    }
    private static string Instructions(CursorProjectOptions? options) => options is null
        ? "SI Desk conversation: answer only using supplied information. Do not run tools, access files, connectors, accounts or spawn subagents."
        : "SI Desk project session: follow workspace instructions and work on the user's request. Do not commit, push, publish or act on external accounts unless explicitly asked. " +
            (options.Mode == CursorProjectMode.Plan ? "Stay read-only. Inspect and propose a plan; never implement or edit files. The user must choose Always ask or Always allow and send an implementation instruction first. " : "Use native tools to implement requested changes and report actual results. ") +
            (options.SubagentsAllowed ? "Delegate independent work with native subagents when useful. Use up to three direct helpers concurrently and at most sixteen identities. Use default, worker or explorer roles without model overrides. Helpers inherit this chat's sandbox, mode and permissions. Assign separate implementation tasks/files, do not spawn nested helpers, wait for their results and summarize them." : "Do not spawn subagents in this chat.");

    public async Task<JsonElement> CallAsync(string method, object parameters, CancellationToken ct)
    {
        if (_failure is not null) throw new IOException("Codex connection is no longer usable. Reconnect.", _failure);
        var id = Interlocked.Increment(ref _nextId); var waiting = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiting;
        try { await WriteAsync(new { id, method, @params = parameters }, ct); return await waiting.Task.WaitAsync(RpcTimeout(method), ct); }
        finally { _pending.TryRemove(id, out _); }
    }
    private async Task WriteAsync(object value, CancellationToken ct)
    {
        await _write.WaitAsync(ct);
        try { await writer.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), ct); await writer.FlushAsync(ct); }
        finally { _write.Release(); }
    }
    public async Task<bool> RequestSteeringAsync(string instruction, CancellationToken ct)
    {
        var thread = ParentThread; var turn = _activeTurn;
        if (string.IsNullOrEmpty(thread) || string.IsNullOrEmpty(turn)) return false;
        var result = await CallAsync("turn/steer", new
        {
            threadId = thread, expectedTurnId = turn,
            input = new object[] { new { type = "text", text = instruction } }
        }, ct);
        var accepted = result.TryGetProperty("turnId", out var id) ? id.GetString() : null;
        if (!string.Equals(accepted, turn, StringComparison.Ordinal))
            throw new IOException("Codex did not accept the correction for the active turn.");
        return true;
    }
    public async Task InterruptAsync(CancellationToken ct)
    {
        var targets = _state.ActiveTurns();
        if (ParentThread is { } parent && _activeTurn is { } turn && !targets.Any(t => t.Thread == parent)) targets.Add((parent, turn));
        try
        {
            await Task.WhenAll(targets.Select(target => _failure is null
                ? CallAsync("turn/interrupt", new { threadId = target.Thread, turnId = target.Turn }, ct)
                : WriteAsync(new { id = Interlocked.Increment(ref _nextId), method = "turn/interrupt", @params = new { threadId = target.Thread, turnId = target.Turn } }, ct)));
        }
        finally { _state.Finish("Stop requested · completion unconfirmed"); }
    }
    private async Task ReadLoopAsync()
    {
        try
        {
            while (await reader.ReadLineAsync(_lifetime.Token) is { } line)
            {
                if (line.Length > 2_000_000) throw new IOException("Codex protocol message exceeded its size limit.");
                using var json = JsonDocument.Parse(line); var root = json.RootElement;
                if (root.TryGetProperty("id", out var id) && !root.TryGetProperty("method", out _))
                {
                    if (id.TryGetInt32(out var number) && _pending.TryRemove(number, out var waiting))
                    {
                        if (root.TryGetProperty("error", out var error)) waiting.TrySetException(new IOException(error.GetProperty("message").GetString()));
                        else waiting.TrySetResult(root.GetProperty("result").Clone());
                    }
                    continue;
                }
                if (!root.TryGetProperty("method", out var method)) continue;
                var name = method.GetString()!; var p = root.TryGetProperty("params", out var parameters) ? parameters : default;
                if (root.TryGetProperty("id", out var requestId)) { _ = RespondAsync(requestId.Clone(), name, p.Clone()); continue; }
                if (name == "windowsSandbox/setupCompleted") { _sandboxSetup?.TrySetResult(p.GetProperty("success").GetBoolean()); continue; }
                if (name == "item/completed") NoteCommandDenial(p);
                _state.Observe(name, p, Append);
                if (name == "error" && _state.IsParent(p) && (!p.TryGetProperty("willRetry", out var retry) || !retry.GetBoolean()))
                    _turn?.TrySetException(new IOException(p.GetProperty("error").GetProperty("message").GetString()));
                if (name == "turn/started" && _state.IsParent(p)) _activeTurn = p.GetProperty("turn").GetProperty("id").GetString();
                if (name == "turn/completed" && _state.IsParent(p))
                {
                    var turn = p.GetProperty("turn");
                    if (_activeTurn is not null && turn.GetProperty("id").GetString() != _activeTurn) continue;
                    var status = turn.GetProperty("status").GetString();
                    if (status == "completed") _turn?.TrySetResult(true);
                    else _turn?.TrySetException(new IOException("Codex turn did not complete: " + status));
                }
            }
            throw new IOException("Codex disconnected. Reconnect before sending another message.");
        }
        catch (Exception ex)
        {
            _failure = ex;
            _state.Finish("Connection interrupted · completion unconfirmed", ex.Message);
            foreach (var pending in _pending.Values) pending.TrySetException(ex);
            _turn?.TrySetException(ex);
        }
    }
    private void Append(string piece)
    {
        if (_delta is null) return;
        _length += piece.Length; if (_length > 200000) throw new IOException("Codex response exceeded the chat limit.");
        _delta(piece);
    }
    private async Task RespondAsync(JsonElement id, string method, JsonElement parameters)
    {
        try
        {
            if (method is "item/commandExecution/requestApproval" or "item/fileChange/requestApproval")
            {
                var decision = "cancel";
                var owner = _turn;
                var permissionToken = _permissions?.Token ?? _turnToken;
                var thread = parameters.GetProperty("threadId").GetString()!;
                if (thread != ParentThread && _subscriptions.TryGetValue(thread, out var subscription))
                    await subscription.WaitAsync(permissionToken);
                if (_failure is null && _turn is not null && !_turnToken.IsCancellationRequested && project is not null && project.Mode == CursorProjectMode.Agent && _state.Owns(parameters))
                {
                    var request = _state.Permission(parameters, method == "item/fileChange/requestApproval");
                    if (project.Permissions == CursorPermissionPolicy.AlwaysAllow && request.Options.Any(o => o.Id == "accept")) decision = "accept";
                    else if (project.RequestPermission is not null)
                    {
                        activity?.Invoke(new("permission", request.Tool.Title));
                        var choice = await project.RequestPermission(request, permissionToken).WaitAsync(permissionToken);
                        decision = request.Options.Any(o => o.Id == choice) ? choice! : "cancel";
                        activity?.Invoke(new("resumed", "Waiting for Codex"));
                    }
                }
                await _write.WaitAsync(_lifetime.Token);
                try
                {
                    if (_failure is not null || !ReferenceEquals(owner, _turn) || owner is null || permissionToken.IsCancellationRequested || !_state.Owns(parameters)) decision = "cancel";
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new { id, result = new { decision } }).AsMemory(), _lifetime.Token);
                    await writer.FlushAsync(_lifetime.Token);
                }
                finally { _write.Release(); }
            }
            else if (method == "item/permissions/requestApproval")
            {
                Dictionary<string, object?> grant;
                try { grant = await PermissionResultAsync(parameters); }
                catch (OperationCanceledException) { grant = DenyPermissions(parameters); }
                await WriteAsync(new { id, result = grant }, _lifetime.Token);
            }
            else await WriteAsync(new { id, error = new { code = -32601, message = "Ask the user in this conversation. This client does not implement this request." } }, _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            try { await WriteAsync(new { id, result = new { decision = "cancel" } }, _lifetime.Token); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
        catch (Exception ex) { _turn?.TrySetException(ex); }
    }

    private async Task<Dictionary<string, object?>> PermissionResultAsync(JsonElement parameters)
    {
        var permissionToken = _permissions?.Token ?? _turnToken;
        var thread = parameters.TryGetProperty("threadId", out var threadId) && threadId.ValueKind == JsonValueKind.String ? threadId.GetString() ?? "" : "";
        if (thread.Length > 0 && thread != ParentThread && _subscriptions.TryGetValue(thread, out var subscription))
            await subscription.WaitAsync(permissionToken);
        var owned = _failure is null && _turn is not null && !permissionToken.IsCancellationRequested && project is not null && _state.Owns(parameters);
        Func<CursorPermissionRequest, Task<string?>>? ask = owned && project?.RequestPermission is not null
            ? async request =>
            {
                activity?.Invoke(new("permission", request.Tool.Title));
                var choice = await project.RequestPermission!(request, permissionToken).WaitAsync(permissionToken);
                activity?.Invoke(new("resumed", "Waiting for Codex"));
                return choice;
            }
            : null;
        return await BuildPermissionGrantAsync(workspace, project, parameters, owned, ask);
    }

    private static Dictionary<string, object?> DenyPermissions(JsonElement parameters) =>
        BuildPermissionGrantAsync("", null, parameters, false, null).GetAwaiter().GetResult();

    internal static async Task<Dictionary<string, object?>> BuildPermissionGrantAsync(string workspace, CursorProjectOptions? project, JsonElement parameters, bool owned, Func<CursorPermissionRequest, Task<string?>>? ask)
    {
        var entries = ParsePermissionEntries(parameters);
        var networkRequested = ParseNetwork(parameters);
        var agent = project?.Mode == CursorProjectMode.Agent;
        var alwaysAllow = project?.Permissions == CursorPermissionPolicy.AlwaysAllow;
        var decided = new List<PermissionChoice>();
        foreach (var entry in entries)
        {
            if (entry.Access == "deny" || entry.Access is not ("read" or "write"))
            {
                decided.Add(new(entry.Path, "deny", false));
                continue;
            }
            var write = entry.Access == "write";
            if (!owned || project is null || (write && agent != true))
            {
                decided.Add(new(entry.Path, "deny", false));
                continue;
            }
            var escalation = IsEscalation(workspace, entry.Path);
            decided.Add(new(entry.Path, entry.Access, escalation || alwaysAllow != true));
        }
        var accepted = false;
        var needsPrompt = decided.Any(item => item.Prompt) || (owned && agent == true && alwaysAllow != true && networkRequested == true);
        if (needsPrompt && ask is not null)
        {
            var reason = parameters.TryGetProperty("reason", out var why) && why.ValueKind == JsonValueKind.String ? why.GetString() : null;
            var item = parameters.TryGetProperty("itemId", out var itemId) ? itemId.GetString() ?? "permissions" : "permissions";
            var thread = parameters.TryGetProperty("threadId", out var threadId) && threadId.ValueKind == JsonValueKind.String ? threadId.GetString() ?? "" : "";
            var lines = decided.Select(choice => choice.Access + " " + DescribePath(choice.Path)).ToList();
            if (networkRequested == true) lines.Add("network");
            var tool = new CursorToolActivity(thread + ":" + item, string.IsNullOrWhiteSpace(reason) ? "Additional project permissions" : reason!, "permission", "pending", string.Join("\n", lines), []);
            var choice = await ask(new(tool, [new("accept", "Allow once", "allow_once"), new("decline", "Reject", "reject_once")]));
            accepted = choice == "accept";
        }
        var output = new List<object>();
        foreach (var choice in decided)
        {
            var access = choice.Prompt && !accepted ? "deny" : choice.Access;
            output.Add(new Dictionary<string, object> { ["access"] = access, ["path"] = choice.Path });
        }
        var permissions = new Dictionary<string, object?> { ["fileSystem"] = new Dictionary<string, object> { ["entries"] = output } };
        if (networkRequested is bool requested)
            permissions["network"] = new Dictionary<string, object> { ["enabled"] = requested && owned && agent == true && (alwaysAllow == true || accepted) };
        return new Dictionary<string, object?> { ["permissions"] = permissions, ["scope"] = "turn" };
    }

    private static List<(JsonElement Path, string Access)> ParsePermissionEntries(JsonElement parameters)
    {
        var entries = new List<(JsonElement Path, string Access)>();
        if (!parameters.TryGetProperty("permissions", out var permissions) || permissions.ValueKind != JsonValueKind.Object
            || !permissions.TryGetProperty("fileSystem", out var fileSystem) || fileSystem.ValueKind != JsonValueKind.Object)
            return entries;
        if (fileSystem.TryGetProperty("entries", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in list.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("path", out var path)) continue;
                var access = entry.TryGetProperty("access", out var mode) && mode.ValueKind == JsonValueKind.String ? mode.GetString() ?? "deny" : "deny";
                entries.Add((path.Clone(), access));
            }
        }
        AddLegacy(fileSystem, "read", "read");
        AddLegacy(fileSystem, "write", "write");
        return entries;
        void AddLegacy(JsonElement fileSystemNode, string name, string access)
        {
            if (!fileSystemNode.TryGetProperty(name, out var paths) || paths.ValueKind != JsonValueKind.Array) return;
            foreach (var path in paths.EnumerateArray())
            {
                if (path.ValueKind != JsonValueKind.String) continue;
                entries.Add((JsonSerializer.SerializeToElement(new { type = "path", path = path.GetString() }), access));
            }
        }
    }

    private static bool? ParseNetwork(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("permissions", out var permissions) || permissions.ValueKind != JsonValueKind.Object
            || !permissions.TryGetProperty("network", out var network) || network.ValueKind != JsonValueKind.Object
            || !network.TryGetProperty("enabled", out var enabled)) return null;
        return enabled.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => false };
    }

    private static string DescribePath(JsonElement path)
    {
        var type = path.TryGetProperty("type", out var kind) ? kind.GetString() : null;
        if (type == "path" && path.TryGetProperty("path", out var text)) return text.GetString() ?? path.GetRawText();
        if (type == "glob_pattern" && path.TryGetProperty("pattern", out var pattern)) return pattern.GetString() ?? path.GetRawText();
        if (type == "special") return "special " + path.GetRawText();
        return path.GetRawText();
    }

    private static bool IsEscalation(string workspace, JsonElement path)
    {
        var type = path.TryGetProperty("type", out var kind) ? kind.GetString() : null;
        if (type == "path") return !IsInside(workspace, path.TryGetProperty("path", out var text) ? text.GetString() ?? "" : "");
        if (type == "glob_pattern")
        {
            var pattern = path.TryGetProperty("pattern", out var text) ? text.GetString() ?? "" : "";
            if (pattern.Contains("..", StringComparison.Ordinal) || pattern.Contains(':') || pattern.StartsWith('/') || pattern.StartsWith('\\'))
                return !IsInside(workspace, pattern);
            return false;
        }
        if (type == "special" && path.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Object)
        {
            var special = value.TryGetProperty("kind", out var specialKind) ? specialKind.GetString() : null;
            if (special != "project_roots") return true;
            if (value.TryGetProperty("subpath", out var sub) && sub.ValueKind == JsonValueKind.String)
            {
                var text = sub.GetString() ?? "";
                return text.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(text);
            }
            return false;
        }
        return true;
    }

    private static bool IsInside(string workspace, string path)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
            var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
            return full.Equals(root, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private void NoteCommandDenial(JsonElement parameters)
    {
        if (project is null || !parameters.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) return;
        if (!item.TryGetProperty("type", out var type) || type.GetString() != "commandExecution") return;
        var status = item.TryGetProperty("status", out var state) ? state.GetString() : "";
        if (status != "failed") return;
        var evidence = (item.TryGetProperty("aggregatedOutput", out var output) ? output.GetString() : "") ?? "";
        evidence += (item.TryGetProperty("stderr", out var stderr) ? stderr.GetString() : "") ?? "";
        if (!IsAccessDenied(evidence)) return;
        var exit = item.TryGetProperty("exitCode", out var code) && code.TryGetInt32(out var number) ? number : -1;
        ProjectToolsStatus = "failed: " + CommandPresentation.Redact(ToolsFailure(EffectiveSandbox, exit, "", evidence));
    }

    private static string? FindSandbox(JsonElement element)
    {
        string? found = null;
        Walk(element);
        return found;
        void Walk(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in node.EnumerateObject())
                {
                    if (prop.NameEquals("sandbox") && prop.Value.ValueKind == JsonValueKind.String && prop.Value.GetString() is "elevated" or "unelevated" or "mxc")
                        found = prop.Value.GetString();
                    else Walk(prop.Value);
                }
            }
            else if (node.ValueKind == JsonValueKind.Array)
                foreach (var item in node.EnumerateArray()) Walk(item);
        }
    }

    private static bool IsAccessDenied(string evidence) =>
        evidence.Contains("CreateProcessAsUserW", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("Access is denied", StringComparison.OrdinalIgnoreCase);

    private static string ToolsFailure(string? sandbox, int exit, string stdout, string stderr)
    {
        var detail = $"Project tools failed. Sandbox {sandbox}. Exit {exit}. stdout {Clip(stdout)} stderr {Clip(stderr)} This command was not started again.";
        if (!IsAccessDenied(stdout + stderr)) return detail;
        return detail + " Windows refused to start the WindowsApps pwsh.exe alias. A Windows PowerShell 5.1 check is not proof that this alias can run.";
    }

    private static string Clip(string value)
    {
        var trimmed = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed[..500];
    }

    private sealed record PermissionChoice(JsonElement Path, string Access, bool Prompt);

    public void Dispose() { _lifetime.Cancel(); _state.Finish("Disconnected · completion unconfirmed"); }
}
