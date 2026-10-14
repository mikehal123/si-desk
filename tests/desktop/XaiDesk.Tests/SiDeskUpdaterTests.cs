using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Sovereign.Desktop.Updates;
using Xunit;

namespace XaiDesk.Tests;

public sealed class SiDeskUpdaterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sidesk-updater-test-" + Guid.NewGuid().ToString("N"));

    public SiDeskUpdaterTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("v0.3.68", "0.3.67-desk-foundation", true)]
    [InlineData("v0.3.68", "0.3.68-desk-foundation", false)]
    [InlineData("v0.3.68", "0.3.68", false)]
    [InlineData("0.3.67", "0.3.68", false)]
    [InlineData("v0.3.10", "0.3.9", true)]
    [InlineData("v0.4.0", "0.3.99", true)]
    [InlineData("v1.0", "0.9.9", true)]
    [InlineData("v0.3.68.1", "0.3.68", true)]
    [InlineData("v0.3.69-beta", "0.3.68", false)]
    [InlineData("latest", "0.3.68", false)]
    [InlineData("v0.3.69", "dev", false)]
    public void Version_compare(string tag, string running, bool newer) =>
        Assert.Equal(newer, SiDeskVersion.IsNewer(tag, running));

    [Fact]
    public void Newer_release_with_digest_is_available()
    {
        var result = SiDeskReleaseFeed.Evaluate(Feed("v0.3.69"), "0.3.68-desk-foundation");
        Assert.Equal(SiDeskCheckState.Available, result.State);
        Assert.Equal("SI Desk 0.3.69 available", result.Message);
        var release = Assert.IsType<SiDeskRelease>(result.Release);
        Assert.Equal("SIDesk-0.3.69-win-x64.zip", release.AssetName);
        Assert.Equal(new string('a', 64), release.Sha256);
        Assert.Equal(1234, release.Size);
    }

    [Theory]
    [InlineData("v0.3.70", true, false)]
    [InlineData("v0.3.70", false, true)]
    [InlineData("v0.3.70-rc1", false, false)]
    public void Pre_releases_and_drafts_are_skipped(string tag, bool prerelease, bool draft)
    {
        var result = SiDeskReleaseFeed.Evaluate(Feed(tag, prerelease: prerelease, draft: draft), "0.3.68");
        Assert.Equal(SiDeskCheckState.PreRelease, result.State);
        Assert.Null(result.Release);
    }

    [Fact]
    public void Same_or_older_release_is_up_to_date()
    {
        Assert.Equal(SiDeskCheckState.UpToDate, SiDeskReleaseFeed.Evaluate(Feed("v0.3.68"), "0.3.68-desk-foundation").State);
        Assert.Equal(SiDeskCheckState.UpToDate, SiDeskReleaseFeed.Evaluate(Feed("v0.3.67"), "0.3.68").State);
    }

    [Theory]
    [InlineData("SIDesk-0.3.69-win-x64.zip", "", "https://github.com/mikehal123/si-desk/releases/download/v0.3.69/SIDesk-0.3.69-win-x64.zip")]
    [InlineData("SIDesk-0.3.69-win-x64.zip", "md5:abc", "https://github.com/mikehal123/si-desk/releases/download/v0.3.69/SIDesk-0.3.69-win-x64.zip")]
    [InlineData("SIDesk-0.3.68-win-x64.zip", "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "https://github.com/mikehal123/si-desk/releases/download/v0.3.69/SIDesk-0.3.68-win-x64.zip")]
    [InlineData("SIDesk-0.3.69-win-x64.zip", "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "https://example.com/SIDesk-0.3.69-win-x64.zip")]
    public void Release_without_a_usable_zip_and_digest_is_not_offered(string name, string digest, string url)
    {
        var result = SiDeskReleaseFeed.Evaluate(Feed("v0.3.69", assetName: name, digest: digest, url: url), "0.3.68");
        Assert.Equal(SiDeskCheckState.NoAsset, result.State);
        Assert.Null(result.Release);
    }

    [Fact]
    public async Task Check_sends_one_anonymous_get_to_the_latest_release()
    {
        var handler = new FakeHandler(_ => Json(Feed("v0.3.69")));
        using var client = new SiDeskUpdateClient(handler);
        var result = await client.CheckAsync("0.3.68", CancellationToken.None);
        Assert.Equal(SiDeskCheckState.Available, result.State);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(SiDeskReleaseFeed.LatestUrl, request.Uri.ToString());
        Assert.False(request.Headers.ContainsKey("Authorization"));
        Assert.False(request.Headers.ContainsKey("Cookie"));
        Assert.Equal("SI-Desk-Updater/1", request.Headers["User-Agent"]);
    }

    [Fact]
    public async Task Download_with_matching_digest_is_kept()
    {
        var bytes = Encoding.UTF8.GetBytes("zip bytes for the update");
        var release = Release(bytes.Length, Sha(bytes));
        using var client = new SiDeskUpdateClient(new FakeHandler(_ => Bytes(bytes)));
        var path = await client.DownloadAsync(release, Path.Combine(_root, "dl"), null, CancellationToken.None);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Download_with_a_digest_mismatch_is_refused_and_deleted()
    {
        var bytes = Encoding.UTF8.GetBytes("tampered zip bytes");
        var release = Release(bytes.Length, new string('0', 64));
        using var client = new SiDeskUpdateClient(new FakeHandler(_ => Bytes(bytes)));
        var folder = Path.Combine(_root, "dl");
        var error = await Assert.ThrowsAsync<SiDeskUpdateRefusedException>(() => client.DownloadAsync(release, folder, null, CancellationToken.None));
        Assert.Contains("SHA-256", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(folder, release.AssetName)));
        Assert.Throws<SiDeskUpdateRefusedException>(() =>
        {
            var copy = Path.Combine(_root, "copy.zip");
            File.WriteAllBytes(copy, bytes);
            SiDeskUpdateClient.VerifyFile(copy, new string('0', 64));
        });
    }

    [Fact]
    public async Task Download_redirect_to_an_unknown_host_is_refused()
    {
        var handler = new FakeHandler(request =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("https://evil.example.com/SIDesk.zip");
            return redirect;
        });
        using var client = new SiDeskUpdateClient(handler);
        await Assert.ThrowsAsync<IOException>(() => client.DownloadAsync(Release(10, new string('a', 64)), Path.Combine(_root, "dl"), null, CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Fake_update_replaces_only_the_app_folder_and_leaves_profile_and_settings_byte_identical()
    {
        var layout = Layout();
        var before = Snapshot(layout.Guarded);
        var zip = Zip("new-exe", "0.3.69");
        var staged = SiDeskFolderSwap.Stage(zip, layout.App);
        var host = new FakeHost(ready: true);
        var logs = new List<string>();
        var request = new SiDeskHelperRequest(4242, layout.App, staged, "0.3.69", Path.Combine(_root, "work", "ready.txt"), layout.Profile);
        Directory.CreateDirectory(Path.GetDirectoryName(request.ReadyFile)!);

        var code = await SiDeskUpdateHelper.RunAsync(request, host, Protected(layout), TimeSpan.FromSeconds(5), logs.Add);

        Assert.True(code == 0, string.Join("\n", logs));
        Assert.Equal("new-exe", File.ReadAllText(Path.Combine(layout.App, "SIDesk.exe")));
        Assert.False(File.Exists(Path.Combine(layout.App, "old-only.dll")));
        Assert.False(Directory.Exists(SiDeskFolderSwap.BackupPath(layout.App)));
        Assert.False(Directory.Exists(staged));
        Assert.Equal(before, Snapshot(layout.Guarded));
        Assert.Equal([Path.Combine(layout.App, "SIDesk.exe")], host.Launched);
    }

    [Fact]
    public async Task New_app_that_never_reports_ready_is_rolled_back_from_bak()
    {
        var layout = Layout();
        var before = Snapshot(layout.Guarded);
        var oldApp = Snapshot([layout.App]);
        var staged = SiDeskFolderSwap.Stage(Zip("new-exe", "0.3.69"), layout.App);
        var host = new FakeHost(ready: false);
        var logs = new List<string>();
        var request = new SiDeskHelperRequest(4242, layout.App, staged, "0.3.69", Path.Combine(_root, "work", "ready.txt"), layout.Profile);
        Directory.CreateDirectory(Path.GetDirectoryName(request.ReadyFile)!);

        var code = await SiDeskUpdateHelper.RunAsync(request, host, Protected(layout), TimeSpan.FromMilliseconds(300), logs.Add);

        Assert.Equal(1, code);
        Assert.Equal(oldApp, Snapshot([layout.App]));
        Assert.False(Directory.Exists(SiDeskFolderSwap.BackupPath(layout.App)));
        Assert.False(Directory.Exists(layout.App + ".failed"));
        Assert.Equal(before, Snapshot(layout.Guarded));
        Assert.Equal(2, host.Launched.Count);
        Assert.True(host.Killed);
        Assert.Contains(logs, line => line.Contains("Rolled back", StringComparison.Ordinal));
    }

    [Fact]
    public void Swap_keeps_bak_until_confirm_and_rollback_restores_it()
    {
        var layout = Layout();
        var oldApp = Snapshot([layout.App]);
        var staged = SiDeskFolderSwap.Stage(Zip("new-exe", "0.3.69"), layout.App);
        SiDeskFolderSwap.Swap(layout.App, staged);
        Assert.Equal("new-exe", File.ReadAllText(Path.Combine(layout.App, "SIDesk.exe")));
        Assert.True(Directory.Exists(SiDeskFolderSwap.BackupPath(layout.App)));
        SiDeskFolderSwap.Rollback(layout.App);
        Assert.Equal(oldApp, Snapshot([layout.App]));
        Assert.False(Directory.Exists(SiDeskFolderSwap.BackupPath(layout.App)));

        staged = SiDeskFolderSwap.Stage(Zip("new-exe", "0.3.69"), layout.App);
        SiDeskFolderSwap.Swap(layout.App, staged);
        SiDeskFolderSwap.Confirm(layout.App);
        Assert.False(Directory.Exists(SiDeskFolderSwap.BackupPath(layout.App)));
        Assert.Equal("new-exe", File.ReadAllText(Path.Combine(layout.App, "SIDesk.exe")));
    }

    [Fact]
    public void Install_folder_that_holds_profile_data_is_refused()
    {
        var app = Path.Combine(_root, "holder");
        Directory.CreateDirectory(Path.Combine(app, "profile"));
        File.WriteAllText(Path.Combine(app, "SIDesk.exe"), "exe");
        Assert.Throws<SiDeskUpdateRefusedException>(() => SiDeskInstall.ValidateInstall(app, [Path.Combine(app, "profile")]));
        Assert.Throws<SiDeskUpdateRefusedException>(() => SiDeskInstall.ValidateInstall(app, [app]));
        Assert.Throws<SiDeskUpdateRefusedException>(() => SiDeskInstall.ValidateInstall(Path.Combine(_root, "missing"), []));
        Assert.Equal(SiDeskInstall.Normalize(app), SiDeskInstall.ValidateInstall(app, [Path.Combine(_root, "elsewhere")]));
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.Contains(Path.Combine(local, "SIDesk"), SiDeskInstall.ProtectedPaths(null));
        Assert.Contains(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SIDesk"), SiDeskInstall.ProtectedPaths(null));
    }

    [Fact]
    public void Dev_build_is_detected_from_git_source_tree_or_marker()
    {
        var plain = Path.Combine(_root, "plain", "app");
        Directory.CreateDirectory(plain);
        Assert.False(SiDeskInstall.IsDevBuild(plain));

        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var inRepo = Path.Combine(repo, "apps", "bin", "Release");
        Directory.CreateDirectory(inRepo);
        Assert.True(SiDeskInstall.IsDevBuild(inRepo));

        var tree = Path.Combine(_root, "tree");
        Directory.CreateDirectory(Path.Combine(tree, "out"));
        File.WriteAllText(Path.Combine(tree, "Directory.Build.props"), "<Project />");
        Assert.True(SiDeskInstall.IsDevBuild(Path.Combine(tree, "out")));

        var marked = Path.Combine(_root, "marked");
        Directory.CreateDirectory(marked);
        File.WriteAllText(Path.Combine(marked, SiDeskInstall.DevMarker), "");
        Assert.True(SiDeskInstall.IsDevBuild(marked));
    }

    [Fact]
    public void Ready_signal_is_written_only_to_a_temp_ready_file()
    {
        var ready = Path.Combine(_root, "ready.txt");
        Assert.True(SiDeskUpdateHelper.SignalReady("0.3.69-desk-foundation", ready));
        Assert.Equal("0.3.69-desk-foundation", File.ReadAllText(ready));
        Assert.False(SiDeskUpdateHelper.SignalReady("0.3.69", Path.Combine(_root, "other.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "other.txt")));
    }

    [Fact]
    public void Helper_arguments_round_trip()
    {
        var request = new SiDeskHelperRequest(77, @"C:\Apps\SI Desk", @"C:\Apps\SI Desk.new", "0.3.69", @"C:\Temp\x\ready.txt", @"D:\Profiles\SIDesk");
        Assert.Equal(request, SiDeskHelperRequest.Parse(request.ToArgs()));
        Assert.Throws<ArgumentException>(() => SiDeskHelperRequest.Parse(["--pid", "x"]));
    }

    private sealed record Paths(string App, string Profile, string[] Guarded);

    private Paths Layout()
    {
        var app = Path.Combine(_root, "install", "SIDesk");
        Directory.CreateDirectory(Path.Combine(app, "runtimes"));
        File.WriteAllText(Path.Combine(app, "SIDesk.exe"), "old-exe");
        File.WriteAllText(Path.Combine(app, "old-only.dll"), "old dll");
        File.WriteAllText(Path.Combine(app, "runtimes", "native.dll"), "native");
        var profile = Path.Combine(_root, "LocalAppData", "SIDesk");
        Directory.CreateDirectory(Path.Combine(profile, "chats"));
        Directory.CreateDirectory(Path.Combine(profile, "connector"));
        File.WriteAllText(Path.Combine(profile, "settings.json"), "{\"theme\":\"dark\",\"wake\":\"\"}");
        File.WriteAllBytes(Path.Combine(profile, "chats", "chat-1.json"), [1, 2, 3, 0, 255]);
        File.WriteAllText(Path.Combine(profile, "connector", "audit.log"), "2026-10-14\trejected\twrong-token\n");
        var roaming = Path.Combine(_root, "AppData", "SIDesk");
        Directory.CreateDirectory(roaming);
        File.WriteAllText(Path.Combine(roaming, "settings.json"), "{\"roaming\":true}");
        var personal = Path.Combine(_root, "install", "personal");
        Directory.CreateDirectory(personal);
        File.WriteAllText(Path.Combine(personal, "PrivateModule.cs"), "// ignored personal file");
        return new Paths(app, profile, [profile, roaming, personal]);
    }

    private static IReadOnlyList<string> Protected(Paths layout) => [.. layout.Guarded, .. SiDeskInstall.ProtectedPaths(layout.Profile)];

    private string Zip(string exeText, string version)
    {
        var source = Path.Combine(_root, "zip-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(source, "runtimes"));
        File.WriteAllText(Path.Combine(source, "SIDesk.exe"), exeText);
        File.WriteAllText(Path.Combine(source, SiDeskUpdateHelper.ExeName), "helper");
        File.WriteAllText(Path.Combine(source, "runtimes", "native.dll"), "native " + version);
        var zip = Path.Combine(_root, "SIDesk-" + version + "-win-x64-" + Guid.NewGuid().ToString("N") + ".zip");
        ZipFile.CreateFromDirectory(source, zip);
        return zip;
    }

    private static Dictionary<string, string> Snapshot(IEnumerable<string> roots)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                files[file] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))) + ":" + File.GetLastWriteTimeUtc(file).Ticks;
        return files;
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static SiDeskRelease Release(long size, string sha) =>
        new(new Version(0, 3, 69, 0), "v0.3.69", "SIDesk-0.3.69-win-x64.zip",
            new Uri("https://github.com/mikehal123/si-desk/releases/download/v0.3.69/SIDesk-0.3.69-win-x64.zip"), size, sha);

    private static string Feed(string tag, bool prerelease = false, bool draft = false, string? assetName = null, string? digest = null, string? url = null)
    {
        var version = tag.TrimStart('v');
        assetName ??= "SIDesk-" + version + "-win-x64.zip";
        digest ??= "sha256:" + new string('a', 64);
        url ??= "https://github.com/mikehal123/si-desk/releases/download/" + tag + "/" + assetName;
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            tag_name = tag, prerelease, draft,
            assets = new object[]
            {
                new { name = "notes.txt", size = 3, digest = "sha256:" + new string('b', 64), browser_download_url = "https://github.com/x/notes.txt" },
                new { name = assetName, size = 1234, digest, browser_download_url = url }
            }
        });
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Bytes(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentLength = bytes.Length;
        return response;
    }

    private sealed record SeenRequest(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers);

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<SeenRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new SeenRequest(request.Method, request.RequestUri!, request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase)));
            return Task.FromResult(respond(request));
        }
    }

    private sealed class FakeHost(bool ready) : ISiDeskHelperHost
    {
        public List<string> Launched { get; } = [];
        public bool Killed { get; private set; }

        public Task<bool> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken ct) => Task.FromResult(true);

        public ISiDeskAppProcess Launch(string exe, string workingDirectory, string? readyFile)
        {
            Launched.Add(exe);
            if (ready && readyFile is not null) File.WriteAllText(readyFile, "0.3.69-desk-foundation");
            return new FakeProcess(this);
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(20, ct);

        private sealed class FakeProcess(FakeHost owner) : ISiDeskAppProcess
        {
            public bool HasExited => owner.Killed;
            public void Kill() => owner.Killed = true;
            public void Dispose() { }
        }
    }
}
