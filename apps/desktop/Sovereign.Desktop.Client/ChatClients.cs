using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace Sovereign.Desktop.Client;

public sealed record ChatMessage(string Role, string Content, string[]? Images = null);
public sealed record OllamaChatOptions(int ContextTokens = 8192, int MaxOutputTokens = 2048, double Temperature = 0.2, bool? Think = null);
public sealed record OllamaChatReceipt(string Model, string? FinishReason, double? FirstContentMs, double ElapsedMs,
    long? LoadDurationNs, long? PromptTokens, long? OutputTokens, long? EvaluationDurationNs);

public sealed class OllamaChatClient : IDisposable
{
    private readonly HttpClient _http;
    public OllamaChatClient(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri("http://127.0.0.1:11434/");
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }
    public async Task<string[]> ModelsAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("api/tags", ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("name").GetString()!)
            .Where(m => !m.Contains("cloud", StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    public async Task SendAsync(string model, IReadOnlyList<ChatMessage> history, Action<string> delta, CancellationToken ct,
        OllamaChatOptions? options = null, Action<OllamaChatReceipt>? completed = null)
    {
        var timer = Stopwatch.StartNew();
        double? firstContentMs = null;
        var hasFinalContent = false;
        var observedModel = model;
        options ??= new();
        // A local Ollama server can advertise cloud models. Require a local model before sending chat.
        using var show = await _http.PostAsJsonAsync("api/show", new { model }, ct);
        if (show.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("Ollama lists this tag but cannot load its model. Repair or download that model in Ollama, or choose another model.");
        show.EnsureSuccessStatusCode();
        using var detail = JsonDocument.Parse(await show.Content.ReadAsStringAsync(ct));
        if (detail.RootElement.TryGetProperty("remote_host", out var remote) && !string.IsNullOrWhiteSpace(remote.GetString()))
            throw new InvalidOperationException("This is a remote model. Choose a downloaded local model for the Ollama room.");
        if (history.Any(m => m.Images is { Length: > 0 }) && !(detail.RootElement.TryGetProperty("capabilities", out var caps) && caps.EnumerateArray().Any(c => c.GetString() == "vision")))
            throw new IOException("This Ollama model does not advertise vision. Choose a vision model or remove the images.");
        var body = new Dictionary<string, object>
        {
            ["model"] = model, ["messages"] = history.Select(m => new { role = m.Role, content = m.Content, images = m.Images ?? [] }), ["stream"] = true,
            ["options"] = new { num_ctx = options.ContextTokens, num_predict = options.MaxOutputTokens, temperature = options.Temperature }
        };
        if (options.Think is { } think && detail.RootElement.TryGetProperty("capabilities", out var capabilities)
            && capabilities.EnumerateArray().Any(c => c.GetString() == "thinking")) body["think"] = think;
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/chat")
        {
            Content = JsonContent.Create(body)
        };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var chunk = JsonDocument.Parse(line);
            var root = chunk.RootElement;
            if (root.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.GetString());
            if (root.TryGetProperty("model", out var identity)) observedModel = identity.GetString() ?? model;
            if (root.TryGetProperty("message", out var message))
            {
                if (message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array && calls.GetArrayLength() > 0)
                    throw new IOException("This chat cannot execute model tool calls. No complete answer was received.");
                if (message.TryGetProperty("content", out var content))
                {
                    var text = content.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(text)) { hasFinalContent = true; firstContentMs ??= timer.Elapsed.TotalMilliseconds; }
                    delta(text);
                }
            }
            if (root.TryGetProperty("done", out var done) && done.GetBoolean())
            {
                var reason = root.TryGetProperty("done_reason", out var finish) ? finish.GetString() : null;
                if (reason is not null && reason != "stop")
                    throw new IOException("Ollama ended with '" + reason + "'. The reply is incomplete; try a shorter question or another model.");
                if (!hasFinalContent)
                    throw new IOException("Ollama returned no final answer. Thinking alone is not a completed reply; try again or choose another model.");
                long? Number(string name) => root.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : null;
                completed?.Invoke(new(observedModel, reason, firstContentMs, timer.Elapsed.TotalMilliseconds,
                    Number("load_duration"), Number("prompt_eval_count"), Number("eval_count"), Number("eval_duration")));
                return;
            }
        }
        ct.ThrowIfCancellationRequested();
        throw new IOException("Ollama disconnected before confirming completion. The reply is incomplete.");
    }
    public void Dispose() => _http.Dispose();
}
