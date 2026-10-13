using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Sovereign.Desktop.Updates;

public sealed class UpdateClient : IDisposable
{
    public const string RepositoryApi = "https://api.github.com/repos/mikehal123/xai-desk";
    private readonly HttpClient _http;
    private readonly string? _token;
    public UpdateClient(string? token = null, HttpMessageHandler? handler = null)
    {
        _token = token;
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("xAI Desk-Updater/1.0");
    }
    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken ct, bool binary = false)
    {
        for (var i = 0; i < 6; i++)
        {
            if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
                !(uri.Host == "api.github.com" || uri.Host == "github.com" || uri.Host == "release-assets.githubusercontent.com" || uri.Host == "objects.githubusercontent.com"))
                throw new IOException("Unexpected update server redirect.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (uri.Host == "api.github.com")
            {
                if (!uri.AbsolutePath.StartsWith("/repos/mikehal123/xai-desk/", StringComparison.Ordinal)) throw new IOException("Unexpected update repository.");
                if (!string.IsNullOrEmpty(_token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                request.Headers.Accept.ParseAdd(binary ? "application/octet-stream" : "application/vnd.github+json");
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            }
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            { uri = new Uri(uri, location); response.Dispose(); continue; }
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                var status = response.StatusCode; response.Dispose();
                throw new IOException(status == HttpStatusCode.Unauthorized ? "GitHub access expired or was revoked. Connect GitHub again." : "GitHub could not provide this private release. Check your repository invitation and the updater app’s installation, or try again after a rate limit clears.");
            }
            try { response.EnsureSuccessStatusCode(); return response; } catch { response.Dispose(); throw; }
        }
        throw new IOException("Too many update redirects.");
    }
    private async Task<Uri> FindAssetAsync(string releasePath, string name, string? expectedUrl, long? expectedBytes, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_token)) throw new IOException("Connect GitHub in Settings to receive private updates.");
        using var response = await GetAsync(new Uri(RepositoryApi + "/releases/" + releasePath), ct);
        var bytes = await ReadBoundedAsync(response, 1_000_000, ct);
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) throw new IOException("This release is not ready for updates.");
        var matches = root.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == name).ToArray();
        if (matches.Length != 1) throw new IOException("The release does not contain a unique update asset.");
        var asset = matches[0];
        var id = asset.GetProperty("id").GetInt64();
        if (id <= 0 || (expectedUrl is not null && asset.GetProperty("browser_download_url").GetString() != expectedUrl) ||
            (expectedBytes.HasValue && asset.GetProperty("size").GetInt64() != expectedBytes.Value)) throw new IOException("Release asset does not match signed update information.");
        return new Uri(RepositoryApi + "/releases/assets/" + id);
    }
    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int limit, CancellationToken ct)
    {
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192]; int n;
        while ((n = await body.ReadAsync(chunk, ct)) > 0)
        { if (buffer.Length + n > limit) throw new IOException("Update information is too large."); await buffer.WriteAsync(chunk.AsMemory(0, n), ct); }
        return buffer.ToArray();
    }
    public async Task<(UpdateRelease Release, string Envelope)> CheckAsync(CancellationToken ct)
    {
        await Task.CompletedTask;
        throw new InvalidOperationException("Updates are disabled.");
    }

    private async Task<(UpdateRelease Release, string Envelope)> CheckAsyncDisabled(CancellationToken ct)
    {
        var asset = await FindAssetAsync("latest", "update.json", null, null, ct);
        using var response = await GetAsync(asset, ct, true);
        var envelope = System.Text.Encoding.UTF8.GetString(await ReadBoundedAsync(response, 64_000, ct));
        return (UpdatePackage.Verify(envelope, UpdateTrust.PublicKey, DateTimeOffset.UtcNow), envelope);
    }
    public async Task DownloadAsync(UpdateRelease release, string destination, IProgress<int>? progress, CancellationToken ct)
    {
        await Task.CompletedTask;
        throw new InvalidOperationException("Updates are disabled.");
    }

    private async Task DownloadAsyncRemoved(UpdateRelease release, string destination, IProgress<int>? progress, CancellationToken ct)
    {
        // The signed browser URL remains the identity; API URLs are constructed here, never trusted from metadata.
        var expected = $"https://github.com/mikehal123/xai-desk/releases/download/v{release.Version}/xAIDesk-Windows-{release.Version}.zip";
        _ = UpdatePackage.ParseVersion(release.Version);
        if (release.Url != expected) throw new IOException("Unexpected signed archive address.");
        var asset = await FindAssetAsync("tags/v" + release.Version, $"xAIDesk-Windows-{release.Version}.zip", release.Url, release.Bytes, ct);
        using var response = await GetAsync(asset, ct, true);
        if (response.Content.Headers.ContentLength is long length && length != release.Bytes) throw new IOException("Unexpected download size.");
        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
        {
            var buffer = new byte[81920]; long total = 0; int n;
            while ((n = await input.ReadAsync(buffer, ct)) > 0)
            {
                total += n; if (total > release.Bytes) throw new IOException("Download exceeded its signed size.");
                await output.WriteAsync(buffer.AsMemory(0, n), ct); progress?.Report((int)(total * 100 / release.Bytes));
            }
        }
        await UpdatePackage.VerifyArchiveAsync(destination, release, ct);
    }
    public void Dispose() => _http.Dispose();
}
