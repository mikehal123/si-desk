using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sovereign.Desktop.Core;
using Sovereign.Desktop.Updates;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class PrivateUpdateTests
{
    private const string DisabledSkip = XaiDeskIdentity.UpdaterEnabled ? null : "Updates are disabled in this build";
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request);
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    [Fact(Skip = DisabledSkip)] public async Task Private_archive_uses_API_bearer_and_strips_it_on_CDN_redirect()
    {
        var bytes = Encoding.UTF8.GetBytes("verified archive bytes");
        var url = "https://github.com/mikehal123/si-desk/releases/download/v0.3.21/SIDesk-Windows-0.3.21.zip";
        var release = new UpdateRelease("0.3.21", url, Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length, "test", DateTimeOffset.UtcNow.AddDays(1));
        var count = 0;
        using var client = new UpdateClient("test-token", new Handler(request =>
        {
            count++;
            if (count == 1)
            {
                Assert.Equal(UpdateClient.RepositoryApi + "/releases/tags/v0.3.21", request.RequestUri!.AbsoluteUri);
                Assert.Equal("test-token", request.Headers.Authorization!.Parameter);
                return Task.FromResult(Json(new { draft = false, prerelease = false, assets = new[] { new { name = "SIDesk-Windows-0.3.21.zip", id = 123, size = bytes.Length, browser_download_url = url } } }));
            }
            if (count == 2)
            {
                Assert.Equal(UpdateClient.RepositoryApi + "/releases/assets/123", request.RequestUri!.AbsoluteUri);
                Assert.Equal("test-token", request.Headers.Authorization!.Parameter);
                Assert.Equal("application/octet-stream", request.Headers.Accept.Single().MediaType);
                var redirect = new HttpResponseMessage(HttpStatusCode.Found); redirect.Headers.Location = new Uri("https://release-assets.githubusercontent.com/download"); return Task.FromResult(redirect);
            }
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }));
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".zip");
        try { await client.DownloadAsync(release, path, null, default); Assert.Equal(bytes, await File.ReadAllBytesAsync(path)); Assert.Equal(3, count); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Theory(Skip = DisabledSkip)]
    [InlineData("https://evil.example/download")]
    [InlineData("http://api.github.com/repos/mikehal123/si-desk/releases/latest")]
    [InlineData("https://api.github.com/repos/someone/private/releases/latest")]
    public async Task Unsafe_redirect_is_rejected_before_sending_token(string location)
    {
        var calls = 0;
        using var client = new UpdateClient("test-token", new Handler(_ =>
        {
            calls++; var response = new HttpResponseMessage(HttpStatusCode.Found); response.Headers.Location = new Uri(location); return Task.FromResult(response);
        }));
        await Assert.ThrowsAsync<IOException>(() => client.CheckAsync(default)); Assert.Equal(1, calls);
    }
    [Fact(Skip = DisabledSkip)] public async Task Missing_sign_in_never_makes_anonymous_private_request()
    {
        using var client = new UpdateClient(null, new Handler(_ => throw new Exception("Network should not be used")));
        await Assert.ThrowsAsync<IOException>(() => client.CheckAsync(default));
    }
    [Fact] public async Task Refresh_uses_device_flow_without_client_secret()
    {
        using var auth = new GitHubSignIn("public-id", new Handler(async request =>
        {
            Assert.Equal("https://github.com/login/oauth/access_token", request.RequestUri!.AbsoluteUri);
            var form = await request.Content!.ReadAsStringAsync();
            Assert.Contains("grant_type=refresh_token", form); Assert.Contains("refresh_token=old-refresh", form);
            Assert.Contains("client_id=public-id", form); Assert.DoesNotContain("client_secret", form);
            return Json(new { access_token = "new-access", refresh_token = "new-refresh", token_type = "bearer", expires_in = 28800 });
        }));
        var result = await auth.RefreshAsync(new("old-access", "old-refresh", DateTimeOffset.UtcNow), default);
        Assert.Equal("new-access", result.AccessToken); Assert.Equal("new-refresh", result.RefreshToken);
        Assert.DoesNotContain("new-access", result.ToString());
    }
    [Fact] public async Task Device_code_checks_verification_destination()
    {
        using var auth = new GitHubSignIn("public-id", new Handler(_ => Task.FromResult(Json(new { verification_uri = "https://evil.example", device_code = "secret", user_code = "ABCD-EFGH", interval = 5, expires_in = 900 }))));
        await Assert.ThrowsAsync<IOException>(() => auth.BeginAsync(default));
    }
    [Fact] public async Task Device_poll_cancels_before_dispatch()
    {
        using var auth = new GitHubSignIn("public-id", new Handler(_ => throw new Exception("No poll expected")));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => auth.CompleteAsync(new("secret", "ABCD-EFGH", 5, DateTimeOffset.UtcNow.AddMinutes(15)), cancel.Token));
    }
}
