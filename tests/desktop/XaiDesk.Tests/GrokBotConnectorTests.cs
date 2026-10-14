using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Sovereign.Desktop.Connector;
using Xunit;

namespace XaiDesk.Tests;

public sealed class GrokBotConnectorTests
{
    private const string Token = "grok-bot-test-token-not-for-logs";

    [Fact]
    public async Task Toggle_off_does_not_listen()
    {
        await using var host = Host(new MemoryDesk());
        var error = await host.StartAsync(false, ConnectorBindMode.Loopback);
        Assert.Equal("Grok Bot connector is off.", error);
        Assert.False(host.Running);
        Assert.Null(host.Url);
    }

    [Fact]
    public async Task Auth_is_required_and_a_wrong_token_is_rejected()
    {
        var desk = new MemoryDesk();
        await using var host = await Start(desk);
        using var http = new HttpClient();
        var missing = await http.PostAsync(host.Url, Body("status"));
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        http.DefaultRequestHeaders.Authorization = new("Bearer", "wrong-token");
        var wrong = await http.PostAsync(host.Url, Body("status"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        http.DefaultRequestHeaders.Authorization = new("Bearer", Token);
        var ok = await Call(http, host.Url!, "status");
        Assert.Contains("idle", ok, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_tool_works_against_a_test_chat()
    {
        var desk = new MemoryDesk();
        await using var host = await Start(desk);
        using var http = Client();
        var chats = await Call(http, host.Url!, "list_chats");
        Assert.Contains(desk.ChatId + "\tFixture", chats, StringComparison.Ordinal);
        var read = await Call(http, host.Url!, "read_chat", new { conversation = desk.ChatId, limit = 10 });
        Assert.Contains("hello", read, StringComparison.Ordinal);
        var sent = await Call(http, host.Url!, "send_to_chat", new { conversation = desk.ChatId, text = "from the bot" });
        Assert.Contains("Grok Bot", sent, StringComparison.Ordinal);
        var again = await Call(http, host.Url!, "read_chat", new { conversation = desk.ChatId });
        Assert.Contains("Grok Bot\tGrok Bot\tfrom the bot", again, StringComparison.Ordinal);
        var room = await Call(http, host.Url!, "read_shared_room", new { limit = 5 });
        Assert.Contains("room note", room, StringComparison.Ordinal);
        var posted = await Call(http, host.Url!, "post_shared_room", new { text = "bot in the room" });
        Assert.Contains("Shared room", posted, StringComparison.Ordinal);
        Assert.Equal("Grok Bot", desk.Room.Single(message => message.Text == "bot in the room").Author);
        var agents = await Call(http, host.Url!, "list_subagents", new { conversation = desk.ChatId });
        Assert.Contains("helper-1\tReader\tcompleted", agents, StringComparison.Ordinal);
        var agent = await Call(http, host.Url!, "read_subagent", new { id = "helper-1" });
        Assert.Contains("finished the file", agent, StringComparison.Ordinal);
        var status = await Call(http, host.Url!, "status");
        Assert.Contains("connection\tCursor", status, StringComparison.Ordinal);
        Assert.Contains("model\tgrok-4.7", status, StringComparison.Ordinal);
    }

    [Fact]
    public void Send_to_chat_returns_while_ui_delivery_is_still_queued()
    {
        Action? queued = null;
        var runs = 0;
        UiDelivery.Invoke(false, () => runs++, action => queued = action, TimeSpan.FromMilliseconds(50));
        Assert.Equal(0, runs);
        Assert.NotNull(queued);
        queued();
        Assert.Equal(1, runs);
    }

    [Fact]
    public void Send_to_chat_ui_delivery_propagates_chat_missing()
    {
        Assert.Throws<KeyNotFoundException>(() =>
            UiDelivery.Invoke(false, () => throw new KeyNotFoundException("That chat is not open."), action => action(), TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Send_to_chat_on_the_ui_thread_runs_inline()
    {
        var ran = false;
        UiDelivery.Invoke(true, () => ran = true, _ => throw new IOException("The desktop window is closed."), TimeSpan.FromSeconds(2));
        Assert.True(ran);
    }

    [Fact]
    public async Task String_arguments_still_send_to_chat()
    {
        var desk = new MemoryDesk();
        await using var host = await Start(desk);
        using var http = Client();
        var arguments = "{\"conversation\":\"" + desk.ChatId + "\",\"text\":\"from the bot\"}";
        var sent = await Call(http, host.Url!, "send_to_chat", arguments);
        Assert.Contains("Grok Bot", sent, StringComparison.Ordinal);
        var again = await Call(http, host.Url!, "read_chat", new { conversation = desk.ChatId });
        Assert.Contains("Grok Bot\tGrok Bot\tfrom the bot", again, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_and_post_stop_at_twenty_per_minute()
    {
        await using var host = await Start(new MemoryDesk());
        using var http = Client();
        for (var i = 0; i < ConnectorRateGate.Limit; i++)
            Assert.DoesNotContain("Rate limit", await Call(http, host.Url!, "send_to_chat", new { conversation = "chat-1", text = "n" + i }), StringComparison.Ordinal);
        var blocked = await Call(http, host.Url!, "post_shared_room", new { text = "one more" });
        Assert.Contains("Rate limit: 20 messages a minute.", blocked, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Audit_log_records_the_tool_and_not_the_token()
    {
        var folder = Path.Combine(Path.GetTempPath(), "xai-desk-connector-" + Guid.NewGuid().ToString("N"));
        var audit = Path.Combine(folder, "audit.log");
        await using var host = await Start(new MemoryDesk(), audit);
        using var http = Client();
        await Call(http, host.Url!, "read_chat", new { conversation = "chat-1", text = Token });
        var log = File.ReadAllText(audit);
        Assert.Contains("read_chat", log, StringComparison.Ordinal);
        Assert.Contains("chat-1", log, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, log, StringComparison.Ordinal);
        Directory.Delete(folder, true);
    }

    [Fact]
    public void Tailscale_address_comes_from_the_command_not_another_100_address()
    {
        var first = Cg(64, 1, 2);
        var second = Cg(65, 3, 4);
        Assert.Null(ConnectorBind.Select(ConnectorBindMode.Tailscale, [first, second]));
        Assert.Equal(second, ConnectorBind.ParseTailscaleIp(second.ToString() + "\r\n"));
        Assert.Null(ConnectorBind.ParseTailscaleIp(""));
        Assert.Null(ConnectorBind.ParseTailscaleIp("8.8.8.8\n192.168.1.20"));
        Assert.Equal(IPAddress.Loopback, ConnectorBind.Select(ConnectorBindMode.Loopback, [first]));
    }

    [Fact]
    public async Task A_failed_tailscale_lookup_does_not_listen()
    {
        await using var host = Host(new MemoryDesk());
        var error = await host.StartAsync(true, ConnectorBindMode.Tailscale, default, _ => Task.FromResult<(IPAddress?, string?)>((null, "tailscale ip -4 failed. The connector stayed off.")));
        Assert.Contains("tailscale ip -4 failed", error, StringComparison.Ordinal);
        Assert.False(host.Running);
        Assert.False(host.IsBound);
        Assert.Null(host.Url);
    }

    [Fact]
    public async Task Restart_binds_the_new_address_and_drops_the_old_port()
    {
        var desk = new MemoryDesk();
        await using var host = Host(desk);
        var first = await host.ListenForChecksAsync(IPAddress.Loopback, 0, ConnectorBindMode.Loopback);
        Assert.Null(first);
        var firstPort = new Uri(host.Url!).Port;
        Assert.True(ConnectorBind.IsListening(IPAddress.Loopback, firstPort));
        var second = await host.ListenForChecksAsync(IPAddress.Loopback, 0, ConnectorBindMode.Loopback);
        Assert.Null(second);
        var secondPort = new Uri(host.Url!).Port;
        Assert.True(ConnectorBind.IsListening(IPAddress.Loopback, secondPort));
        if (firstPort != secondPort) Assert.False(PortOpen(firstPort));
    }

    [Fact]
    public async Task Live_loopback_and_tailscale_accept_status()
    {
        if (Environment.GetEnvironmentVariable("XAI_DESK_CONNECTOR_LIVE") != "1") return;
        var loopback = await Prove(IPAddress.Loopback, ConnectorBindMode.Loopback, null);
        var (address, lookupError) = await ConnectorBind.QueryTailscaleIpv4Async();
        Assert.Null(lookupError);
        Assert.NotNull(address);
        Assert.StartsWith("100.", address!.ToString(), StringComparison.Ordinal);
        await Prove(address!, ConnectorBindMode.Tailscale, address);
        Assert.Equal("http://127.0.0.1:8790/mcp", loopback);
    }

    private static IPAddress Cg(int second, int third, int fourth) =>
        IPAddress.Parse("100." + second.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "." + third.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "." + fourth.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static async Task<string> Prove(IPAddress address, ConnectorBindMode mode, IPAddress? expected)
    {
        await using var host = Host(new MemoryDesk());
        var error = await host.ListenForChecksAsync(address, ConnectorBind.Port, mode);
        Assert.Null(error);
        var url = host.Url!;
        Assert.StartsWith("http://" + address + ":8790/mcp", url, StringComparison.Ordinal);
        Assert.True(host.IsBound);
        Assert.True(ConnectorBind.IsListening(address, ConnectorBind.Port));
        var listed = ListenersFromNetTcp(ConnectorBind.Port);
        Assert.Contains(address.ToString(), listed, StringComparison.OrdinalIgnoreCase);
        using var http = Client();
        var status = await Call(http, url, "status");
        Assert.Contains("idle", status, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, status, StringComparison.Ordinal);
        if (expected is not null) Assert.Equal(expected, address);
        return url;
    }

    private static string ListenersFromNetTcp(int port)
    {
        var info = new System.Diagnostics.ProcessStartInfo("powershell", "-NoProfile -Command \"Get-NetTCPConnection -LocalPort " + port + " -State Listen -ErrorAction SilentlyContinue | ForEach-Object { $_.LocalAddress }\"")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        using var process = System.Diagnostics.Process.Start(info);
        if (process is null) return "";
        var text = process.StandardOutput.ReadToEnd();
        process.WaitForExit(5000);
        return text;
    }

    [Fact]
    public async Task Stopping_the_connector_closes_the_port()
    {
        var desk = new MemoryDesk();
        var host = await Start(desk);
        var port = new Uri(host.Url!).Port;
        Assert.True(PortOpen(port));
        await host.DisposeAsync();
        Assert.False(host.Running);
        Assert.False(PortOpen(port));
    }

    [Fact]
    public async Task Inbox_round_trip_marks_the_message_answered()
    {
        var desk = new MemoryDesk();
        var queued = desk.Mail.Queue("hello from the desk", false);
        Assert.Equal(GrokbotMailStatus.Queued, queued.Status);
        await using var host = await Start(desk);
        using var http = Client();
        var listed = await Call(http, host.Url!, "grokbot_inbox_list");
        Assert.Contains(queued.Id + "\tPickedUp\treply\thello from the desk", listed, StringComparison.Ordinal);
        Assert.Equal(GrokbotMailStatus.PickedUp, desk.Mail.Messages.Single().Status);
        var replied = await Call(http, host.Url!, "grokbot_inbox_reply", new { message_id = queued.Id, text = "on it" });
        Assert.Equal("Answered\t" + queued.Id, replied);
        Assert.Equal("on it", desk.Mail.Messages.Single().Reply);
        Assert.Equal(GrokbotMailStatus.Answered, desk.Mail.Messages.Single().Status);
        var again = await Call(http, host.Url!, "grokbot_inbox_list");
        Assert.DoesNotContain(queued.Id, again, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inbox_list_keeps_the_text_row_and_appends_image_blocks_in_row_order()
    {
        var firstPng = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 1 };
        var secondPng = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 2, 3 };
        var desk = new MemoryDesk();
        var first = desk.Mail.Queue("one", false, firstPng);
        var plain = desk.Mail.Queue("two", false, []);
        var second = desk.Mail.Queue("three", true, secondPng);
        Assert.Null(plain.Image);
        await using var host = await Start(desk);
        using var http = Client();
        var response = await http.PostAsync(host.Url, Body("grokbot_inbox_list"));
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        var result = json.RootElement.GetProperty("result");
        Assert.False(result.GetProperty("isError").GetBoolean());
        var content = result.GetProperty("content");
        Assert.Equal(5, content.GetArrayLength());
        var text = content[0].GetProperty("text").GetString() ?? "";
        var lines = text.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Equal(first.Id + "\tPickedUp\treply\tone", lines[0]);
        Assert.Equal(plain.Id + "\tPickedUp\treply\ttwo", lines[1]);
        Assert.Equal(second.Id + "\tPickedUp\tact\tthree", lines[2]);
        foreach (var line in lines) Assert.Equal(4, line.Split('\t').Length);
        Assert.DoesNotContain(Convert.ToBase64String(firstPng), text, StringComparison.Ordinal);
        Assert.Equal("text", content[1].GetProperty("type").GetString());
        Assert.Equal(first.Id, content[1].GetProperty("text").GetString());
        Assert.Equal("image", content[2].GetProperty("type").GetString());
        Assert.Equal("image/png", content[2].GetProperty("mimeType").GetString());
        Assert.Equal(Convert.ToBase64String(firstPng), content[2].GetProperty("data").GetString());
        Assert.Equal("text", content[3].GetProperty("type").GetString());
        Assert.Equal(second.Id, content[3].GetProperty("text").GetString());
        Assert.Equal("image", content[4].GetProperty("type").GetString());
        Assert.Equal("image/png", content[4].GetProperty("mimeType").GetString());
        Assert.Equal(Convert.ToBase64String(secondPng), content[4].GetProperty("data").GetString());
    }

    [Fact]
    public async Task Action_tools_require_the_control_gate()
    {
        var desk = new MemoryDesk();
        desk.Bounds = new GrokbotWindowBounds(0, 0, 200, 100);
        desk.Controls.Add(new GrokbotControl("title", "ControlType.Text", "Title", 10, 10, 40, 20));
        await using var host = await Start(desk);
        using var http = Client();
        var blocked = await Call(http, host.Url!, "app_click", new { x = 10, y = 10 });
        Assert.Contains("Grokbot control is off", blocked, StringComparison.Ordinal);
        var shots = await Call(http, host.Url!, "app_screenshot");
        Assert.Contains("Grokbot control is off", shots, StringComparison.Ordinal);
        var prefs = desk.Mail.PreferencesCopy();
        prefs.AllowControl = true;
        desk.Mail.SavePreferences(prefs);
        var tree = await Call(http, host.Url!, "app_ui_tree");
        Assert.Contains("title\tControlType.Text\tTitle\t10\t10\t40\t20", tree, StringComparison.Ordinal);
        var clicked = await Call(http, host.Url!, "app_click", new { control_id = "title" });
        Assert.Equal("Clicked.", clicked);
        var outside = await Call(http, host.Url!, "app_click", new { x = 500, y = 500 });
        Assert.Contains("outside the SI Desk window", outside, StringComparison.Ordinal);
        prefs.Stopped = true;
        desk.Mail.SavePreferences(prefs);
        var stopped = await Call(http, host.Url!, "app_navigate", new { page = "Chat" });
        Assert.Contains("Grokbot control is off", stopped, StringComparison.Ordinal);
        Assert.Contains("app_navigate", string.Join("\n", desk.Mail.Audit.Select(line => line.Tool)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Secret_fields_and_points_outside_the_window_are_refused()
    {
        var window = new GrokbotWindowBounds(0, 0, 200, 100);
        var secret = new GrokbotControl("xAI API key", "ControlType.Edit", "xAI API key", 10, 40, 80, 20, true);
        var away = new GrokbotControl("title", "ControlType.Text", "Title", 400, 400, 20, 20);
        Assert.True(GrokbotWindow.IsSecret("grokbot-blocked-token", "TextBox", "Grok Bot connector token"));
        Assert.True(GrokbotWindow.IsSecret("xAI API key", "ControlType.Edit", "xAI API key", true));
        Assert.False(GrokbotWindow.IsSecret("Grokbot message", "TextBox", "Grokbot message"));
        Assert.Throws<InvalidOperationException>(() => GrokbotWindow.RequireClick(window, secret, null, null, null));
        Assert.Throws<InvalidOperationException>(() => GrokbotWindow.RequireClick(window, null, 10, 50, secret));
        Assert.Throws<InvalidOperationException>(() => GrokbotWindow.RequireClick(window, null, 900, 10, null));
        Assert.Throws<InvalidOperationException>(() => GrokbotWindow.RequireInside(window, away));
        Assert.Throws<InvalidOperationException>(() => GrokbotWindow.RequireType("grokbot-blocked-xai-key", "PasswordBox", "xAI API key", "secret"));
        GrokbotWindow.RequireClick(window, null, 20, 20, null);
        var desk = new MemoryDesk();
        desk.Bounds = window;
        desk.Controls.Add(secret);
        var prefs = desk.Mail.PreferencesCopy();
        prefs.AllowControl = true;
        desk.Mail.SavePreferences(prefs);
        await using var host = await Start(desk);
        using var http = Client();
        var typed = await Call(http, host.Url!, "app_type", new { control_id = "xAI API key", text = "nope" });
        Assert.Contains("blocked", typed, StringComparison.Ordinal);
        Assert.DoesNotContain("nope", string.Join("\n", desk.Mail.Audit.Select(line => line.Args + " " + line.Result)), StringComparison.Ordinal);
        desk.Controls.Insert(0, new GrokbotControl("SI Desk", "Window", "SI Desk", 0, 0, 200, 100));
        var point = await Call(http, host.Url!, "app_click", new { x = 20, y = 50 });
        Assert.Contains("blocked", point, StringComparison.Ordinal);
        desk.FocusInside = false;
        var leftWindow = await Call(http, host.Url!, "app_key", new { keys = "Enter" });
        Assert.Contains("outside the SI Desk window", leftWindow, StringComparison.Ordinal);
        desk.FocusInside = true;
        desk.FocusedSecret = true;
        var keyed = await Call(http, host.Url!, "app_key", new { keys = "A" });
        Assert.Contains("blocked", keyed, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => GrokbotWindow.RequireSafeKeys("Alt+Tab"));
        Assert.Throws<InvalidOperationException>(() => GrokbotWindow.RequireSafeKeys("Ctrl+Shift+Esc"));
        var stoppedAsk = new MemoryDesk();
        stoppedAsk.Mail.Queue("do it", true);
        var stopPrefs = stoppedAsk.Mail.PreferencesCopy();
        stopPrefs.Stopped = true;
        stoppedAsk.Mail.SavePreferences(stopPrefs);
        await using var stopHost = await Start(stoppedAsk);
        using var stopHttp = Client();
        var still = await Call(stopHttp, stopHost.Url!, "app_click", new { x = 1, y = 1 });
        Assert.Contains("Grokbot control is off", still, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Latest_message_ask_flag_allows_one_action_and_screenshots_stay_separate()
    {
        var desk = new MemoryDesk();
        desk.Mail.Queue("look", true);
        await using var host = await Start(desk);
        using var http = Client();
        var denied = await Call(http, host.Url!, "app_screenshot");
        Assert.Contains("Screenshots are off", denied, StringComparison.Ordinal);
        var prefs = desk.Mail.PreferencesCopy();
        prefs.Screenshots = true;
        desk.Mail.SavePreferences(prefs);
        var body = await http.PostAsync(host.Url, Body("app_screenshot"));
        var json = await body.Content.ReadAsStringAsync();
        Assert.Contains("image/png", json, StringComparison.Ordinal);
        Assert.Contains("PNG 2x2 8 bytes", json, StringComparison.Ordinal);
        desk.Mail.Queue("hands off", false);
        var after = await Call(http, host.Url!, "app_key", new { keys = "Enter" });
        Assert.Contains("Grokbot control is off", after, StringComparison.Ordinal);
    }

    [Fact]
    public void Inbox_survives_a_reload_and_the_link_follows_the_last_poll()
    {
        var folder = Path.Combine(Path.GetTempPath(), "xai-desk-grokbot-" + Guid.NewGuid().ToString("N"));
        var first = GrokbotMailbox.Open(folder);
        var mail = first.Queue("keep me", true);
        first.NotePoll(DateTimeOffset.Parse("2026-10-10T18:00:00Z"));
        var prefs = first.PreferencesCopy();
        prefs.AllowControl = true;
        prefs.TabEnabled = false;
        first.SavePreferences(prefs);
        var second = GrokbotMailbox.Open(folder);
        Assert.Equal(mail.Id, second.Messages.Single().Id);
        Assert.True(second.Messages.Single().AskToAct);
        Assert.False(second.PreferencesCopy().TabEnabled);
        Assert.True(second.PreferencesCopy().AllowControl);
        var connected = second.Link(true, DateTimeOffset.Parse("2026-10-10T18:09:00Z"));
        Assert.Equal(GrokbotLinkState.Connected, connected.State);
        var waiting = second.Link(true, DateTimeOffset.Parse("2026-10-10T18:11:00Z"));
        Assert.Equal(GrokbotLinkState.Waiting, waiting.State);
        Assert.Equal(GrokbotLinkState.Off, second.Link(false, DateTimeOffset.Parse("2026-10-10T18:01:00Z")).State);
        Directory.Delete(folder, true);
    }

    [Fact]
    public async Task Tool_list_includes_the_grokbot_tab_tools()
    {
        await using var host = await Start(new MemoryDesk());
        using var http = Client();
        var response = await http.PostAsync(host.Url, new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 2, method = "tools/list" }), Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();
        foreach (var name in new[] { "grokbot_inbox_list", "grokbot_inbox_reply", "app_screenshot", "app_ui_tree", "app_click", "app_type", "app_key", "app_navigate", "read_chat", "send_to_chat", "list_subagents", "swarm_continue", "swarm_run_status" })
            Assert.Contains("\"" + name + "\"", body, StringComparison.Ordinal);
    }

    private static HttpClient Client()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", Token);
        return http;
    }

    private static async Task<ConnectorHost> Start(MemoryDesk desk, string? audit = null)
    {
        var host = new ConnectorHost(new SeededSecrets(Token), desk, new ConnectorAudit(audit ?? Path.Combine(Path.GetTempPath(), "xai-desk-connector-audit-" + Guid.NewGuid().ToString("N") + ".log")));
        var error = await host.ListenForChecksAsync(IPAddress.Loopback, 0, ConnectorBindMode.Loopback);
        Assert.Null(error);
        Assert.StartsWith("http://127.0.0.1:", host.Url, StringComparison.Ordinal);
        Assert.EndsWith("/mcp", host.Url, StringComparison.Ordinal);
        Assert.True(host.IsBound);
        Assert.True(ConnectorBind.IsListening(IPAddress.Loopback, new Uri(host.Url!).Port));
        return host;
    }

    private static ConnectorHost Host(MemoryDesk desk) =>
        new(new SeededSecrets(Token), desk, new ConnectorAudit(Path.Combine(Path.GetTempPath(), "xai-desk-unused-audit.log")));

    private static StringContent Body(string tool, object? arguments = null) =>
        new(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments } }), Encoding.UTF8, "application/json");

    private static async Task<string> Call(HttpClient http, string url, string tool, object? arguments = null)
    {
        var response = await http.PostAsync(url, Body(tool, arguments));
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString() ?? "";
    }

    private static bool PortOpen(int port)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(IPAddress.Loopback, port);
            return task.Wait(TimeSpan.FromMilliseconds(300)) && client.Connected;
        }
        catch { return false; }
    }

    private sealed class SeededSecrets(string token) : IConnectorSecrets
    {
        public string? Current() => token;
        public string Generate() => token;
        public void Revoke() { }
    }

    private sealed class MemoryDesk : IGrokBotDesk
    {
        public string ChatId { get; } = "chat-1";
        public List<GrokBotMessage> Messages { get; } = [new("You", "hello", null), new("Grok", "hi", "grok-4.7")];
        public List<GrokBotMessage> Room { get; } = [new("You", "room note", null)];
        public GrokBotStatus Status() => new("0.3.60", "Cursor", "grok-4.7", false);
        public IReadOnlyList<GrokBotChat> ListChats() => [new(ChatId, "Fixture")];
        public IReadOnlyList<GrokBotMessage> ReadChat(string? conversation, int limit)
        {
            if (conversation is not null && conversation != ChatId) throw new KeyNotFoundException("That chat is not open.");
            return Messages.TakeLast(limit).ToArray();
        }
        public void SendToChat(string conversation, string text)
        {
            if (conversation != ChatId) throw new KeyNotFoundException("That chat is not open.");
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Grok Bot message is empty, too long, or has unsupported characters.");
            Messages.Add(new("Grok Bot", text.Trim(), "Grok Bot"));
        }
        public IReadOnlyList<GrokBotMessage> ReadSharedRoom(int limit) => Room.TakeLast(limit).ToArray();
        public void PostSharedRoom(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Grok Bot message is empty, too long, or has unsupported characters.");
            Room.Add(new("Grok Bot", text.Trim(), null));
        }
        public IReadOnlyList<GrokBotSubagent> ListSubagents(string conversation)
        {
            if (conversation != ChatId) throw new KeyNotFoundException("That chat is not open.");
            var child = ReadSubagent("helper-1");
            return child is null ? [] : [child];
        }
        public GrokBotSubagent? ReadSubagent(string id) => id == "helper-1"
            ? new("helper-1", "Reader", "Read the fixture", "completed", "finished the file", "grok-4.7")
            : null;
        public GrokbotMailbox Mail { get; } = new(null);
        public GrokbotWindowBounds Bounds { get; set; } = new(0, 0, 800, 600);
        public List<GrokbotControl> Controls { get; } = [];
        public string? OpenedPage { get; private set; }
        public bool FocusInside { get; set; } = true;
        public bool FocusedSecret { get; set; }
        public void NoteGrokbotPoll() => Mail.NotePoll();
        public IReadOnlyList<GrokbotMail> ListGrokbotInbox(string? since) => Mail.ListSince(since);
        public GrokbotMail ReplyGrokbotInbox(string messageId, string text) => Mail.Reply(messageId, text);
        public GrokbotPng CaptureGrokbotScreenshot()
        {
            Mail.EnsureScreenshot();
            return new GrokbotPng("PNG 2x2 8 bytes", [1, 2, 3, 4, 5, 6, 7, 8]);
        }
        public string GrokbotUiTree()
        {
            Mail.EnsureAction("app_ui_tree");
            return string.Join("\n", Controls.Select(item => string.Join('\t', item.Id, item.Type, item.Label, item.X, item.Y, item.Width, item.Height)));
        }
        public string GrokbotClick(string? controlId, int? x, int? y)
        {
            Mail.EnsureAction("app_click");
            GrokbotControl? control = null;
            if (!string.IsNullOrWhiteSpace(controlId))
                control = Controls.FirstOrDefault(item => item.Id == controlId) ?? throw new KeyNotFoundException("That control is not in the SI Desk window.");
            var hit = x is int px && y is int py ? GrokbotWindow.SmallestHit(Controls, px, py) : null;
            GrokbotWindow.RequireClick(Bounds, control, x, y, hit);
            return "Clicked.";
        }
        public string GrokbotType(string controlId, string text)
        {
            Mail.EnsureAction("app_type");
            var control = Controls.FirstOrDefault(item => item.Id == controlId) ?? throw new KeyNotFoundException("That control is not in the SI Desk window.");
            GrokbotWindow.RequireType(control.Id, control.Type, control.Label, text, control.Password);
            GrokbotWindow.RequireInside(Bounds, control);
            return "Typed.";
        }
        public string GrokbotKey(string keys)
        {
            Mail.EnsureAction("app_key");
            GrokbotWindow.RequireSafeKeys(keys);
            GrokbotWindow.RequireFocused(FocusInside, FocusedSecret);
            return "Sent keys.";
        }
        public string GrokbotNavigate(string page)
        {
            Mail.EnsureAction("app_navigate");
            OpenedPage = GrokbotWindow.CanonicalPage(page);
            return "Opened " + page.Trim() + ".";
        }
        public void RecordGrokbotTool(string tool, string args, string result) => Mail.Record(tool, args, result);
        public string? SwarmRoot { get; set; }
        public string ReadSwarmBoard(string? swarmId, string? runId = null) => SwarmBoard.ReadText(string.IsNullOrWhiteSpace(swarmId) ? null : swarmId, SwarmRoot, string.IsNullOrWhiteSpace(runId) ? null : runId);
        public string SubmitSwarmResult(string taskId, string result, string proof, string? swarmId = null, string? runId = null)
        {
            var board = SwarmBoard.RequireTask(taskId, SwarmRoot, out _, string.IsNullOrWhiteSpace(swarmId) ? null : swarmId, string.IsNullOrWhiteSpace(runId) ? null : runId);
            var task = board.Submit(taskId, result, proof);
            return task.Id + "\t" + task.Status;
        }
        public bool GrokbotReviewsHandoffs(string conversationId) => false;
        public bool ConversationOpen(string conversationId) => false;
        public void DeliverSwarmReview(string conversationId, string text, bool resume) { }
        public string? SwarmBoardRoot() => SwarmRoot;
    }
}
