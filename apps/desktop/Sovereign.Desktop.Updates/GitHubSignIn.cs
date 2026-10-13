using System.Text.Json;

namespace Sovereign.Desktop.Updates;

// Tokens deliberately have no generated ToString(): they must never appear in logs.
public sealed class GitHubSession(string accessToken, string refreshToken, DateTimeOffset expiresAt)
{
    public string AccessToken { get; } = accessToken;
    public string RefreshToken { get; } = refreshToken;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
}

public sealed class GitHubDeviceCode(string deviceCode, string userCode, int interval, DateTimeOffset expiresAt)
{
    internal string DeviceCode { get; } = deviceCode;
    public string UserCode { get; } = userCode;
    public int Interval { get; } = interval;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
}

public sealed class GitHubSignIn : IDisposable
{
    public const string VerificationUrl = "https://github.com/login/device";
    private readonly HttpClient _http;
    private readonly string _clientId;
    public GitHubSignIn(string clientId, HttpMessageHandler? handler = null)
    {
        if (string.IsNullOrWhiteSpace(clientId)) throw new InvalidOperationException("GitHub updates have not been configured in this build.");
        _clientId = clientId;
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SI Desk-Updater/1.0");
    }
    private async Task<JsonDocument> PostAsync(string path, Dictionary<string, string> fields, CancellationToken ct)
    {
        fields["client_id"] = _clientId;
        using var response = await _http.PostAsync("https://github.com/login/" + path, new FormUrlEncodedContent(fields), ct);
        if (!response.IsSuccessStatusCode) throw new IOException("GitHub sign-in is unavailable. Try again shortly.");
        var body = await response.Content.ReadAsByteArrayAsync(ct);
        if (body.Length > 64_000) throw new IOException("Unexpected GitHub sign-in response.");
        return JsonDocument.Parse(body);
    }
    public async Task<GitHubDeviceCode> BeginAsync(CancellationToken ct)
    {
        using var json = await PostAsync("device/code", new(), ct);
        var root = json.RootElement;
        if (root.TryGetProperty("error", out _)) throw new IOException("GitHub device sign-in is not enabled for this updater.");
        if (root.GetProperty("verification_uri").GetString() != VerificationUrl) throw new IOException("Unexpected GitHub verification address.");
        var expiry = root.GetProperty("expires_in").GetInt32();
        var interval = root.GetProperty("interval").GetInt32();
        if (expiry is <= 0 or > 3600 || interval is <= 0 or > 60) throw new IOException("Invalid GitHub sign-in lifetime.");
        return new(root.GetProperty("device_code").GetString()!, root.GetProperty("user_code").GetString()!, interval, DateTimeOffset.UtcNow.AddSeconds(expiry));
    }
    public async Task<GitHubSession> CompleteAsync(GitHubDeviceCode code, CancellationToken ct)
    {
        var interval = code.Interval;
        while (DateTimeOffset.UtcNow < code.ExpiresAt)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            using var json = await PostAsync("oauth/access_token", new() { ["device_code"] = code.DeviceCode, ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code" }, ct);
            if (!json.RootElement.TryGetProperty("error", out var error)) return ReadSession(json.RootElement);
            switch (error.GetString())
            {
                case "authorization_pending": continue;
                case "slow_down": interval += 5; continue;
                case "access_denied": throw new IOException("GitHub sign-in was declined.");
                case "expired_token": throw new IOException("The sign-in code expired. Connect GitHub again.");
                default: throw new IOException("GitHub could not complete sign-in. Connect GitHub again.");
            }
        }
        throw new IOException("The sign-in code expired. Connect GitHub again.");
    }
    public async Task<GitHubSession> RefreshAsync(GitHubSession session, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(session.RefreshToken)) throw new IOException("Connect GitHub again to renew access to private updates.");
        using var json = await PostAsync("oauth/access_token", new() { ["grant_type"] = "refresh_token", ["refresh_token"] = session.RefreshToken }, ct);
        if (json.RootElement.TryGetProperty("error", out _)) throw new IOException("GitHub access expired or was revoked. Connect GitHub again.");
        return ReadSession(json.RootElement);
    }
    private static GitHubSession ReadSession(JsonElement root)
    {
        var access = root.GetProperty("access_token").GetString();
        if (string.IsNullOrWhiteSpace(access) || !string.Equals(root.GetProperty("token_type").GetString(), "bearer", StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid GitHub token response.");
        var seconds = root.TryGetProperty("expires_in", out var expiry) ? expiry.GetInt32() : 28800;
        if (seconds <= 0) throw new IOException("GitHub returned expired access.");
        return new(access, root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() ?? "" : "", DateTimeOffset.UtcNow.AddSeconds(seconds));
    }
    public void Dispose() => _http.Dispose();
}
