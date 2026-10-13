using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Sovereign.Desktop.Client;

/// <summary>IMIGROK provider mapping, restricted to explicitly chosen coding-plan services.</summary>
public sealed partial class CodingPlanChatClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _provider;
    public static string[] Endpoints(string provider) => provider == "glm"
        ? ["https://api.z.ai/api/coding/paas/v4/", "https://open.bigmodel.cn/api/coding/paas/v4/"]
        : provider == "kimi" ? ["https://api.kimi.com/coding/v1/", "https://api.kimi.ai/coding/v1/"]
        : provider == "moonshot" ? ["https://api.moonshot.ai/v1/"]
        : throw new ArgumentException("Unknown provider");

    public CodingPlanChatClient(string provider, string endpoint, string key, HttpMessageHandler? handler = null)
    {
        if (!Endpoints(provider).Contains(endpoint, StringComparer.Ordinal)) throw new ArgumentException("Select a supported endpoint for this provider.");
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("A provider key is required.");
        _provider = provider;
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(endpoint), Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("xAI Desk/0.3.1");
    }
    public async Task<string[]> ModelsAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("models",ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound || response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed) return [];
        RequireSuccess(response);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var models = document.RootElement.GetProperty("data").EnumerateArray().Select(m=>m.GetProperty("id").GetString()!).ToArray();
        // Provider catalog order can change; start platform chat with the verified general chat model.
        return _provider == "moonshot" ? models.OrderBy(m => m == "kimi-k2.6" ? 0 : 1).ThenBy(m => m, StringComparer.Ordinal).ToArray() : models;
    }
    public async Task SendAsync(string model, IReadOnlyList<ChatMessage> history, Action<string> delta, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,"chat/completions") { Content = JsonContent.Create(RequestBody(model, history)) };
        using var response = await _http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        RequireSuccess(response);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        var finished=false;
        while(await reader.ReadLineAsync(ct) is { } line)
        {
            if(!line.StartsWith("data:",StringComparison.Ordinal)) continue;
            var data=line[5..].Trim();
            if(data=="[DONE]") { if(finished) return; throw new IOException("Provider ended without a completed answer."); }
            using var chunk=JsonDocument.Parse(data);
            if(chunk.RootElement.TryGetProperty("error",out _)) throw new IOException("Provider rejected this request. Check account access and model availability.");
            if(!chunk.RootElement.TryGetProperty("choices",out var choices)) continue;
            foreach(var choice in choices.EnumerateArray())
            {
                if(choice.TryGetProperty("delta",out var d) && d.TryGetProperty("content",out var content) && content.ValueKind==JsonValueKind.String) delta(content.GetString()!);
                if(choice.TryGetProperty("finish_reason",out var reason) && reason.ValueKind==JsonValueKind.String)
                {
                    if(reason.GetString()!="stop") throw new IOException("Reply incomplete: " + reason.GetString());
                    finished=true;
                }
            }
        }
        if(!finished) throw new IOException("Connection ended before a complete reply.");
    }
    private object RequestBody(string model, IReadOnlyList<ChatMessage> history)
    {
        var body = new Dictionary<string, object> { ["model"] = model, ["stream"] = true,
            ["max_tokens"] = 2048, ["messages"] = history.Select(m => new { role = m.Role, content = m.Content }) };
        if (_provider == "moonshot") body["thinking"] = new { type = "disabled" };
        return body;
    }
    private void RequireSuccess(HttpResponseMessage response)
    {
        if(response.IsSuccessStatusCode) return;
        var hint = _provider == "moonshot"
            ? "Use your platform.kimi.ai API key. This connection uses Kimi Platform API billing."
            : _provider == "kimi" ? "This connection requires Kimi Code credentials. For a platform.kimi.ai key, select Kimi Platform API."
            : "Check the selected region, coding-plan key, permissions or quota.";
        throw new IOException($"Provider returned HTTP {(int)response.StatusCode}. {hint} No alternate endpoint was used.");
    }
    public void Dispose()=>_http.Dispose();
}

public static class ImigrokCredentialImport
{
    // Parse data only. Never execute .env contents or copy unrelated credentials.
    public static string ReadKey(string path,string provider)
    {
        if(new FileInfo(path).Length>2_000_000) throw new IOException("Credential file is too large.");
        var text=File.ReadAllText(path);
        if (provider is not ("glm" or "kimi" or "moonshot")) throw new ArgumentException("Unknown provider");
        var keys=provider=="moonshot" ? new[]{"MOONSHOT_API_KEY","KIMI_API_KEY","KIMI_CN_API_KEY"} : provider=="glm" ? new[]{"GLM_API_KEY","ZAI_API_KEY","Z_AI_API_KEY"} : new[]{"KIMI_API_KEY","KIMI_CODING_API_KEY","KIMI_CN_API_KEY"};
        if(Path.GetExtension(path).Equals(".json",StringComparison.OrdinalIgnoreCase))
        {
            using var doc=JsonDocument.Parse(text);
            var ids=provider=="moonshot" ? new[]{"moonshot","kimi"} : provider=="glm" ? new[]{"zai"} : new[]{"kimi-coding","kimi-coding-cn"};
            var found=new HashSet<string>(StringComparer.Ordinal);
            if(doc.RootElement.TryGetProperty("providers",out var providers))
                foreach(var id in ids) if(providers.TryGetProperty(id,out var p) && p.TryGetProperty("api_key",out var key) && key.ValueKind==JsonValueKind.String && !string.IsNullOrWhiteSpace(key.GetString())) found.Add(key.GetString()!);
            if(doc.RootElement.TryGetProperty("credential_pool",out var pool))
                foreach(var id in ids) if(pool.TryGetProperty(id,out var entries) && entries.ValueKind==JsonValueKind.Array)
                    foreach(var entry in entries.EnumerateArray())
                    {
                        if(entry.TryGetProperty("auth_type",out var type) && type.GetString()!="api_key") continue;
                        if((entry.TryGetProperty("api_key",out var key) || entry.TryGetProperty("access_token",out key)) && key.ValueKind==JsonValueKind.String && !string.IsNullOrWhiteSpace(key.GetString())) found.Add(key.GetString()!);
                    }
            if(found.Count==1) return found.Single();
            throw new IOException(found.Count>1 ? "Multiple provider keys found. Paste the intended coding-plan key instead." : "No matching API key found in this auth file. Select IMIGROK’s .env file or paste the provider key.");
        }
        var values=new HashSet<string>(StringComparer.Ordinal);
        foreach(var raw in text.Split('\n'))
        {
            var line=raw.Trim(); if(line.StartsWith("export ")) line=line[7..].Trim();
            var at=line.IndexOf('='); if(at<0 || !keys.Contains(line[..at].Trim())) continue;
            var value=line[(at+1)..].Trim();
            if(value.Length>=2 && (value[0]=='\''&&value[^1]=='\'' || value[0]=='"'&&value[^1]=='"')) value=value[1..^1];
            else { var comment=value.IndexOf(" #",StringComparison.Ordinal); if(comment>=0) value=value[..comment].Trim(); }
            if(value.Contains('$') || value.Contains('`')) throw new IOException("Computed .env values are not executed. Paste the provider key.");
            if(!string.IsNullOrWhiteSpace(value)) values.Add(value);
        }
        if(values.Count==1) return values.Single();
        throw new IOException(values.Count>1 ? "Multiple provider keys found; paste the intended key." : "No matching key found in the selected file.");
    }
}
