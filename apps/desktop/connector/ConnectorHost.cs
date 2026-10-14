using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Sovereign.Desktop.Connector;

/// <summary>Loopback MCP server for Grok Bot. Off until Settings enables it. Never binds a public address.</summary>
public sealed class ConnectorHost : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly IConnectorSecrets _secrets;
    private readonly IGrokBotDesk _desk;
    private readonly ConnectorAudit _audit;
    private readonly ConnectorRateGate _rate;
    private WebApplication? _app;
    private ConnectorBindMode _mode = ConnectorBindMode.Loopback;
    public static readonly TimeSpan BindRetryInterval = TimeSpan.FromSeconds(5);
    public bool Running => _app is not null;
    public string? Url { get; private set; }
    /// <summary>The last start failed because another listener already holds the address and port.</summary>
    public bool PortBusy { get; private set; }

    public ConnectorHost(IConnectorSecrets secrets, IGrokBotDesk desk, ConnectorAudit audit, ConnectorRateGate? rate = null)
    {
        _secrets = secrets;
        _desk = desk;
        _audit = audit;
        _rate = rate ?? new ConnectorRateGate();
    }

    public async Task<string?> StartAsync(bool enabled, ConnectorBindMode mode, CancellationToken ct = default, Func<CancellationToken, Task<(IPAddress? Address, string? Error)>>? tailscale = null)
    {
        PortBusy = false;
        if (!enabled) return "Grok Bot connector is off.";
        if (mode == ConnectorBindMode.Loopback)
            return await ListenAsync(IPAddress.Loopback, ConnectorBind.Port, mode, ct);
        var (address, lookupError) = await (tailscale ?? ConnectorBind.QueryTailscaleIpv4Async)(ct);
        if (address is null)
            return lookupError ?? "No Tailscale address is on this PC. The connector stayed off.";
        if (!ConnectorBind.IsTailscale(address))
            return "tailscale ip -4 returned " + address + ", which is not a Tailscale address. The connector stayed off.";
        return await ListenAsync(address, ConnectorBind.Port, mode, ct);
    }

    public Task<string?> ListenForChecksAsync(IPAddress address, int port, ConnectorBindMode mode, CancellationToken ct = default) =>
        ListenAsync(address, port, mode, ct);

    /// <summary>
    /// Like <see cref="StartAsync"/>, but while the port is taken it waits and tries again until it binds or <paramref name="ct"/> is cancelled.
    /// <paramref name="busy"/> runs once, on the first busy attempt. Other failures return right away.
    /// </summary>
    public Task<string?> StartWithRetryAsync(bool enabled, ConnectorBindMode mode, Action<string>? busy, CancellationToken ct = default, TimeSpan? interval = null, Func<CancellationToken, Task<(IPAddress? Address, string? Error)>>? tailscale = null) =>
        RetryAsync(token => StartAsync(enabled, mode, token, tailscale), ConnectorBind.Port, busy, interval, ct);

    public Task<string?> ListenWithRetryForChecksAsync(IPAddress address, int port, ConnectorBindMode mode, Action<string>? busy, TimeSpan interval, CancellationToken ct = default) =>
        RetryAsync(token => ListenAsync(address, port, mode, token), port, busy, interval, ct);

    private async Task<string?> RetryAsync(Func<CancellationToken, Task<string?>> attempt, int port, Action<string>? busy, TimeSpan? interval, CancellationToken ct)
    {
        var told = false;
        while (true)
        {
            var error = await attempt(ct);
            if (error is null || !PortBusy || ct.IsCancellationRequested) return error;
            if (!told)
            {
                told = true;
                busy?.Invoke(ConnectorBind.BusyStatus(port));
            }
            await Task.Delay(interval ?? BindRetryInterval, ct);
        }
    }

    private async Task<string?> ListenAsync(IPAddress address, int port, ConnectorBindMode mode, CancellationToken ct)
    {
        PortBusy = false;
        if (!ConnectorBind.ConnectionAllowed(address, mode))
            return "Refusing an address that is not loopback or Tailscale.";
        await StopAsync();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(ConnectorHost).Assembly.FullName });
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.None);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 32768;
            options.Limits.MaxConcurrentConnections = 16;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            options.Listen(address, port);
        });
        var app = builder.Build();
        _mode = mode;
        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers.CacheControl = "no-store";
            if (!ConnectorBind.ConnectionAllowed(context.Connection.LocalIpAddress, _mode))
            {
                context.Response.StatusCode = 403;
                return;
            }
            try { await next(context); }
            catch (Exception ex) when (ex is JsonException or BadHttpRequestException)
            {
                context.Response.StatusCode = 400;
            }
        });
        app.MapPost("/mcp", HandleAsync);
        try { await app.StartAsync(ct); }
        catch (Exception ex)
        {
            await ReleaseAsync(app);
            PortBusy = ConnectorBind.IsAddressInUse(ex);
            return "Grok Bot connector could not start. " + ConnectorBind.OneLine(ex.Message);
        }
        var boundPort = BoundPort(app, port);
        if (boundPort is null || !await WaitUntilListening(address, boundPort.Value, ct))
        {
            var reported = app.Urls.Count == 0 ? "" : " Server reported " + string.Join(", ", app.Urls) + ".";
            await ReleaseAsync(app);
            return "Grok Bot connector did not bind " + address + ":" + (boundPort?.ToString() ?? port.ToString()) + "." + reported;
        }
        _app = app;
        Url = "http://" + address + ":" + boundPort + "/mcp";
        return null;
    }

    public bool IsBound
    {
        get
        {
            if (_app is null || Url is null || !Uri.TryCreate(Url, UriKind.Absolute, out var uri)) return false;
            return IPAddress.TryParse(uri.Host, out var address) && ConnectorBind.IsListening(address, uri.Port);
        }
    }

    private static int? BoundPort(WebApplication app, int requested)
    {
        if (requested != 0) return requested;
        foreach (var item in app.Urls)
            if (Uri.TryCreate(item, UriKind.Absolute, out var uri) && uri.Port > 0) return uri.Port;
        return null;
    }

    private static async Task<bool> WaitUntilListening(IPAddress address, int port, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (ConnectorBind.IsListening(address, port)) return true;
            await Task.Delay(50, ct);
        }
        return ConnectorBind.IsListening(address, port);
    }

    private static async Task ReleaseAsync(WebApplication app)
    {
        try { await app.DisposeAsync(); }
        catch (Exception) { }
    }

    private async Task HandleAsync(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        var presented = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..].Trim() : "";
        if (!ConnectorToken.Matches(_secrets.Current(), presented))
        {
            _audit.Write("rejected", null);
            context.Response.StatusCode = 401;
            return;
        }
        try { _desk.NoteGrokbotPoll(); } catch (Exception) { }
        var message = await context.Request.ReadFromJsonAsync<JsonElement>(Json, context.RequestAborted);
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("method", out var methodElement))
        {
            context.Response.StatusCode = 400;
            return;
        }
        var method = methodElement.GetString() ?? "";
        JsonElement id = default;
        var hasId = message.TryGetProperty("id", out id);
        if (method == "notifications/initialized" && !hasId) return;
        var result = Dispatch(method, message);
        if (!hasId) return;
        await context.Response.WriteAsJsonAsync(new { jsonrpc = "2.0", id, result }, Json, context.RequestAborted);
    }

    private object Dispatch(string method, JsonElement message)
    {
        if (method == "initialize")
            return new
            {
                protocolVersion = "2025-03-26",
                capabilities = new { tools = new { } },
                serverInfo = new { name = "SI Desk", version = _desk.Status().Version }
            };
        if (method == "tools/list") return new { tools = ToolList() };
        if (method != "tools/call") return Error("Unknown method.");
        if (!message.TryGetProperty("params", out var parameters) || !parameters.TryGetProperty("name", out var nameElement))
            return Error("Missing tool name.");
        var name = nameElement.GetString() ?? "";
        var arguments = parameters.TryGetProperty("arguments", out var args) ? args : default;
        if (arguments.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var parsed = JsonDocument.Parse(arguments.GetString() ?? "");
                if (parsed.RootElement.ValueKind == JsonValueKind.Object)
                    arguments = parsed.RootElement.Clone();
            }
            catch (JsonException) { }
        }
        string Arg(string key)
        {
            if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(key, out var value))
                return "";
            if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? "";
            if (value.ValueKind == JsonValueKind.Number) return value.GetRawText();
            return "";
        }
        int Limit()
        {
            if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("limit", out var value) && value.TryGetInt32(out var limit))
                return Math.Clamp(limit, 1, 50);
            return 20;
        }
        int? ArgInt(string key)
        {
            if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null)
                return null;
            if (value.TryGetInt32(out var number)) return number;
            throw new ArgumentException(key + " must be an integer.");
        }
        if (name == "app_screenshot") return Screenshot(arguments);
        try
        {
            string text;
            object[]? inboxContent = null;
            if (name == "grokbot_inbox_list")
            {
                var rows = _desk.ListGrokbotInbox(string.IsNullOrWhiteSpace(Arg("since")) ? null : Arg("since"));
                text = InboxText(rows);
                inboxContent = InboxContent(text, rows);
            }
            else text = name switch
            {
                "read_chat" => Text(_desk.ReadChat(string.IsNullOrWhiteSpace(Arg("conversation")) ? null : Arg("conversation"), Limit())),
                "list_chats" => Text(_desk.ListChats().Select(item => item.Id + "\t" + item.Title)),
                "send_to_chat" => Send(Arg("conversation"), Arg("text")),
                "read_shared_room" => Text(_desk.ReadSharedRoom(Limit())),
                "post_shared_room" => Post(Arg("text")),
                "list_subagents" => Text(_desk.ListSubagents(Arg("conversation")).Select(item => item.Id + "\t" + item.Name + "\t" + item.State)),
                "read_subagent" => ReadSubagent(Arg("id")),
                "status" => Status(),
                "grokbot_inbox_reply" => InboxReply(Arg("message_id"), Arg("text")),
                "app_ui_tree" => _desk.GrokbotUiTree(),
                "app_click" => _desk.GrokbotClick(Blank(Arg("control_id")), ArgInt("x"), ArgInt("y")),
                "app_type" => _desk.GrokbotType(Arg("control_id"), Arg("text")),
                "app_key" => _desk.GrokbotKey(Arg("keys")),
                "app_navigate" => _desk.GrokbotNavigate(Arg("page")),
                "read_swarm_board" => _desk.ReadSwarmBoard(BlankArg(Arg("swarm_id")), BlankArg(Arg("run_id"))),
                "swarm_submit_result" => SubmitSwarm(Arg("task_id"), Arg("result"), Arg("proof"), BlankArg(Arg("swarm_id")), BlankArg(Arg("run_id"))),
                "swarm_continue" => SwarmContinue.Apply(_desk, BlankArg(Arg("swarm_id")), BlankArg(Arg("run_id")), Arg("verdict"), Arg("notes")).ToolText(),
                "swarm_run_status" => SwarmContinue.Status(_desk, BlankArg(Arg("swarm_id")), BlankArg(Arg("run_id"))),
                _ => ""
            };
            if (!KnownTool(name))
            {
                _audit.Write("rejected", null);
                TryRecord(name, ArgSummary(arguments), "Unknown tool.");
                return ToolError("Unknown tool.");
            }
            var auditTarget = name is "read_chat" or "send_to_chat" or "list_subagents" ? Arg("conversation") : name == "read_subagent" ? Arg("id") : name == "grokbot_inbox_reply" ? Arg("message_id") : name == "swarm_submit_result" ? Arg("task_id") : name is "read_swarm_board" or "swarm_continue" or "swarm_run_status" ? Arg("swarm_id") : null;
            _audit.Write(name, Redact(auditTarget));
            TryRecord(name, ArgSummary(arguments), text);
            if (inboxContent is not null)
                return new { content = inboxContent, isError = false };
            return new { content = new[] { new { type = "text", text } }, isError = false };
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or KeyNotFoundException)
        {
            _audit.Write(name, null);
            TryRecord(name, ArgSummary(arguments), ex.Message);
            return ToolError(ex.Message);
        }
    }

    private object Screenshot(JsonElement arguments)
    {
        try
        {
            var shot = _desk.CaptureGrokbotScreenshot();
            var text = string.IsNullOrWhiteSpace(shot.Text) ? "PNG" : shot.Text;
            _audit.Write("app_screenshot", null);
            TryRecord("app_screenshot", ArgSummary(arguments), text);
            if (shot.Bytes is { Length: > 0 } png)
                return new { content = new object[] { new { type = "text", text }, new { type = "image", mimeType = "image/png", data = Convert.ToBase64String(png) } }, isError = false };
            return new { content = new[] { new { type = "text", text } }, isError = false };
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or KeyNotFoundException)
        {
            _audit.Write("app_screenshot", null);
            TryRecord("app_screenshot", ArgSummary(arguments), ex.Message);
            return ToolError(ex.Message);
        }
    }

    private static string InboxText(IReadOnlyList<GrokbotMail> rows) =>
        string.Join("\n", rows.Select(mail => mail.Id + "\t" + mail.Status + "\t" + (mail.AskToAct ? "act" : "reply") + "\t" + mail.Text.Replace('\n', ' ')));

    private static object[] InboxContent(string text, IReadOnlyList<GrokbotMail> rows)
    {
        var content = new List<object> { new { type = "text", text } };
        foreach (var mail in rows)
        {
            if (mail.Image is not { Length: > 0 } png) continue;
            content.Add(new { type = "text", text = mail.Id });
            content.Add(new { type = "image", mimeType = "image/png", data = Convert.ToBase64String(png) });
        }
        return content.ToArray();
    }

    private string InboxReply(string id, string text)
    {
        var mail = _desk.ReplyGrokbotInbox(id, text);
        return "Answered\t" + mail.Id;
    }

    private void TryRecord(string tool, string args, string result)
    {
        try { _desk.RecordGrokbotTool(tool, Redact(args), Redact(result)); }
        catch (Exception) { }
    }

    private string Redact(string? value)
    {
        var text = value ?? "";
        var token = _secrets.Current();
        if (!string.IsNullOrEmpty(token)) text = text.Replace(token, "[redacted]", StringComparison.Ordinal);
        return text;
    }

    private string ArgSummary(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) return "-";
        var parts = new List<string>();
        foreach (var property in arguments.EnumerateObject())
        {
            var value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? "",
                JsonValueKind.Number => property.Value.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => property.Value.ValueKind.ToString()
            };
            if (property.Name is "text") value = "<" + value.Length + " chars>";
            value = Redact(value.Replace('\n', ' ').Replace('\r', ' '));
            if (value.Length > 40) value = value[..40];
            parts.Add(property.Name + "=" + value);
        }
        var text = string.Join(" ", parts);
        return text.Length <= 120 ? text : text[..120];
    }

    private static bool KnownTool(string name) => name is
        "read_chat" or "list_chats" or "send_to_chat" or "read_shared_room" or "post_shared_room" or
        "list_subagents" or "read_subagent" or "status" or "grokbot_inbox_list" or "grokbot_inbox_reply" or
        "app_ui_tree" or "app_click" or "app_type" or "app_key" or "app_navigate" or
        "read_swarm_board" or "swarm_submit_result" or "swarm_continue" or "swarm_run_status";

    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private string Send(string conversation, string text)
    {
        if (string.IsNullOrWhiteSpace(conversation)) throw new ArgumentException("Choose a conversation.");
        if (!_rate.TryPost()) throw new InvalidOperationException("Rate limit: 20 messages a minute.");
        _desk.SendToChat(conversation, text);
        return "Sent to " + conversation + " as Grok Bot.";
    }

    private string Post(string text)
    {
        if (!_rate.TryPost()) throw new InvalidOperationException("Rate limit: 20 messages a minute.");
        _desk.PostSharedRoom(text);
        return "Posted to Shared room as Grok Bot.";
    }

    private static string? BlankArg(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private string SubmitSwarm(string taskId, string result, string proof, string? swarmId, string? runId)
    {
        var review = SwarmHandoffReview.Parse(result);
        if (string.IsNullOrWhiteSpace(taskId))
        {
            if (review is null) throw new ArgumentException("Task id is required.");
            return SwarmHandoffReview.Record(swarmId, runId, review, null);
        }
        if (string.IsNullOrWhiteSpace(proof)) throw new ArgumentException("Nothing is done without proof.");
        var line = _desk.SubmitSwarmResult(taskId, result, proof, swarmId, runId);
        if (review is null) return line;
        return line + "\n" + SwarmHandoffReview.Record(swarmId, runId, review, null, taskId);
    }

    private string ReadSubagent(string id)
    {
        var child = _desk.ReadSubagent(id) ?? throw new KeyNotFoundException("That subagent is not in an open chat.");
        return child.Id + "\t" + child.Name + "\t" + child.State + "\t" + child.Model + "\n" + child.Task + "\n" + child.Output;
    }

    private string Status()
    {
        var status = _desk.Status();
        return "version\t" + status.Version + "\nconnection\t" + (status.Connection ?? "") + "\nmodel\t" + (status.Model ?? "") + "\nturn\t" + (status.TurnRunning ? "running" : "idle");
    }

    private static string Text(IEnumerable<GrokBotMessage> messages) =>
        string.Join("\n", messages.Select(message => message.Author + "\t" + (message.Model ?? "") + "\t" + message.Text.Replace('\n', ' ')));

    private static string Text(IEnumerable<string> lines) => string.Join("\n", lines);

    private static object ToolError(string message) => new { content = new[] { new { type = "text", text = message } }, isError = true };

    private static object Error(string message) => new { content = new[] { new { type = "text", text = message } }, isError = true };

    private static object[] ToolList() =>
    [
        Tool("read_chat", "Recent messages from the open or named chat.", "conversation", false, true),
        Tool("list_chats", "Chats with ids and titles.", null, false, false),
        Tool("send_to_chat", "Send a message into a chat as Grok Bot.", "conversation", true, false),
        Tool("read_shared_room", "Recent Shared room messages.", null, false, true),
        Tool("post_shared_room", "Post in Shared room as Grok Bot.", null, false, false),
        Tool("list_subagents", "Subagents in a chat.", "conversation", true, false),
        Tool("read_subagent", "Status and final result for one subagent.", "id", true, false),
        Tool("status", "App version, connection, model, and whether a turn is running.", null, false, false),
        Tool("grokbot_inbox_list", "Grokbot tab messages that are not answered yet. Listing marks Queued messages as Picked up.", "since", false, false),
        Tool("grokbot_inbox_reply", "Post a reply into the Grokbot tab and mark that message Answered.", "message_id", true, false),
        Tool("app_screenshot", "PNG of the SI Desk window only. Requires Screenshots and permission to act.", null, false, false),
        Tool("app_ui_tree", "Visible controls in the SI Desk window: id, type, label, and bounds.", null, false, false),
        Tool("app_click", "Click a control or a point inside the SI Desk window.", null, false, false),
        Tool("app_type", "Type into a control in the SI Desk window. Secret fields are blocked.", "control_id", true, false),
        Tool("app_key", "Send keys to the focused control in the SI Desk window.", "keys", true, false),
        Tool("app_navigate", "Open Chat, Shared room, Settings, or Grokbot.", "page", true, false),
        Tool("read_swarm_board", "Read a swarm board. Pass run_id to read that run even when it is not current. Omit swarm_id to list boards.", "swarm_id", false, false),
        Tool("swarm_submit_result", "Submit a Grok Bot swarm result and proof for swarm_id and task_id. Pass run_id to write that run. Moves the task to review. Cannot mark it done. A handoff review may send an empty task_id with swarm_id, run_id, and result lines verdict, issues, and next. That records the review and does not mark a task done.", "task_id", true, false),
        Tool("swarm_continue", "After a handoff review, post the verdict into that swarm chat as Grok Bot. Verdict is continue, fix, or done. Continue and fix resume the manager. Done does not. A repeated call for the same review is ignored.", "swarm_id", true, false),
        Tool("swarm_run_status", "Read the run id, review status, and each task status for a swarm run. Does not write the board.", "swarm_id", true, false)
    ];

    private static object Tool(string name, string description, string? argument, bool required, bool limit)
    {
        var properties = new Dictionary<string, object>();
        var requiredNames = new List<string>();
        if (argument is not null)
        {
            properties[argument] = new { type = "string" };
            if (required) requiredNames.Add(argument);
        }
        if (name is "send_to_chat" or "post_shared_room" or "grokbot_inbox_reply" or "app_type")
        {
            properties["text"] = new { type = "string" };
            requiredNames.Add("text");
        }
        if (name == "read_swarm_board")
            properties["run_id"] = new { type = "string" };
        if (name == "swarm_submit_result")
        {
            properties["swarm_id"] = new { type = "string" };
            properties["run_id"] = new { type = "string" };
            properties["result"] = new { type = "string" };
            properties["proof"] = new { type = "string" };
            requiredNames.Add("result");
            requiredNames.Add("proof");
        }
        if (name == "swarm_continue")
        {
            properties["run_id"] = new { type = "string" };
            properties["verdict"] = new { type = "string" };
            properties["notes"] = new { type = "string" };
            requiredNames.Add("run_id");
            requiredNames.Add("verdict");
        }
        if (name == "swarm_run_status")
        {
            properties["run_id"] = new { type = "string" };
            requiredNames.Add("run_id");
        }
        if (name == "app_click")
        {
            properties["control_id"] = new { type = "string" };
            properties["x"] = new { type = "integer" };
            properties["y"] = new { type = "integer" };
        }
        if (limit) properties["limit"] = new { type = "integer" };
        return new { name, description, inputSchema = new { type = "object", properties, required = requiredNames } };
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    public async Task StopAsync()
    {
        if (_app is not { } app) return;
        _app = null;
        Url = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await app.StopAsync(timeout.Token); }
        catch (Exception) { }
        await app.DisposeAsync();
    }
}
