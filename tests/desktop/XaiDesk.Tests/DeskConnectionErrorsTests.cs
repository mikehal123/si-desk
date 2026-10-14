using System.Net;
using System.Net.Http;
using Sovereign.Desktop.Client;
using Xunit;

namespace XaiDesk.Tests;

public sealed class DeskConnectionErrorsTests
{
    [Theory]
    [InlineData(DeskConnectKind.XaiApi, DeskConnectionErrors.XaiNetwork)]
    [InlineData(DeskConnectKind.Cursor, DeskConnectionErrors.CursorNetwork)]
    [InlineData(DeskConnectKind.GrokBuild, DeskConnectionErrors.GrokNetwork)]
    public void Network_failures_name_the_provider_and_the_next_step(DeskConnectKind kind, string expected)
    {
        var text = DeskConnectionErrors.ForConnect(new HttpRequestException("No such host is known."), kind);
        Assert.Equal(expected, text);
        Assert.Contains("network", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Connect / refresh models", text);
        Assert.DoesNotContain("No such host", text);
    }

    [Fact]
    public void Missing_xai_key_says_where_to_save_it()
    {
        var text = DeskConnectionErrors.ForConnect(new InvalidOperationException("anything about an API key"), DeskConnectKind.XaiApi);
        Assert.Equal(DeskConnectionErrors.XaiKeyMissing, text);
        Assert.Contains("Settings", text);
    }

    [Fact]
    public void Auth_failures_do_not_repeat_the_provider_body()
    {
        const string secret = "super-secret-token";
        var text = DeskConnectionErrors.ForConnect(new IOException("401 unauthorized " + secret), DeskConnectKind.XaiApi);
        Assert.Equal(DeskConnectionErrors.XaiAuthFailed, text);
        Assert.Contains("auth failed", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, text);
    }

    [Fact]
    public void Cursor_not_signed_in_and_grok_missing_are_distinct()
    {
        var cursor = DeskConnectionErrors.ForConnect(new IOException("not signed in"), DeskConnectKind.Cursor);
        var grok = DeskConnectionErrors.ForConnect(new FileNotFoundException("missing"), DeskConnectKind.GrokBuild);
        Assert.Equal(DeskConnectionErrors.CursorNotSignedIn, cursor);
        Assert.Equal(DeskConnectionErrors.GrokNotInstalled, grok);
        Assert.Contains("PATH", grok);
        Assert.Contains("Sign in with Cursor", cursor);
    }

    [Fact]
    public void Connect_timeout_is_actionable()
    {
        var text = DeskConnectionErrors.ForConnect(new OperationCanceledException(), DeskConnectKind.Other);
        Assert.Equal(DeskConnectionErrors.ConnectTimedOut, text);
        Assert.Contains("network", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("API key", text);
        Assert.Contains("PATH", text);
    }

    [Fact]
    public void Unclear_refresh_does_not_echo_the_raw_exception()
    {
        var text = DeskConnectionErrors.ForConnect(new IOException("something went wrong: secret-body"), DeskConnectKind.Cursor);
        Assert.Equal(DeskConnectionErrors.CursorRefreshFailed, text);
        Assert.DoesNotContain("secret-body", text);
        Assert.Contains("sign in", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejected_model_list_does_not_include_the_key()
    {
        const string key = "unit-test-key";
        using var client = new XaiApiChatClient(key, new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("rejected " + key) }));
        var error = await Assert.ThrowsAsync<IOException>(() => client.ModelsAsync(CancellationToken.None));
        Assert.Equal(DeskConnectionErrors.XaiAuthFailed, error.Message);
        Assert.DoesNotContain(key, error.Message);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
