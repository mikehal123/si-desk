using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Sovereign.Desktop.Phone;

public sealed class PhoneTlsMaterial
{
    public required X509Certificate2 Certificate { get; init; }
    public required string HostName { get; init; }
}

/// <summary>Loopback HTTP or Tailscale-only HTTPS on port 8789. Off unless Settings enables it. Does not open a public tunnel.</summary>
public sealed class PhoneAccessHost : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly PhoneDeviceStore _store;
    private readonly IPhoneChatBridge _chats;
    private readonly PhoneApprovalGate _approvals;
    private readonly string _webRoot;
    private readonly object _notesGate = new();
    private readonly List<PhoneNotification> _notes = [];
    private readonly Dictionary<string, bool> _wasWorking = new();
    private readonly HashSet<string> _announcedApprovals = new();
    private readonly ConcurrentQueue<string> _log = new();
    private long _cursor;
    private WebApplication? _app;
    private CancellationTokenSource? _observe;
    private PhoneBindMode _mode = PhoneBindMode.Loopback;
    private X509Certificate2? _certificate;
    public string? Origin { get; private set; }
    public string? LastError { get; private set; }
    public bool Running => _app is not null;

    public PhoneAccessHost(PhoneDeviceStore store, IPhoneChatBridge chats, PhoneApprovalGate approvals, string webRoot)
    {
        _store = store;
        _chats = chats;
        _approvals = approvals;
        _webRoot = webRoot;
    }

    public static string FindWebRoot()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "phone", "www"),
            Path.Combine(AppContext.BaseDirectory, "www")
        };
        return candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];
    }

    public async Task<string?> StartAsync(CancellationToken ct = default)
    {
        var settings = _store.Settings;
        if (!settings.Enabled) return "Phone access is off.";
        var selected = PhoneBindAddress.Select(settings.Bind, PhoneBindAddress.MachineAddresses());
        if (selected is null)
            return settings.Bind == PhoneBindMode.Tailscale
                ? "No Tailscale address (100.x) is on this PC. Phone access stayed off."
                : "Loopback is unavailable. Phone access stayed off.";
        if (!PhoneBindAddress.ConnectionAllowed(selected, settings.Bind))
            return "Refusing an address that is not loopback or Tailscale. Phone access stayed off.";
        X509Certificate2? certificate = null;
        var hostName = "127.0.0.1";
        if (settings.Bind == PhoneBindMode.Tailscale)
        {
            var tls = await PhoneTailscaleCertificate.TryCreateAsync(_store.Profile, ct);
            if (tls is null) return "Tailscale certificate is unavailable. Phone access stayed off.";
            certificate = tls.Certificate;
            hostName = tls.HostName;
        }
        return await ListenAsync(selected, PhoneBindAddress.Port, settings.Bind, certificate, hostName, ct);
    }

    internal Task<string?> ListenForChecksAsync(IPAddress address, int port, PhoneBindMode mode, X509Certificate2? certificate, CancellationToken ct) =>
        ListenAsync(address, port, mode, certificate, certificate is null ? "127.0.0.1" : "phone.local", ct);

    public string? IssuePairingCode() => Running ? _store.BeginPairing() : null;

    public bool Revoke(string id) => _store.Revoke(id);

    public IReadOnlyList<string> LogLines() => _log.ToArray();

    private async Task<string?> ListenAsync(IPAddress address, int port, PhoneBindMode mode, X509Certificate2? certificate, string hostName, CancellationToken ct)
    {
        if (!PhoneBindAddress.ConnectionAllowed(address, mode == PhoneBindMode.Loopback ? PhoneBindMode.Loopback : mode) && !(mode == PhoneBindMode.Tailscale && PhoneBindAddress.IsLoopbackV4(address) && port == 0))
            return "Refusing an address that is not loopback or Tailscale.";
        if (mode == PhoneBindMode.Tailscale && certificate is null && port == PhoneBindAddress.Port)
            return "Tailscale mode requires HTTPS. Phone access stayed off.";
        await StopAsync();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(PhoneAccessHost).Assembly.FullName });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new PhoneLogProvider(_log));
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 65536;
            options.Limits.MaxConcurrentConnections = 32;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            if (certificate is null) options.Listen(address, port);
            else options.Listen(address, port, listen => listen.UseHttps(certificate));
        });
        var app = builder.Build();
        _mode = mode;
        _certificate = certificate;
        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; connect-src 'self'; manifest-src 'self'; worker-src 'self'";
            if (!context.Request.Path.StartsWithSegments("/sw.js")) context.Response.Headers.CacheControl = "no-store";
            if (!PhoneBindAddress.ConnectionAllowed(context.Connection.LocalIpAddress, _mode)) { context.Response.StatusCode = 403; return; }
            if (!OriginAllowed(context.Request)) { context.Response.StatusCode = 403; return; }
            try { await next(context); }
            catch (Exception ex) when (ex is JsonException or BadHttpRequestException)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { error = "Bad request." }, Json);
            }
        });
        MapSite(app);
        MapApi(app);
        try { await app.StartAsync(ct); }
        catch (Exception error)
        {
            LastError = "Phone access could not start. The address, port or certificate may be unavailable.";
            _log.Enqueue(PhoneLog.Clean(error.GetType().Name));
            await app.DisposeAsync();
            _certificate?.Dispose(); _certificate = null;
            return LastError;
        }
        var bound = app.Urls.Select(item => new Uri(item)).FirstOrDefault();
        if (bound is null)
        {
            await app.StopAsync(ct);
            await app.DisposeAsync();
            return "Phone access could not bind.";
        }
        _app = app;
        Origin = (certificate is null ? "http://" : "https://") + hostName + ":" + bound.Port;
        LastError = null;
        _observe = new CancellationTokenSource();
        _ = ObserveAsync(_observe.Token);
        return null;
    }

    private void MapSite(WebApplication app)
    {
        app.MapGet("/", () => Serve("index.html"));
        foreach (var file in new[] { "app.js", "app.css", "manifest.webmanifest", "sw.js", "icon-192.png", "icon-512.png" })
        {
            var name = file;
            app.MapGet("/" + name, () => Serve(name));
        }
    }

    private IResult Serve(string name)
    {
        var path = Path.GetFullPath(Path.Combine(_webRoot, name));
        var root = Path.GetFullPath(_webRoot);
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return Results.NotFound();
        var type = Path.GetExtension(name) switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".webmanifest" => "application/manifest+json",
            ".png" => "image/png",
            _ => "application/octet-stream"
        };
        return Results.File(path, type);
    }

    private void MapApi(WebApplication app)
    {
        app.MapPost("/api/pair", async (HttpContext context) =>
        {
            var body = await context.Request.ReadFromJsonAsync<PairBody>(Json, context.RequestAborted);
            var result = _store.Redeem(body?.Code, body?.Name);
            return result.Status switch
            {
                PhonePairStatus.Ok => Results.Json(new { token = result.Token, deviceId = result.DeviceId, name = result.Name }, Json),
                PhonePairStatus.RateLimited => Results.Json(new { error = "Too many pairing attempts. Wait and try a new code." }, Json, statusCode: 429),
                PhonePairStatus.Expired => Results.Json(new { error = "That pairing code expired. Create a new one on the desktop." }, Json, statusCode: 401),
                _ => Results.Json(new { error = "Pairing code was not accepted." }, Json, statusCode: 401)
            };
        });
        app.MapGet("/api/session", (HttpContext context) => Authed(context, out var device)
            ? Results.Json(new { notify = _store.Settings.Notify, name = device!.Name }, Json)
            : Unauthorized());
        app.MapGet("/api/chats", async (HttpContext context) =>
        {
            if (!Authed(context, out _)) return Unauthorized();
            var chats = await _chats.ListAsync(context.RequestAborted);
            return Results.Json(new { chats }, Json);
        });
        app.MapGet("/api/chats/{id}", async (string id, HttpContext context) =>
        {
            if (!Authed(context, out _)) return Unauthorized();
            var chat = await _chats.GetAsync(id, context.RequestAborted);
            return chat is null ? Results.NotFound() : Results.Json(chat, Json);
        });
        app.MapGet("/api/chats/{id}/events", async (string id, HttpContext context) =>
        {
            if (!Authed(context, out _)) { context.Response.StatusCode = 401; return; }
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-store";
            while (!context.RequestAborted.IsCancellationRequested)
            {
                // Revoking a device also closes its existing event stream.
                if (!Authed(context, out _)) return;
                var chat = await _chats.GetAsync(id, context.RequestAborted);
                var approvals = _approvals.Snapshot();
                await context.Response.WriteAsync("event: snapshot\ndata: " + JsonSerializer.Serialize(new { chat, approvals }, Json) + "\n\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                await Task.Delay(500, context.RequestAborted);
            }
        });
        app.MapPost("/api/chats/{id}/messages", async (string id, HttpContext context) =>
        {
            if (!Authed(context, out var device)) return Unauthorized();
            if (!_store.AllowSend(device!.Id)) return Results.Json(new { error = "Sending too quickly." }, Json, statusCode: 429);
            var body = await context.Request.ReadFromJsonAsync<SendBody>(Json, context.RequestAborted);
            var text = body?.Text?.Trim() ?? "";
            if (text.Length == 0 || text.Length > 16000) return Results.Json(new { error = "Write a message of 1–16000 characters." }, Json, statusCode: 400);
            var result = await _chats.SendAsync(id, text, context.RequestAborted);
            return result.Accepted ? Results.Json(new { accepted = true }, Json) : Results.Json(new { error = result.Error ?? "Not sent." }, Json, statusCode: 409);
        });
        app.MapPost("/api/chats/{id}/stop", async (string id, HttpContext context) =>
        {
            if (!Authed(context, out _)) return Unauthorized();
            var stopped = await _chats.StopAsync(id, context.RequestAborted);
            return Results.Json(new { stopped }, Json);
        });
        app.MapGet("/api/approvals", (HttpContext context) => Authed(context, out _)
            ? Results.Json(new { approvals = _approvals.Snapshot() }, Json)
            : Unauthorized());
        app.MapPost("/api/approvals/{id}", async (string id, HttpContext context) =>
        {
            if (!Authed(context, out _)) return Unauthorized();
            var body = await context.Request.ReadFromJsonAsync<DecisionBody>(Json, context.RequestAborted);
            if (body?.Decision is not ("allow" or "deny")) return Results.Json(new { error = "Choose allow or deny." }, Json, statusCode: 400);
            var result = _approvals.Decide(id, body.Decision == "allow", body.Confirm);
            if (result.Accepted) return Results.Json(new { accepted = true }, Json);
            var status = result.Error == "confirm_required" ? 409 : 404;
            return Results.Json(new { error = result.Error }, Json, statusCode: status);
        });
        app.MapGet("/api/notifications", (long? after, HttpContext context) =>
        {
            if (!Authed(context, out _)) return Unauthorized();
            PhoneNotification[] items;
            long cursor;
            lock (_notesGate)
            {
                cursor = _cursor;
                items = _notes.Where(item => item.Cursor > (after ?? 0)).Take(50).ToArray();
            }
            return Results.Json(new { cursor, items }, Json);
        });
    }

    private bool Authed(HttpContext context, out PhoneDeviceRecord? device)
    {
        device = null;
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        var token = header["Bearer ".Length..].Trim();
        return _store.Authorize(token, out device);
    }

    private static IResult Unauthorized() => Results.Json(new { error = "Sign in from the desktop pairing code." }, Json, statusCode: 401);

    private static bool OriginAllowed(HttpRequest request)
    {
        if (!request.Headers.ContainsKey("Origin")) return true;
        var origin = request.Headers.Origin.ToString();
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        var port = request.Host.Port ?? (request.IsHttps ? 443 : 80);
        return uri.Scheme == request.Scheme && uri.Host.Equals(request.Host.Host, StringComparison.OrdinalIgnoreCase) && uri.Port == port;
    }

    private async Task ObserveAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var chats = await _chats.ListAsync(ct);
                lock (_notesGate)
                {
                    foreach (var chat in chats)
                    {
                        if (_wasWorking.TryGetValue(chat.Id, out var was) && was && !chat.Working)
                            AddNote("finished", chat.Id, chat.Title + " finished.");
                        _wasWorking[chat.Id] = chat.Working;
                    }
                    var pending = _approvals.Snapshot();
                    var live = pending.Select(item => item.Id).ToHashSet();
                    foreach (var item in pending)
                        if (_announcedApprovals.Add(item.Id))
                            AddNote("approval", "", item.Origin + " needs approval: " + item.Title);
                    _announcedApprovals.RemoveWhere(id => !live.Contains(id));
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { /* The window may be closing. The next tick tries again. */ }
            try { await Task.Delay(2000, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void AddNote(string kind, string chatId, string text)
    {
        var note = new PhoneNotification(++_cursor, kind, chatId, PhoneLog.Clean(text));
        _notes.Add(note);
        if (_notes.Count > 100) _notes.RemoveRange(0, _notes.Count - 100);
    }

    public async Task StopAsync()
    {
        var observe = _observe;
        _observe = null;
        if (observe is not null) { await observe.CancelAsync(); observe.Dispose(); }
        var app = _app;
        _app = null;
        Origin = null;
        if (app is not null) await app.StopAsync();
        if (app is not null) await app.DisposeAsync();
        _certificate?.Dispose();
        _certificate = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private sealed record PairBody(string? Code, string? Name);
    private sealed record SendBody(string? Text);
    private sealed record DecisionBody(string? Decision, bool Confirm);

    private sealed class PhoneLogProvider(ConcurrentQueue<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new PhoneLogger(sink);
        public void Dispose() { }
    }

    private sealed class PhoneLogger(ConcurrentQueue<string> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var line = PhoneLog.Clean(formatter(state, exception));
            if (line.Length == 0) return;
            sink.Enqueue(line);
            while (sink.Count > 40 && sink.TryDequeue(out _)) { }
        }
    }
}

public static class PhoneTailscaleCertificate
{
    public static async Task<PhoneTlsMaterial?> TryCreateAsync(string profile, CancellationToken ct)
    {
        var executable = FindTailscale();
        if (executable is null) return null;
        var status = await RunAsync(executable, ["status", "--json"], ct);
        if (status.Code != 0 || string.IsNullOrWhiteSpace(status.Text)) return null;
        string? dns;
        try
        {
            using var document = JsonDocument.Parse(status.Text);
            dns = document.RootElement.GetProperty("Self").GetProperty("DNSName").GetString()?.Trim().TrimEnd('.');
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
        if (string.IsNullOrWhiteSpace(dns)) return null;
        var folder = Path.Combine(profile, "phone", "tls");
        Directory.CreateDirectory(folder);
        var certificatePath = Path.Combine(folder, "tailscale.crt");
        var keyPath = Path.Combine(folder, "tailscale.key");
        var issued = await RunAsync(executable, ["cert", "--cert-file", certificatePath, "--key-file", keyPath, dns], ct);
        if (issued.Code != 0 || !File.Exists(certificatePath) || !File.Exists(keyPath)) return null;
        using var created = X509Certificate2.CreateFromPemFile(certificatePath, keyPath);
        var portable = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
        return new PhoneTlsMaterial { Certificate = portable, HostName = dns };
    }

    private static string? FindTailscale()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };
        foreach (var root in roots)
        {
            var candidate = Path.Combine(root, "Tailscale", "tailscale.exe");
            if (File.Exists(candidate)) return candidate;
        }
        var path = Environment.GetEnvironmentVariable("PATH");
        if (path is null) return null;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), "tailscale.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static async Task<(int Code, string Text)> RunAsync(string executable, string[] args, CancellationToken ct)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info);
        if (process is null) return (1, "");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(stdoutTask, stderrTask);
        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (process.ExitCode, PhoneLog.Clean(stdout + "\n" + stderr));
    }
}
