using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Sovereign.Desktop.Client;

/// <summary>Direct xAI API chat. The key is supplied by the caller and is never written to logs or exceptions.</summary>
public sealed class XaiApiChatClient : IDisposable
{
    public const string DefaultBaseUrl = "https://api.x.ai/v1/";
    private readonly HttpClient _http;
    private readonly string _apiKey;

    public XaiApiChatClient(string apiKey, HttpMessageHandler? handler = null, string? baseUrl = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("Save an xAI API key in Settings first.");
        _apiKey = apiKey.Trim();
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl);
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SIDesk/0.1");
    }

    public async Task<string[]> ModelsAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("models", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new IOException("xAI API model list failed (" + (int)response.StatusCode + "). " + Safe(body));
        using var json = JsonDocument.Parse(body);
        if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new IOException("xAI API model list was empty.");
        var models = data.EnumerateArray()
            .Select(m => m.TryGetProperty("id", out var id) ? id.GetString() : null)
            .Where(Sovereign.Desktop.Core.XaiDeskIdentity.IsGrokModel)
            .Select(m => m!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (models.Length == 0) throw new IOException("xAI API did not advertise a Grok model.");
        return models;
    }

    public async Task SendAsync(string model, IReadOnlyList<ChatMessage> history, Action<string> delta, CancellationToken ct)
    {
        if (!Sovereign.Desktop.Core.XaiDeskIdentity.IsGrokModel(model))
            throw new IOException("Choose a Grok model.");
        var doctrine = Sovereign.Desktop.Core.WorkDoctrine.PromptPrefix().Trim();
        var payload = new
        {
            model,
            stream = true,
            messages = history.Select(m => new { role = m.Role, content = m.Content })
                .Prepend(new { role = "system", content = doctrine })
                .ToArray()
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(payload)
        };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            throw new IOException("xAI API chat failed (" + (int)response.StatusCode + "). " + Safe(error));
        }
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var produced = false;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payloadText = line["data:".Length..].Trim();
            if (payloadText.Length == 0) continue;
            if (payloadText == "[DONE]")
            {
                if (!produced) throw new IOException("xAI API returned no answer.");
                return;
            }
            using var chunk = JsonDocument.Parse(payloadText);
            if (!chunk.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
            var choice = choices[0];
            if (choice.TryGetProperty("delta", out var deltaElement) && deltaElement.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                var text = content.GetString() ?? "";
                if (text.Length > 0) produced = true;
                delta(text);
            }
        }
        if (!produced) throw new IOException("xAI API disconnected before an answer.");
    }

    private string Safe(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "No details were returned.";
        var text = body.Length > 180 ? body[..180] : body;
        if (_apiKey.Length > 0) text = text.Replace(_apiKey, "[redacted]", StringComparison.Ordinal);
        return text.Replace("\r", " ").Replace("\n", " ");
    }

    public void Dispose() => _http.Dispose();
}
