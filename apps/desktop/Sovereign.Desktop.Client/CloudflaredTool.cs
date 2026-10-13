using System.Net;
using System.Security.Cryptography;

namespace Sovereign.Desktop.Client;

/// <summary>Pinned Windows amd64 cloudflared. The published URL and checksum are public release data.</summary>
public static class CloudflaredTool
{
    public const string ReleaseTag = "2026.10.0";
    public const string DownloadUrl = "https://github.com/cloudflare/cloudflared/releases/download/2026.10.0/cloudflared-windows-amd64.exe";
    public const string Sha256Hex = "86aee4017b26625cee8484c113558f48effa4cd47f7aa05fcf425604e5d2b23c";
    public const string FileName = "cloudflared.exe";
    public const long MaximumBytes = 96L * 1024 * 1024;

    private const string PartialName = "cloudflared.exe.download";

    public static string DefaultDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "xai-desk", "tools");

    public static string ExecutablePath(string? toolsDirectory = null) =>
        Path.Combine(DirectoryOf(toolsDirectory), FileName);

    public static async Task<string> EnsureAsync(
        string? toolsDirectory = null,
        HttpMessageHandler? handler = null,
        string? expectedSha256 = null,
        Action? downloading = null,
        CancellationToken ct = default)
    {
        var expected = string.IsNullOrWhiteSpace(expectedSha256) ? Sha256Hex : expectedSha256.Trim();
        var directory = DirectoryOf(toolsDirectory);
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, FileName);
        if (File.Exists(destination) && await HashMatchesAsync(destination, expected, ct))
            return destination;
        if (File.Exists(destination))
            File.Delete(destination);

        var partial = Path.Combine(directory, PartialName);
        var accepted = false;
        try
        {
            downloading?.Invoke();
            await DownloadAsync(partial, handler, ct);
            if (!await HashMatchesAsync(partial, expected, ct))
                throw new IOException("The tunnel tool file did not match the expected checksum and was removed.");
            File.Move(partial, destination, overwrite: true);
            accepted = true;
            return destination;
        }
        catch (IOException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new IOException("Could not download the tunnel tool. Check your internet connection.");
        }
        finally
        {
            if (!accepted && File.Exists(partial))
                File.Delete(partial);
        }
    }

    private static string DirectoryOf(string? toolsDirectory) =>
        string.IsNullOrWhiteSpace(toolsDirectory) ? DefaultDirectory() : toolsDirectory;

    private static async Task DownloadAsync(string destination, HttpMessageHandler? handler, CancellationToken ct)
    {
        using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("xAIDesk");
        using var response = await client.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new IOException("Could not download the tunnel tool.");
        if (response.Content.Headers.ContentLength is > MaximumBytes)
            throw new IOException("The tunnel tool download is larger than expected.");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, ct);
            if (read == 0) break;
            total += read;
            if (total > MaximumBytes) throw new IOException("The tunnel tool download is larger than expected.");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        if (total == 0) throw new IOException("The tunnel tool download was empty.");
    }

    private static async Task<bool> HashMatchesAsync(string path, string expected, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
        return actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
}
