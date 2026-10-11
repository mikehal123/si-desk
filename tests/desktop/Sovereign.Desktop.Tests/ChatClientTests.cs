using System.Net;
using Sovereign.Desktop.Client;
using Xunit;

namespace Sovereign.Desktop.Tests;
public sealed class ChatClientTests
{
    private sealed class Handler(Func<HttpRequestMessage, string> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response(request)) });
    }
    [Fact]
    public async Task Ollama_stream_handles_chunks_and_requires_completion()
    {
        using var client = new OllamaChatClient(new Handler(r => r.RequestUri!.AbsolutePath == "/api/show" ? "{}" : "{\"message\":{\"content\":\"hello \"},\"done\":false}\n{\"message\":{\"content\":\"world\"},\"done\":true}\n"));
        var answer = "";
        await client.SendAsync("local", [new("user", "Hi")], s => answer += s, CancellationToken.None);
        Assert.Equal("hello world", answer);
    }
    [Fact]
    public async Task Ollama_broken_stream_is_not_a_successful_reply()
    {
        using var client = new OllamaChatClient(new Handler(r => r.RequestUri!.AbsolutePath == "/api/show" ? "{}" : "{\"message\":{\"content\":\"partial\"},\"done\":false}\n"));
        await Assert.ThrowsAsync<IOException>(() => client.SendAsync("local", [], _ => { }, CancellationToken.None));
    }
    [Fact]
    public async Task Ollama_remote_model_is_refused_before_chat()
    {
        var calls = 0;
        using var client = new OllamaChatClient(new Handler(r => { calls++; return "{\"remote_host\":\"https://ollama.com\"}"; }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync("remote", [], _ => { }, CancellationToken.None));
        Assert.Equal(1, calls);
    }
    [Theory]
    [InlineData("{\"message\":{\"content\":\"\"},\"done\":true,\"done_reason\":\"stop\"}")]
    [InlineData("{\"message\":{\"thinking\":\"still considering\"},\"done\":true,\"done_reason\":\"stop\"}")]
    [InlineData("{\"message\":{\"content\":\"partial\"},\"done\":true,\"done_reason\":\"length\"}")]
    [InlineData("{\"message\":{\"content\":\"calling tool\",\"tool_calls\":[{}]},\"done\":true}")]
    public async Task Ollama_invalid_final_does_not_report_success(string stream)
    {
        using var client = new OllamaChatClient(new Handler(r => r.RequestUri!.AbsolutePath == "/api/show" ? "{}" : stream));
        var success = false;
        await Assert.ThrowsAsync<IOException>(() => client.SendAsync("local", [], _ => { }, CancellationToken.None, completed: _ => success = true));
        Assert.False(success);
    }
    [Fact]
    public async Task Ollama_receipt_counts_final_content_without_exposing_thinking()
    {
        using var client = new OllamaChatClient(new Handler(r => r.RequestUri!.AbsolutePath == "/api/show" ? "{}" :
            "{\"message\":{\"thinking\":\"private trace\"},\"done\":false}\n{\"model\":\"local\",\"message\":{\"content\":\"ready\"},\"done\":true,\"done_reason\":\"stop\",\"eval_count\":3,\"eval_duration\":1000000000}\n"));
        var answer = "";
        OllamaChatReceipt? receipt = null;
        await client.SendAsync("local", [], s => answer += s, CancellationToken.None, completed: value => receipt = value);
        Assert.Equal("ready", answer);
        Assert.NotNull(receipt);
        Assert.Equal(3, receipt.OutputTokens);
        Assert.NotNull(receipt.FirstContentMs);
        Assert.Equal("stop", receipt.FinishReason);
    }
    [Fact]
    public async Task Ollama_stop_is_cancellation_even_when_server_sends_an_error()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new OllamaChatClient(new Handler(r => r.RequestUri!.AbsolutePath == "/api/show" ? "{}" :
            "{\"message\":{\"content\":\"started\"},\"done\":false}\n{\"error\":\"context canceled\"}\n"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync("local", [], _ => cancellation.Cancel(), cancellation.Token));
    }
}
