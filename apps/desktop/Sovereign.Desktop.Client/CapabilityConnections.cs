using System.Text.Json;

namespace Sovereign.Desktop.Client;

public sealed record LocalServiceCheck(string Name, bool Reachable, string Summary, DateTimeOffset CheckedAt);
public sealed record DiscoveryFinding(string Id, string Title, string Summary, string SourceUrl, string PublishedAt, string RetrievedAt, string Verdict);
public sealed record DiscoveryResults(DateTimeOffset CheckedAt, IReadOnlyList<DiscoveryFinding> Findings);

/// <summary>Read-only connections to already-running services on this computer.</summary>
public sealed class CapabilityConnections : IDisposable
{
    private readonly HttpClient _http;
    private const int MaxResponseBytes = 2_000_000;
    public CapabilityConnections(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        { Timeout = TimeSpan.FromSeconds(5) };
    }

    private async Task<JsonDocument> ReadAsync(string url, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxResponseBytes) throw new IOException("Response too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, deadline.Token)) > 0)
        {
            if (bytes.Length + count > MaxResponseBytes) throw new IOException("Response too large.");
            bytes.Write(buffer, 0, count);
        }
        return JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
    }

    private static string Text(JsonElement item, string key, int max = 1000)
    {
        if (!item.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String) return "Unknown";
        var text = value.GetString() ?? "";
        return text.Length <= max ? text : text[..max] + "…";
    }

    private static int Count(JsonElement item, string key)
    {
        var result = item.GetProperty(key).GetInt32();
        if (result < 0) throw new JsonException("Invalid count.");
        return result;
    }

    private async Task<LocalServiceCheck> CheckAsync(string name, string url, Func<JsonElement, string> describe, CancellationToken ct)
    {
        try
        {
            using var doc = await ReadAsync(url, ct);
            return new(name, true, describe(doc.RootElement), DateTimeOffset.Now);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OperationCanceledException)
        {
            var reason = ex is HttpRequestException or OperationCanceledException
                ? "Not reachable. Start the service on this PC, then check again."
                : "The response could not be verified. No working connection is claimed.";
            return new(name, false, reason, DateTimeOffset.Now);
        }
    }

    public Task<LocalServiceCheck[]> CheckAsync(CancellationToken ct = default) => Task.WhenAll(
        CheckAsync("Curiosity Network", "http://127.0.0.1:8764/api/status", root =>
        {
            var state = root.GetProperty("state").GetString();
            if (string.IsNullOrWhiteSpace(state)) throw new JsonException("Missing state.");
            var counts = root.GetProperty("desk_counts");
            var auto = root.GetProperty("governor").GetProperty("auto_apply").GetBoolean();
            return $"{state} · {Count(counts, "store")} stored · {Count(counts, "inbox")} in inbox · automatic application {(auto ? "ON" : "off")}. Findings can be read below; scans and verdict changes are not connected.";
        }, ct),
        CheckAsync("Ollama models", "http://127.0.0.1:11434/api/tags", root =>
        {
            var models = root.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("name").GetString()).ToArray();
            if (models.Any(string.IsNullOrWhiteSpace)) throw new JsonException("Missing model name.");
            return models.Length == 0 ? "Service reached; no installed models. Add a model in Ollama to use local chat."
                : $"{models.Length} installed: {string.Join(", ", models.Take(8))}. Availability check only; no answer has been generated.";
        }, ct));

    public async Task<DiscoveryResults> ReadFindingsAsync(CancellationToken ct = default)
    {
        using var doc = await ReadAsync("http://127.0.0.1:8764/api/findings?limit=25&view=yours", ct);
        var findings = doc.RootElement.GetProperty("findings").EnumerateArray().Take(25).Select(item =>
        {
            var id = item.GetProperty("id").GetString();
            var title = item.GetProperty("title").GetString();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) throw new JsonException("Missing finding identity.");
            var verdict = item.TryGetProperty("verdict", out var v) && v.ValueKind == JsonValueKind.Object ? Text(v, "action", 40) : "Not recorded";
            return new DiscoveryFinding(id, title.Length > 400 ? title[..400] : title, Text(item, "summary", 8000),
                Text(item, "url", 2000), Text(item, "published_at", 80), Text(item, "retrieved_at", 80), verdict);
        }).ToArray();
        return new(DateTimeOffset.Now, findings);
    }

    public void Dispose() => _http.Dispose();
}
