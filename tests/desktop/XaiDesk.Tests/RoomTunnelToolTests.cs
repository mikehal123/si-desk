using System.Net;
using System.Security.Cryptography;
using System.Text;
using Sovereign.Desktop.Client;
using Xunit;

namespace XaiDesk.Tests;

public sealed class RoomTunnelToolTests
{
    [Fact]
    public async Task Present_file_with_matching_hash_skips_download()
    {
        var bytes = Encoding.UTF8.GetBytes("present-tunnel-tool");
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var root = TempRoot();
        var noticed = false;
        try
        {
            var path = Path.Combine(root, CloudflaredTool.FileName);
            await File.WriteAllBytesAsync(path, bytes);
            using var handler = new StubHandler(_ => throw new InvalidOperationException("Download ran for a present tool."));

            var resolved = await CloudflaredTool.EnsureAsync(root, handler, hash, () => noticed = true);

            Assert.Equal(path, resolved);
            Assert.Equal(0, handler.Calls);
            Assert.False(noticed);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Download_with_matching_hash_keeps_the_file()
    {
        var bytes = Encoding.UTF8.GetBytes("hashed-tunnel-tool");
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var root = TempRoot();
        var noticed = false;
        try
        {
            using var handler = new StubHandler(request =>
            {
                Assert.Equal(CloudflaredTool.DownloadUrl, request.RequestUri?.AbsoluteUri);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            });

            var resolved = await CloudflaredTool.EnsureAsync(root, handler, hash, () => noticed = true);

            Assert.Equal(1, handler.Calls);
            Assert.True(noticed);
            Assert.Equal(Path.Combine(root, CloudflaredTool.FileName), resolved);
            Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(resolved))), ignoreCase: true);
            Assert.False(File.Exists(Path.Combine(root, "cloudflared.exe.download")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Download_with_bad_hash_deletes_the_file()
    {
        Assert.Equal("86aee4017b26625cee8484c113558f48effa4cd47f7aa05fcf425604e5d2b23c", CloudflaredTool.Sha256Hex);
        var root = TempRoot();
        try
        {
            using var handler = new StubHandler(request =>
            {
                Assert.Equal(CloudflaredTool.DownloadUrl, request.RequestUri?.AbsoluteUri);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("not-the-pinned-tool")) };
            });

            var error = await Assert.ThrowsAsync<IOException>(() => CloudflaredTool.EnsureAsync(root, handler));

            Assert.Equal(1, handler.Calls);
            Assert.Contains("checksum", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(root, CloudflaredTool.FileName)));
            Assert.False(File.Exists(Path.Combine(root, "cloudflared.exe.download")));
            Assert.Empty(Directory.GetFiles(root));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "xAIDesk", "tunnel-tool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }
}
