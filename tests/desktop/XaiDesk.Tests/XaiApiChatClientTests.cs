using System.Net;
using System.Text.Json;
using Sovereign.Desktop.Client;
using Xunit;

namespace XaiDesk.Tests;

public sealed class XaiApiChatClientTests
{
    private const string Key = "unit-test-key";

    [Fact]
    public async Task ModelsAsync_returns_only_grok_ids()
    {
        using var client = new XaiApiChatClient(Key, new StubHandler(request =>
        {
            Assert.EndsWith("/models", request.RequestUri?.AbsolutePath);
            return Json("""{"data":[{"id":"grok-4"},{"id":"gpt-4o"}]}""");
        }));

        var models = await client.ModelsAsync(CancellationToken.None);

        Assert.Equal(["grok-4"], models);
        Assert.DoesNotContain("gpt-4o", models);
    }

    [Fact]
    public async Task SendAsync_streams_a_content_delta_until_done()
    {
        var body = "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}\ndata: [DONE]\n";
        using var client = new XaiApiChatClient(Key, new StubHandler(request =>
        {
            Assert.EndsWith("/chat/completions", request.RequestUri?.AbsolutePath);
            using var sent = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var messages = sent.RootElement.GetProperty("messages");
            Assert.Equal("system", messages[0].GetProperty("role").GetString());
            var system = messages[0].GetProperty("content").GetString();
            Assert.Contains("# Work Doctrine", system, StringComparison.Ordinal);
            Assert.DoesNotContain(Key, system, StringComparison.Ordinal);
            Assert.Equal("user", messages[1].GetProperty("role").GetString());
            Assert.Equal("ping", messages[1].GetProperty("content").GetString());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }));
        var text = "";

        await client.SendAsync("grok-4", History(), piece => text += piece, CancellationToken.None);

        Assert.Equal("Hello", text);
    }

    [Fact]
    public async Task Unauthorized_response_does_not_include_the_api_key()
    {
        using var client = new XaiApiChatClient(Key, new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("rejected " + Key) }));

        var error = await Assert.ThrowsAsync<IOException>(() =>
            client.SendAsync("grok-4", History(), _ => { }, CancellationToken.None));

        Assert.DoesNotContain(Key, error.Message);
    }

    [Fact]
    public async Task SendAsync_rejects_a_non_grok_model()
    {
        var called = false;
        using var client = new XaiApiChatClient(Key, new StubHandler(_ =>
        {
            called = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        await Assert.ThrowsAsync<IOException>(() =>
            client.SendAsync("gpt-4o", History(), _ => { }, CancellationToken.None));

        Assert.False(called);
    }

    private static ChatMessage[] History() => [new("user", "ping")];

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
