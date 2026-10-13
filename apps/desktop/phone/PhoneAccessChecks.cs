using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Sovereign.Desktop.Client;

namespace Sovereign.Desktop.Phone;

public static class PhoneAccessChecks
{
    public static async Task<int> RunAsync()
    {
        var failed = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) { failed.Add(name); Console.Error.WriteLine("FAIL " + name); }
            else Console.WriteLine("ok " + name);
        }
        Bind(Check);
        await AuthAsync(Check);
        Approval(Check);
        Qr(Check);
        if (failed.Count == 0) { Console.WriteLine("phone access checks passed"); return 0; }
        Console.Error.WriteLine(failed.Count + " phone access checks failed");
        return 1;
    }

    private static void Bind(Action<bool, string> check)
    {
        check(PhoneBindAddress.Port == 8789, "production port is 8789");
        check(PhoneBindAddress.Select(PhoneBindMode.Loopback, [])!.Equals(IPAddress.Loopback), "loopback mode selects 127.0.0.1");
        var tailscaleExample = ExampleAddress(80, 1, 4);
        var tail = PhoneBindAddress.Select(PhoneBindMode.Tailscale, [IPAddress.Parse("8.8.8.8"), IPAddress.Parse("192.168.1.20"), tailscaleExample]);
        check(tail is not null && tail.Equals(tailscaleExample), "tailscale mode selects the carrier-grade address");
        check(PhoneBindAddress.Select(PhoneBindMode.Tailscale, [IPAddress.Any, IPAddress.Parse("10.1.1.1"), IPAddress.Parse("172.16.0.4"), IPAddress.Loopback, IPAddress.Parse("100.63.255.255"), IPAddress.Parse("100.128.0.1")]) is null,
            "tailscale mode refuses public, LAN, any, and addresses outside the carrier-grade range");
        check(PhoneBindAddress.ConnectionAllowed(IPAddress.Loopback, PhoneBindMode.Loopback), "loopback connections are allowed in loopback mode");
        check(!PhoneBindAddress.ConnectionAllowed(ExampleAddress(64, 0, 1), PhoneBindMode.Loopback), "carrier-grade address is refused in loopback mode");
        check(PhoneBindAddress.ConnectionAllowed(ExampleAddress(127, 255, 255), PhoneBindMode.Tailscale), "top of the carrier-grade range is allowed");
        check(!PhoneBindAddress.ConnectionAllowed(IPAddress.IPv6Loopback, PhoneBindMode.Loopback), "IPv6 loopback is refused");
        check(!PhoneBindAddress.ConnectionAllowed(IPAddress.Any, PhoneBindMode.Tailscale), "0.0.0.0 is refused");
        check(!PhoneBindAddress.IsTailscale(IPAddress.Parse("192.168.0.1")) && !PhoneBindAddress.IsTailscale(IPAddress.Parse("8.8.8.8")), "LAN and public addresses are not tailscale");
    }

    private static async Task AuthAsync(Action<bool, string> check)
    {
        var profile = Path.Combine(Path.GetTempPath(), "sovereign-phone-" + Guid.NewGuid().ToString("N"));
        var web = Path.Combine(profile, "www");
        Directory.CreateDirectory(web);
        await File.WriteAllTextAsync(Path.Combine(web, "index.html"), "<!doctype html><title>phone</title>");
        var store = new PhoneDeviceStore(profile) { Clock = () => DateTimeOffset.Parse("2026-10-08T12:00:00Z") };
        var code = store.BeginPairing();
        var bad = store.Redeem("0000", "Phone");
        check(bad.Status == PhonePairStatus.Invalid && bad.Token is null, "wrong pairing code is rejected");
        var paired = store.Redeem(code, "Pocket");
        check(paired.Status == PhonePairStatus.Ok && paired.Token is { Length: 64 } && paired.DeviceId is not null, "pairing issues one device token");
        var replay = store.Redeem(code, "Pocket");
        check(replay.Status == PhonePairStatus.Invalid, "pairing code cannot be reused");
        check(store.Authorize(paired.Token, out var device) && device!.Name == "Pocket", "bearer token is accepted");
        var devicesFile = await File.ReadAllTextAsync(Path.Combine(profile, "phone", "devices.json"));
        check(!devicesFile.Contains(paired.Token!, StringComparison.Ordinal) && !devicesFile.Contains(code, StringComparison.Ordinal), "raw token and pairing code are not stored");
        check(store.Revoke(paired.DeviceId!), "device revoke removes the record");
        check(!store.Authorize(paired.Token, out _), "revoked token is refused");
        store.Clock = () => DateTimeOffset.Parse("2026-10-08T12:00:00Z");
        var expiring = store.BeginPairing();
        store.Clock = () => DateTimeOffset.Parse("2026-10-08T12:11:00Z");
        check(store.Redeem(expiring, "Late").Status == PhonePairStatus.Expired, "expired pairing code is refused");
        store.Clock = () => DateTimeOffset.Parse("2026-10-08T13:00:00Z");
        var limited = store.BeginPairing();
        for (var i = 0; i < 5; i++) store.Redeem("ffff", "x");
        check(store.Redeem(limited, "x").Status == PhonePairStatus.RateLimited, "pairing is rate limited");
        var fresh = new PhoneDeviceStore(profile + "-send");
        var sendCode = fresh.BeginPairing();
        var sendPair = fresh.Redeem(sendCode, "Send");
        var allowed = 0;
        for (var i = 0; i < 12; i++) if (fresh.AllowSend(sendPair.DeviceId!)) allowed++;
        check(allowed == 10, "send is limited to 10 in the window");
        check(PhoneLog.Clean("Authorization: Bearer " + sendPair.Token) == "Authorization: Bearer [redacted]", "log text drops a bearer token");
        check(!PhoneLog.Clean(sendPair.Token).Contains(sendPair.Token!, StringComparison.Ordinal), "log text drops a raw token");

        store.Clock = () => DateTimeOffset.Parse("2026-10-08T14:00:00Z");
        var gate = new PhoneApprovalGate();
        var bridge = new MemoryBridge();
        await using var host = new PhoneAccessHost(store, bridge, gate, web);
        var started = await host.ListenForChecksAsync(IPAddress.Loopback, 0, PhoneBindMode.Loopback, null, CancellationToken.None);
        check(started is null && host.Origin is not null && host.Origin.StartsWith("http://127.0.0.1:", StringComparison.Ordinal), "check listener binds IPv4 loopback");
        using var http = new HttpClient { BaseAddress = new Uri(host.Origin!) };
        var page = await http.GetAsync("/");
        check(page.IsSuccessStatusCode, "loopback serves the phone page");
        var live = store.BeginPairing();
        var response = await http.PostAsync("/api/pair", Json(new { code = live, name = "Http" }));
        var body = await response.Content.ReadAsStringAsync();
        check(response.IsSuccessStatusCode && body.Contains("token", StringComparison.Ordinal), "HTTP pairing returns a token");
        using var parsed = JsonDocument.Parse(body);
        var token = parsed.RootElement.GetProperty("token").GetString()!;
        var deviceId = parsed.RootElement.GetProperty("deviceId").GetString()!;
        using var authed = new HttpRequestMessage(HttpMethod.Get, "/api/chats");
        authed.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        var chats = await http.SendAsync(authed);
        check(chats.IsSuccessStatusCode, "paired token can list chats");
        check(store.Revoke(deviceId), "HTTP device can be revoked");
        using var again = new HttpRequestMessage(HttpMethod.Get, "/api/chats");
        again.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        var denied = await http.SendAsync(again);
        check(denied.StatusCode == System.Net.HttpStatusCode.Unauthorized, "revoked token is refused over HTTP");
        check(host.LogLines().All(line => !line.Contains(token, StringComparison.Ordinal) && !line.Contains(live, StringComparison.Ordinal)), "listener logs do not contain the token or pairing code");
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var busyPort = ((IPEndPoint)occupied.LocalEndpoint).Port;
        await using var busy = new PhoneAccessHost(new PhoneDeviceStore(profile + "-busy"), bridge, gate, web);
        var busyError = await busy.ListenForChecksAsync(IPAddress.Loopback, busyPort, PhoneBindMode.Loopback, null, CancellationToken.None);
        check(busyError is not null && !busy.Running && busy.Origin is null, "occupied port returns an error without starting or throwing");
        occupied.Stop();

        var streamPair = store.Redeem(store.BeginPairing(), "Stream");
        using var streamCall = new HttpRequestMessage(HttpMethod.Get, "/api/chats/chat/events");
        streamCall.Headers.TryAddWithoutValidation("Authorization", "Bearer " + streamPair.Token);
        using var streamDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var streamResponse = await http.SendAsync(streamCall, HttpCompletionOption.ResponseHeadersRead, streamDeadline.Token);
        using var streamReader = new StreamReader(await streamResponse.Content.ReadAsStreamAsync(streamDeadline.Token));
        check((await streamReader.ReadLineAsync(streamDeadline.Token)) == "event: snapshot", "paired device can open its event stream");
        check(store.Revoke(streamPair.DeviceId!), "stream device can be revoked");
        var lines = 0;
        while (await streamReader.ReadLineAsync(streamDeadline.Token) is not null) lines++;
        check(lines < 8, "revoking a device closes its already-open event stream");

        await using var refused = new PhoneAccessHost(new PhoneDeviceStore(profile + "-bind"), bridge, gate, web);
        var refusedStart = await refused.ListenForChecksAsync(IPAddress.Loopback, 0, PhoneBindMode.Tailscale, null, CancellationToken.None);
        check(refusedStart is null, "tailscale-mode check listener starts only so the filter can refuse it");
        using var refusedHttp = new HttpClient { BaseAddress = new Uri(refused.Origin!) };
        var blocked = await refusedHttp.GetAsync("/");
        check(blocked.StatusCode == System.Net.HttpStatusCode.Forbidden, "tailscale mode refuses a loopback connection");
        await using var publicHost = new PhoneAccessHost(new PhoneDeviceStore(profile + "-public"), bridge, gate, web);
        var publicBind = await publicHost.ListenForChecksAsync(IPAddress.Parse("8.8.8.8"), 0, PhoneBindMode.Loopback, null, CancellationToken.None);
        check(publicBind is not null, "a public address is refused before listen");
    }

    private static void Approval(Action<bool, string> check)
    {
        var gate = new PhoneApprovalGate();
        var destructive = new CursorPermissionRequest(
            new CursorToolActivity("tool-1", "terminal", "execute", "pending", "Remove-Item notes.txt", []),
            [new("allow", "Allow once", "allow_once"), new("deny", "Deny", "reject_once")]);
        var waiting = new TaskCompletionSource<string?>();
        var id = gate.Track(destructive, waiting, CancellationToken.None, "Desk");
        var blocked = gate.Decide(id, true, false);
        check(!blocked.Accepted && blocked.Error == "confirm_required" && !waiting.Task.IsCompleted, "destructive allow without the second tap does nothing");
        var allowed = gate.Decide(id, true, true);
        check(allowed.Accepted && waiting.Task.Result == "allow", "destructive allow with confirm returns the allow choice");
        var read = new CursorPermissionRequest(
            new CursorToolActivity("tool-2", "read_file", "read", "pending", "readme", []),
            [new("once", "Allow once", "allow_once"), new("no", "Deny", "reject_once")]);
        var readWait = new TaskCompletionSource<string?>();
        var readId = gate.Track(read, readWait, CancellationToken.None, "Desk");
        check(!PhoneApprovalGate.IsDestructive(read.Tool), "read_file is not destructive");
        check(gate.Decide(readId, false, false).Accepted && readWait.Task.Result == "no", "deny returns the reject choice");
        var alreadyAnswered = new TaskCompletionSource<string?>();
        var oldId = gate.Track(read, alreadyAnswered, CancellationToken.None, "Desk");
        alreadyAnswered.SetResult("no");
        check(!gate.Decide(oldId, true, true).Accepted && alreadyAnswered.Task.Result == "no", "late phone answer cannot replace or claim a desktop decision");
        using var cancelled = new CancellationTokenSource();
        var cancelledAnswer = new TaskCompletionSource<string?>();
        var cancelledId = gate.Track(read, cancelledAnswer, cancelled.Token, "Desk");
        cancelled.Cancel();
        check(!gate.Decide(cancelledId, true, true).Accepted && cancelledAnswer.Task.IsCanceled, "cancelled approval remains cancelled");
        var write = new CursorToolActivity("perm", "Additional project permissions", "permission", "pending", "write note.txt", []);
        check(PhoneApprovalGate.IsDestructive(write), "a write permission line is destructive");
    }

    private static void Qr(Action<bool, string> check)
    {
        var png = QrCode.Png("http://127.0.0.1:8789/#abc");
        check(png.Length > 8 && png[0] == 137 && png[1] == 80 && png[2] == 78 && png[3] == 71, "pairing QR is a PNG");
        var modules = QrCode.Modules("HI");
        check(modules[0, 0] && modules[6, 6] && !modules[1, 1], "QR finder pattern is in the corner");
    }

    private static StringContent Json(object value) => new(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json");

    private static IPAddress ExampleAddress(int second, int third, int fourth) =>
        IPAddress.Parse("100."
            + second.ToString(System.Globalization.CultureInfo.InvariantCulture) + "."
            + third.ToString(System.Globalization.CultureInfo.InvariantCulture) + "."
            + fourth.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private sealed class MemoryBridge : IPhoneChatBridge
    {
        public Task<IReadOnlyList<PhoneChatSummary>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PhoneChatSummary>>([new("chat", "Hello", "Cursor", "grok-4.7", false, DateTimeOffset.UnixEpoch, null)]);
        public Task<PhoneChatDetail?> GetAsync(string id, CancellationToken ct) =>
            Task.FromResult<PhoneChatDetail?>(new("chat", "Hello", "Cursor", "grok-4.7", "Saved", false, null, "", "Saved", [], []));
        public Task<PhoneSendResult> SendAsync(string id, string text, CancellationToken ct) => Task.FromResult(new PhoneSendResult(true, null));
        public Task<bool> StopAsync(string id, CancellationToken ct) => Task.FromResult(true);
    }
}
