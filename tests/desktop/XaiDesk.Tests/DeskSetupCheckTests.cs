using System.Net;
using System.Net.Http;
using Sovereign.Desktop.Client;
using Xunit;

namespace XaiDesk.Tests;

public sealed class DeskSetupCheckTests
{
    [Fact]
    public void Each_item_is_ok_or_missing_with_one_fix_line()
    {
        var ready = DeskSetupCheck.Evaluate(DeskKeyVerdict.Valid, cursorRuntime: true, cursorSignedIn: true, grokFound: true);
        Assert.All(ready.Items, item => Assert.Equal("OK", item.Line));

        var empty = DeskSetupCheck.Evaluate(DeskKeyVerdict.Absent, cursorRuntime: false, cursorSignedIn: false, grokFound: false);
        Assert.All(empty.Items, item =>
        {
            Assert.StartsWith("Missing. ", item.Line);
            Assert.Equal("Missing", item.State);
            Assert.DoesNotContain("\n", item.Fix);
            Assert.False(string.IsNullOrWhiteSpace(item.Fix));
        });
        Assert.Contains("API key", empty.XaiApi.Fix);
        Assert.Contains("Cursor connector", empty.Cursor.Fix);
        Assert.Contains("PATH", empty.Grok.Fix);
    }

    [Theory]
    [InlineData(DeskKeyVerdict.Rejected, "Auth failed")]
    [InlineData(DeskKeyVerdict.Network, "Network")]
    [InlineData(DeskKeyVerdict.NoGrok, "no Grok model")]
    [InlineData(DeskKeyVerdict.Unavailable, "could not be checked")]
    public void Key_problems_stay_missing_and_name_the_fix(DeskKeyVerdict verdict, string fix)
    {
        var item = DeskSetupCheck.XaiItem(verdict);
        Assert.False(item.Ok);
        Assert.Contains(fix, item.Line);
    }

    [Fact]
    public void Installed_cursor_without_login_asks_for_sign_in()
    {
        var item = DeskSetupCheck.CursorItem(runtimeInstalled: true, signedIn: false);
        Assert.Equal("Missing", item.State);
        Assert.Contains("Sign in with Cursor", item.Line);
        Assert.DoesNotContain("connector", item.Line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Probe_classifies_the_key_without_copying_it_into_the_report()
    {
        const string key = "unit-test-key";
        var absent = await DeskSetupCheck.ProbeXaiKeyAsync("  ", CancellationToken.None);
        Assert.Equal(DeskKeyVerdict.Absent, absent);

        var rejected = await DeskSetupCheck.ProbeXaiKeyAsync(key, CancellationToken.None, new StatusHandler(HttpStatusCode.Unauthorized, key));
        Assert.Equal(DeskKeyVerdict.Rejected, rejected);

        var valid = await DeskSetupCheck.ProbeXaiKeyAsync(key, CancellationToken.None, new StatusHandler(HttpStatusCode.OK,
            """{"data":[{"id":"grok-4"}]}"""));
        Assert.Equal(DeskKeyVerdict.Valid, valid);

        var noGrok = await DeskSetupCheck.ProbeXaiKeyAsync(key, CancellationToken.None, new StatusHandler(HttpStatusCode.OK,
            """{"data":[{"id":"other"}]}"""));
        Assert.Equal(DeskKeyVerdict.NoGrok, noGrok);

        var network = await DeskSetupCheck.ProbeXaiKeyAsync(key, CancellationToken.None, new ThrowingHandler());
        Assert.Equal(DeskKeyVerdict.Network, network);

        var report = await DeskSetupCheck.CollectAsync(new DeskSetupSources
        {
            XaiApiKey = key,
            XaiHandler = new StatusHandler(HttpStatusCode.Unauthorized, "rejected " + key),
            CursorRuntimeInstalled = true,
            CursorSignedIn = false,
            GrokFound = true
        }, CancellationToken.None);
        var shown = string.Join(" ", report.Items.Select(item => item.Name + " " + item.Line));
        Assert.DoesNotContain(key, shown);
        Assert.Equal("Missing", report.XaiApi.State);
        Assert.Contains("Auth failed", report.XaiApi.Line);
        Assert.Equal("Missing", report.Cursor.State);
        Assert.Equal("OK", report.Grok.Line);
    }

    [Fact]
    public void Cursor_login_detects_a_credential_and_does_not_return_it()
    {
        const string token = "present";
        Assert.True(CursorLoginProbe.HasCredential("{\"accessToken\":\"" + token + "\"}"));
        Assert.True(CursorLoginProbe.HasCredential("{\"auth\":{\"refresh_token\":\"present\"}}"));
        Assert.False(CursorLoginProbe.HasCredential("{\"email\":\"ada@example.com\"}"));
        Assert.False(CursorLoginProbe.HasCredential("{\"accessToken\":\"\"}"));
        Assert.False(CursorLoginProbe.HasCredential("not json"));

        var seen = new List<string>();
        string? Read(string path)
        {
            seen.Add(path);
            return path.EndsWith("auth.json", StringComparison.Ordinal) ? "{\"apiKey\":\"present\"}" : "{\"email\":\"ada@example.com\"}";
        }
        static bool Exists(string path) =>
            path.EndsWith("cli-config.json", StringComparison.Ordinal) || path.EndsWith("auth.json", StringComparison.Ordinal);
        Assert.True(CursorLoginProbe.IsSignedIn("profile", Exists, Read));
        Assert.DoesNotContain(token, string.Join(" ", seen));
        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public void First_run_prompt_shows_once()
    {
        var directory = Path.Combine(Path.GetTempPath(), "setup-prompt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.True(DeskSetupPrompt.ShouldShow(directory));
            DeskSetupPrompt.MarkShown(directory);
            Assert.False(DeskSetupPrompt.ShouldShow(directory));
            Assert.False(DeskSetupPrompt.ShouldShow(" "));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private sealed class StatusHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("network down");
    }
}
