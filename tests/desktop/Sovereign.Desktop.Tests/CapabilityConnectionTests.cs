using System.Net;
using Sovereign.Desktop.Client;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class CapabilityConnectionTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("127.0.0.1", request.RequestUri!.Host);
            Requests.Add(request.RequestUri.AbsoluteUri);
            return Task.FromResult(reply(request));
        }
    }
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    private const string Cn = """{"state":"IDLE","desk_counts":{"store":703,"inbox":0},"governor":{"auto_apply":false}}""";

    [Fact]
    public async Task Connection_checks_only_read_local_metadata_and_do_not_claim_inference()
    {
        using var handler = new Handler(r => r.RequestUri!.Port == 8764 ? Json(Cn) : Json("""{"models":[{"name":"gemma4:26b"}]}"""));
        using var client = new CapabilityConnections(handler);
        var results = await client.CheckAsync();
        Assert.All(results, r => Assert.True(r.Reachable));
        Assert.Contains("703", results[0].Summary);
        Assert.Contains("no answer has been generated", results[1].Summary);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("http://127.0.0.1:8764/api/status", handler.Requests);
        Assert.Contains("http://127.0.0.1:11434/api/tags", handler.Requests);
        Assert.DoesNotContain(handler.Requests, r => r.Contains("generate") || r.Contains("completions") || r.Contains("scan") || r.Contains("findings"));
    }

    [Fact]
    public async Task Failed_Ollama_does_not_hide_working_CN()
    {
        using var client = new CapabilityConnections(new Handler(r => r.RequestUri!.Port == 11434
            ? new(HttpStatusCode.ServiceUnavailable) : Json(Cn)));
        var checks = await client.CheckAsync();
        Assert.True(checks[0].Reachable);
        Assert.False(checks[1].Reachable);
        Assert.Contains("703", checks[0].Summary);
        Assert.Contains("scans and verdict changes are not connected", checks[0].Summary);
    }

    [Fact]
    public async Task Failed_CN_does_not_hide_working_Ollama()
    {
        using var client = new CapabilityConnections(new Handler(r => r.RequestUri!.Port == 8764
            ? new(HttpStatusCode.ServiceUnavailable) : Json("""{"models":[]}""")));
        var checks = await client.CheckAsync();
        Assert.False(checks[0].Reachable);
        Assert.True(checks[1].Reachable);
        Assert.Contains("no installed models", checks[1].Summary);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("<html>not an API</html>")]
    [InlineData("{\"state\":\"IDLE\",\"desk_counts\":{\"store\":0,\"inbox\":0},\"governor\":{}}")]
    public async Task Http_200_without_valid_service_data_is_not_reported_connected(string response)
    {
        using var client = new CapabilityConnections(new Handler(_ => Json(response)));
        Assert.All(await client.CheckAsync(), r => Assert.False(r.Reachable));
    }

    [Fact]
    public async Task Findings_preserve_collection_dates_and_operator_verdict_without_loading_embeddings()
    {
        using var handler = new Handler(_ => Json("""{"findings":[{"id":"abc","title":"Saved research","summary":"Evidence","url":"https://example.com/paper","published_at":"2026-09-12","retrieved_at":"2026-09-13","verdict":{"action":"keep"},"embedding":[0.1,0.2]}]}"""));
        using var client = new CapabilityConnections(handler);
        var result = await client.ReadFindingsAsync();
        var finding = Assert.Single(result.Findings);
        Assert.Equal("keep", finding.Verdict);
        Assert.Equal("2026-09-12", finding.PublishedAt);
        Assert.Equal("2026-09-13", finding.RetrievedAt);
        Assert.Equal("https://example.com/paper", finding.SourceUrl);
        Assert.Equal("Evidence", finding.Summary);
        Assert.NotEqual(result.CheckedAt.ToString("yyyy-MM-dd"), finding.RetrievedAt);
        Assert.NotEqual(result.CheckedAt.ToString("o"), finding.PublishedAt);
        Assert.EndsWith("/api/findings?limit=25&view=yours", Assert.Single(handler.Requests));
        Assert.DoesNotContain(handler.Requests, r => r.Contains("generate") || r.Contains("completions") || r.Contains("/scan") || r.Contains("embeddings"));
    }

    [Fact]
    public async Task Missing_finding_dates_stay_unknown_and_are_not_replaced_with_check_time()
    {
        using var handler = new Handler(_ => Json("""{"findings":[{"id":"xyz","title":"No dates","summary":"Body","url":"https://example.com/x"}]}"""));
        using var client = new CapabilityConnections(handler);
        var result = await client.ReadFindingsAsync();
        var finding = Assert.Single(result.Findings);
        Assert.Equal("Unknown", finding.PublishedAt);
        Assert.Equal("Unknown", finding.RetrievedAt);
        Assert.Equal("Not recorded", finding.Verdict);
        Assert.DoesNotContain(result.CheckedAt.ToString("yyyy-MM-dd"), finding.PublishedAt);
        Assert.DoesNotContain(result.CheckedAt.ToString("yyyy-MM-dd"), finding.RetrievedAt);
        Assert.NotEqual(result.CheckedAt.ToString("o"), finding.PublishedAt);
        Assert.NotEqual(result.CheckedAt.ToString("o"), finding.RetrievedAt);
    }

    [Fact]
    public async Task Oversized_response_cannot_be_reported_connected()
    {
        using var client = new CapabilityConnections(new Handler(_ => Json(new string('x', 2_000_001))));
        Assert.All(await client.CheckAsync(), r => Assert.False(r.Reachable));
    }

    [Fact]
    public async Task Cancellation_is_not_converted_to_a_successful_empty_result()
    {
        using var client = new CapabilityConnections(new Handler(_ => throw new OperationCanceledException()));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ReadFindingsAsync(cancel.Token));
    }
}
