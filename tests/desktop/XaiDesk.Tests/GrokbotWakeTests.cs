using System.Net;
using System.Text.Json;
using Sovereign.Desktop.Connector;
using Xunit;

namespace XaiDesk.Tests;

public sealed class GrokbotWakeTests
{
    private const string Secret = "wake-sender-key-do-not-leak";

    [Fact]
    public void Preview_cuts_to_80_and_redacts_api_key()
    {
        var secret = "topsecretvalue";
        var preview = GrokbotWake.Preview("  hello api_key=" + secret + " " + new string('z', 120));
        Assert.DoesNotContain(secret, preview, StringComparison.Ordinal);
        Assert.Contains("[redacted]", preview, StringComparison.Ordinal);
        Assert.True(preview.Length <= 80);
        Assert.Equal(80, GrokbotWake.Preview(new string('m', 140)).Length);
        Assert.Equal("hello", GrokbotWake.Preview("  hello  "));
        Assert.DoesNotContain("sk-live-abcdef", GrokbotWake.Preview("token sk-live-abcdef end"), StringComparison.Ordinal);
        Assert.DoesNotContain("eyJhbGci", GrokbotWake.Preview("Bearer " + "eyJhbGci.payload.sig"), StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", GrokbotWake.Preview("password=hunter2"), StringComparison.Ordinal);
        Assert.DoesNotContain("ya29.token", GrokbotWake.Preview("access_token=ya29.token"), StringComparison.Ordinal);
        Assert.DoesNotContain("xai-livekey", GrokbotWake.Preview("model xai-livekey"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostAsync_sends_bearer_header_and_json_body_without_the_key()
    {
        using var handler = new ScriptedHandler(Secret, HttpStatusCode.OK);
        var result = await GrokbotWake.PostAsync(handler, "https://wake.example/hook", Secret, "m-42", true, "desk says hello", CancellationToken.None, _ => TimeSpan.Zero);
        Assert.Equal("Grokbot pinged", result);
        var body = Assert.Single(handler.Bodies);
        Assert.Equal("Bearer " + Secret, Assert.Single(handler.Authorizations));
        Assert.Equal("application/json", Assert.Single(handler.ContentTypes));
        Assert.DoesNotContain(Secret, body, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(3, json.RootElement.EnumerateObject().Count());
        Assert.Equal("m-42", json.RootElement.GetProperty("message_id").GetString());
        Assert.True(json.RootElement.GetProperty("act").GetBoolean());
        Assert.Equal("desk says hello", json.RootElement.GetProperty("preview").GetString());
    }

    [Fact]
    public async Task DeliverAsync_reports_the_http_status()
    {
        using var ok = new ScriptedHandler(Secret, HttpStatusCode.OK);
        var success = await GrokbotWake.DeliverAsync(ok, "https://wake.example/hook", Secret, "test", false, "test wake", CancellationToken.None, _ => TimeSpan.Zero);
        Assert.Equal("Grokbot pinged", success.Note);
        Assert.Equal(200, success.StatusCode);
        Assert.DoesNotContain(Secret, success.Note, StringComparison.Ordinal);

        using var denied = new ScriptedHandler(Secret, HttpStatusCode.BadRequest);
        var failure = await GrokbotWake.DeliverAsync(denied, "https://wake.example/hook", Secret, "test", false, "test wake", CancellationToken.None, _ => TimeSpan.Zero);
        Assert.Equal(400, failure.StatusCode);
        Assert.Contains("400", failure.Note, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, failure.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostAsync_retries_500_then_returns_pinged()
    {
        using var handler = new ScriptedHandler(Secret, HttpStatusCode.InternalServerError, HttpStatusCode.OK);
        var delays = new List<int>();
        var result = await GrokbotWake.PostAsync(handler, "https://wake.example/hook", Secret, "m-7", false, "again", CancellationToken.None, attempt =>
        {
            delays.Add(attempt);
            return TimeSpan.Zero;
        });
        Assert.Equal("Grokbot pinged", result);
        Assert.Equal(2, handler.Authorizations.Count);
        Assert.Equal(1, Assert.Single(delays));
        Assert.All(handler.Bodies, body => Assert.DoesNotContain(Secret, body, StringComparison.Ordinal));
    }

    [Fact]
    public async Task PostAsync_does_not_retry_400()
    {
        using var handler = new ScriptedHandler("denied " + Secret, HttpStatusCode.BadRequest);
        var delays = 0;
        var result = await GrokbotWake.PostAsync(handler, "https://wake.example/hook", Secret, "m-9", false, "nope", CancellationToken.None, _ =>
        {
            delays++;
            return TimeSpan.Zero;
        });
        Assert.Single(handler.Authorizations);
        Assert.Equal(0, delays);
        Assert.Contains("400", result, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, result, StringComparison.Ordinal);
        Assert.NotEqual("Grokbot pinged", result);
    }

    [Fact]
    public async Task PostAsync_refuses_userinfo_and_does_not_send()
    {
        using var handler = new ScriptedHandler(Secret);
        var result = await GrokbotWake.PostAsync(handler, "https://user:" + Secret + "@wake.example/hook", Secret, "m", false, "x", CancellationToken.None, _ => TimeSpan.Zero);
        Assert.Empty(handler.Authorizations);
        Assert.DoesNotContain(Secret, result, StringComparison.Ordinal);
    }

    [Fact]
    public void NoteWake_round_trips_through_mailbox_open()
    {
        var folder = Path.Combine(Path.GetTempPath(), "xai-desk-wake-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = GrokbotMailbox.Open(folder);
            var mail = first.Queue("keep the note", false);
            first.NoteWake(mail.Id, "Grokbot pinged");
            var prefs = first.PreferencesCopy();
            prefs.WakeUrl = "https://wake.example/hook";
            prefs.AllowControl = true;
            first.SavePreferences(prefs);
            var toggled = first.PreferencesCopy();
            toggled.TabEnabled = false;
            first.SavePreferences(toggled);
            var second = GrokbotMailbox.Open(folder);
            Assert.Equal("Grokbot pinged", second.Messages.Single().WakeNote);
            Assert.Equal("https://wake.example/hook", second.PreferencesCopy().WakeUrl);
            Assert.False(second.PreferencesCopy().TabEnabled);
            Assert.True(second.PreferencesCopy().AllowControl);
            var state = File.ReadAllText(Path.Combine(folder, "state.json"));
            var thread = File.ReadAllText(Path.Combine(folder, "thread.jsonl"));
            Assert.Contains("https://wake.example/hook", state, StringComparison.Ordinal);
            Assert.Contains("Grokbot pinged", thread, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, state, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, thread, StringComparison.Ordinal);
            var positional = new GrokbotMail(mail.Id, "keep the note", false, GrokbotMailStatus.Queued, mail.Created, null, null);
            Assert.Null(positional.WakeNote);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Old_mailbox_files_without_wake_fields_still_load()
    {
        var folder = Path.Combine(Path.GetTempPath(), "xai-desk-wake-old-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "state.json"), "{\"tabEnabled\":false,\"allowControl\":true,\"screenshots\":false,\"stopped\":false}");
            File.WriteAllText(Path.Combine(folder, "thread.jsonl"), "{\"id\":\"m1\",\"text\":\"keep me\",\"askToAct\":true,\"status\":\"Queued\",\"created\":\"2026-10-10T18:00:00+00:00\"}\n");
            var mail = GrokbotMailbox.Open(folder);
            Assert.Equal("", mail.PreferencesCopy().WakeUrl);
            Assert.False(mail.PreferencesCopy().TabEnabled);
            Assert.True(mail.PreferencesCopy().AllowControl);
            var row = Assert.Single(mail.Messages);
            Assert.Equal("m1", row.Id);
            Assert.Equal("keep me", row.Text);
            Assert.True(row.AskToAct);
            Assert.Null(row.WakeNote);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Wake_key_round_trips_in_credential_manager_and_a_miss_does_not_throw()
    {
        var target = "SIDesk.GrokbotWake/test-" + Guid.NewGuid().ToString("N");
        try
        {
            Assert.Null(GrokbotWakeKey.Read(target));
            GrokbotWakeKey.Save(target, Secret);
            Assert.Equal(Secret, GrokbotWakeKey.Read(target));
            GrokbotWakeKey.Clear(target);
            Assert.Null(GrokbotWakeKey.Read(target));
            GrokbotWakeKey.Clear(target);
        }
        finally
        {
            try { GrokbotWakeKey.Clear(target); } catch (IOException) { }
        }
    }

    [Fact]
    public void Connector_choice_survives_a_failed_start()
    {
        var folder = Path.Combine(Path.GetTempPath(), "xai-desk-connector-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(folder).FullName, "connector-settings.json"), "{\"Enabled\":true,\"Bind\":1}");
            var loaded = ConnectorSettings.Load(folder);
            Assert.True(loaded.Enabled);
            Assert.Equal(ConnectorBindMode.Tailscale, loaded.Bind);
            var kept = ConnectorSettings.AfterFailedStart(loaded);
            kept.Save(folder);
            var again = ConnectorSettings.Load(folder);
            Assert.True(again.Enabled);
            Assert.Equal(ConnectorBindMode.Tailscale, again.Bind);
            Assert.NotEqual(ConnectorSettings.Off, again);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task Debounce_keeps_only_the_latest_message()
    {
        var seen = new List<string>();
        var debounce = new GrokbotWakeDebounce(
            (call, _) =>
            {
                seen.Add(call.MessageId);
                return Task.CompletedTask;
            },
            (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));
        debounce.Schedule(new GrokbotWakeCall("first", false, "one"));
        debounce.Schedule(new GrokbotWakeCall("second", true, "two"));
        await debounce.FlushAsync();
        Assert.Equal("second", Assert.Single(seen));
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> _codes;
        private readonly string _echo;
        public List<string> Authorizations { get; } = [];
        public List<string> Bodies { get; } = [];
        public List<string?> ContentTypes { get; } = [];

        public ScriptedHandler(string echo, params HttpStatusCode[] codes)
        {
            _echo = echo;
            _codes = new Queue<HttpStatusCode>(codes);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorizations.Add(request.Headers.Authorization?.ToString() ?? "");
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            ContentTypes.Add(request.Content?.Headers.ContentType?.MediaType);
            var code = _codes.Count > 0 ? _codes.Dequeue() : HttpStatusCode.OK;
            return new HttpResponseMessage(code) { Content = new StringContent(_echo) };
        }
    }
}
