using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sovereign.Desktop.Updates;

/// <summary>Numeric SI Desk versions. A tag with any suffix (for example -beta) does not parse and is never offered.</summary>
public static class SiDeskVersion
{
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
        var parts = value.Split('.');
        if (parts.Length is < 2 or > 4) return false;
        var numbers = new int[4];
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length is 0 or > 9 || !parts[i].All(char.IsAsciiDigit)) return false;
            numbers[i] = int.Parse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture);
        }
        version = new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
        return true;
    }

    /// <summary>The running app reports versions such as 0.3.68-desk-foundation. Only the leading number counts.</summary>
    public static bool TryParseRunning(string? informational, out Version version) =>
        TryParse((informational ?? "").Split('-', '+')[0], out version);

    public static bool IsNewer(string? tag, string? running) =>
        TryParse(tag, out var offered) && TryParseRunning(running, out var current) && offered > current;

    public static string Display(Version version) =>
        version.Revision > 0 ? version.ToString(4) : version.ToString(3);
}

public sealed record SiDeskRelease(Version Version, string Tag, string AssetName, Uri AssetUrl, long Size, string Sha256)
{
    public string DisplayVersion => SiDeskVersion.Display(Version);
}

public enum SiDeskCheckState { UpToDate, Available, PreRelease, NoAsset }

public sealed record SiDeskCheckResult(SiDeskCheckState State, SiDeskRelease? Release, string Message);

/// <summary>Reads the GitHub "latest release" JSON. No network here.</summary>
public static class SiDeskReleaseFeed
{
    public const string LatestUrl = "https://api.github.com/repos/mikehal123/si-desk/releases/latest";
    private static readonly Regex AssetPattern = new(@"^SIDesk-(\d+(?:\.\d+){1,3})-win-x64\.zip$", RegexOptions.CultureInvariant);

    public static SiDeskCheckResult Evaluate(string json, string runningVersion)
    {
        if (!SiDeskVersion.TryParseRunning(runningVersion, out var running))
            throw new FormatException("The running SI Desk version is not a number.");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var tag = root.TryGetProperty("tag_name", out var tagElement) && tagElement.ValueKind == JsonValueKind.String ? tagElement.GetString() ?? "" : "";
        if (Flag(root, "prerelease") || Flag(root, "draft") || !SiDeskVersion.TryParse(tag, out var offered))
            return new(SiDeskCheckState.PreRelease, null, "The latest GitHub release (" + (tag.Length == 0 ? "no tag" : tag) + ") is a pre-release. Skipped.");
        if (offered <= running)
            return new(SiDeskCheckState.UpToDate, null, "SI Desk " + SiDeskVersion.Display(running) + " is up to date.");
        var display = SiDeskVersion.Display(offered);
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = Text(asset, "name");
                var match = AssetPattern.Match(name);
                if (!match.Success || !SiDeskVersion.TryParse(match.Groups[1].Value, out var assetVersion) || assetVersion != offered) continue;
                var digest = Text(asset, "digest");
                if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) continue;
                var sha = digest["sha256:".Length..].ToLowerInvariant();
                if (sha.Length != 64 || !sha.All(char.IsAsciiHexDigit)) continue;
                if (!Uri.TryCreate(Text(asset, "browser_download_url"), UriKind.Absolute, out var url) || !SiDeskUpdateClient.AllowedHost(url)) continue;
                var size = asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var bytes) ? bytes : 0;
                if (size <= 0) continue;
                return new(SiDeskCheckState.Available, new SiDeskRelease(offered, tag, name, url, size, sha), "SI Desk " + display + " available");
            }
        }
        return new(SiDeskCheckState.NoAsset, null,
            "SI Desk " + display + " is on GitHub, but its release has no SIDesk-" + display + "-win-x64.zip with a SHA-256 digest. Download it by hand.");
    }

    private static bool Flag(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}

public sealed class SiDeskUpdateRefusedException(string message) : IOException(message);

/// <summary>
/// The only network the updater uses: one unauthenticated GET of the latest release, and the asset download.
/// No cookies, no tokens, no app or user details beyond a fixed User-Agent that GitHub requires.
/// </summary>
public sealed class SiDeskUpdateClient : IDisposable
{
    public const string UserAgent = "SI-Desk-Updater";
    private readonly HttpClient _http;

    public SiDeskUpdateClient(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false })
        { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static bool AllowedHost(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.Host is "api.github.com" or "github.com" or "objects.githubusercontent.com" or "release-assets.githubusercontent.com";

    public async Task<SiDeskCheckResult> CheckAsync(string runningVersion, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await GetAsync(new Uri(SiDeskReleaseFeed.LatestUrl), "application/vnd.github+json", timeout.Token).ConfigureAwait(false);
        var json = await ReadBoundedAsync(response, 2_000_000, timeout.Token).ConfigureAwait(false);
        return SiDeskReleaseFeed.Evaluate(json, runningVersion);
    }

    /// <summary>Downloads the release zip into <paramref name="directory"/> and refuses it unless its SHA-256 matches GitHub's digest.</summary>
    public async Task<string> DownloadAsync(SiDeskRelease release, string directory, IProgress<int>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Path.GetFileName(release.AssetName));
        try
        {
            using var response = await GetAsync(release.AssetUrl, "application/octet-stream", ct).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is long length && length != release.Size)
                throw new SiDeskUpdateRefusedException("The download size does not match the GitHub release. Nothing was installed.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > release.Size) throw new SiDeskUpdateRefusedException("The download is larger than the GitHub release says. Nothing was installed.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    progress?.Report((int)(total * 100 / release.Size));
                }
                if (total != release.Size) throw new SiDeskUpdateRefusedException("The download ended early. Nothing was installed.");
            }
            RequireDigest(Convert.ToHexStringLower(hash.GetHashAndReset()), release.Sha256);
            return path;
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    /// <summary>Hashes a file on disk and refuses it when it does not match <paramref name="expectedSha256"/>.</summary>
    public static void VerifyFile(string path, string expectedSha256)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        RequireDigest(Convert.ToHexStringLower(SHA256.HashData(stream)), expectedSha256);
    }

    private static void RequireDigest(string actual, string expected)
    {
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new SiDeskUpdateRefusedException("The download does not match GitHub's SHA-256 digest. It was deleted and nothing was installed.");
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, string accept, CancellationToken ct)
    {
        for (var hop = 0; hop < 6; hop++)
        {
            if (!AllowedHost(uri)) throw new IOException("The update server redirected to an unexpected address.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue(UserAgent, "1"));
            request.Headers.Accept.ParseAdd(accept);
            if (uri.Host == "api.github.com") request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                uri = new Uri(uri, location);
                response.Dispose();
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                response.Dispose();
                throw new IOException("GitHub answered " + status + ". Try again later.");
            }
            return response;
        }
        throw new IOException("Too many update redirects.");
    }

    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, int limit, CancellationToken ct)
    {
        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit) throw new IOException("The release information is too large.");
            buffer.Write(chunk, 0, read);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Where SI Desk runs from, and which folders an update must never move.</summary>
public static class SiDeskInstall
{
    public const string ExeName = "SIDesk.exe";
    /// <summary>A file with this name in the app folder turns self-update off for that copy.</summary>
    public const string DevMarker = "SIDesk.dev-build";

    /// <summary>True when the app folder has the dev marker, or it or a parent holds a .git folder or a source tree.</summary>
    public static bool IsDevBuild(string appDirectory)
    {
        var start = new DirectoryInfo(Path.GetFullPath(appDirectory));
        if (File.Exists(Path.Combine(start.FullName, DevMarker))) return true;
        for (var directory = start; directory is not null; directory = directory.Parent)
        {
            try
            {
                var path = directory.FullName;
                if (Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git"))) return true;
                if (File.Exists(Path.Combine(path, "Directory.Build.props")) || File.Exists(Path.Combine(path, "global.json"))) return true;
                if (directory.EnumerateFiles("*.sln").Any() || directory.EnumerateFiles("*.slnx").Any() || directory.EnumerateFiles("*.csproj").Any()) return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return false;
    }

    /// <summary>Profile, chats, settings and swarm folders, current and legacy. An update may not move any of them.</summary>
    public static IReadOnlyList<string> ProtectedPaths(string? profileDirectory)
    {
        var paths = new List<string>();
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) })
        {
            if (string.IsNullOrEmpty(root)) continue;
            paths.Add(root);
            foreach (var name in new[] { "SIDesk", "xAIDesk", "xai-desk" }) paths.Add(Path.Combine(root, name));
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home)) paths.Add(home);
        if (!string.IsNullOrWhiteSpace(profileDirectory)) paths.Add(profileDirectory);
        return paths;
    }

    /// <summary>Refuses an install folder that is a drive root, a link, lacks SIDesk.exe, or equals or contains a protected folder.</summary>
    public static string ValidateInstall(string installDirectory, IEnumerable<string> protectedPaths)
    {
        var install = Normalize(installDirectory);
        if (Path.GetPathRoot(install) is { } root && string.Equals(Normalize(root), install, StringComparison.OrdinalIgnoreCase))
            throw new SiDeskUpdateRefusedException("SI Desk is running from a drive root. Update it by hand.");
        var info = new DirectoryInfo(install);
        if (!info.Exists || !File.Exists(Path.Combine(install, ExeName)))
            throw new SiDeskUpdateRefusedException("The app folder does not contain " + ExeName + ". Nothing was changed.");
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new SiDeskUpdateRefusedException("The app folder is a link. Update it by hand.");
        foreach (var item in protectedPaths)
        {
            if (string.IsNullOrWhiteSpace(item)) continue;
            var guarded = Normalize(item);
            if (string.Equals(install, guarded, StringComparison.OrdinalIgnoreCase) ||
                guarded.StartsWith(install + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new SiDeskUpdateRefusedException("The app folder holds your SI Desk data, so it cannot be swapped. Update it by hand.");
        }
        return install;
    }

    public static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

/// <summary>Replaces only the app folder: stage next to it, keep the old one as &lt;folder&gt;.bak until the new app starts.</summary>
public static class SiDeskFolderSwap
{
    public static string BackupPath(string install) => SiDeskInstall.Normalize(install) + ".bak";
    public static string StagedPath(string install) => SiDeskInstall.Normalize(install) + ".new";

    public static string Stage(string zipPath, string install)
    {
        var staged = StagedPath(install);
        if (Directory.Exists(staged)) Directory.Delete(staged, true);
        Directory.CreateDirectory(staged);
        try
        {
            ZipFile.ExtractToDirectory(zipPath, staged);
            if (!File.Exists(Path.Combine(staged, SiDeskInstall.ExeName)))
                throw new SiDeskUpdateRefusedException("The update zip has no " + SiDeskInstall.ExeName + " at its top level. Nothing was changed.");
            return staged;
        }
        catch
        {
            try { Directory.Delete(staged, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    public static void Swap(string install, string staged, Action<string>? log = null, TimeSpan? wait = null)
    {
        install = SiDeskInstall.Normalize(install);
        staged = SiDeskInstall.Normalize(staged);
        if (!string.Equals(staged, StagedPath(install), StringComparison.OrdinalIgnoreCase))
            throw new SiDeskUpdateRefusedException("The staged update is not next to the app folder.");
        if (!File.Exists(Path.Combine(staged, SiDeskInstall.ExeName)))
            throw new SiDeskUpdateRefusedException("The staged update has no " + SiDeskInstall.ExeName + ".");
        var backup = BackupPath(install);
        if (Directory.Exists(backup))
        {
            log?.Invoke("Removing an older backup " + backup);
            Directory.Delete(backup, true);
        }
        Move(install, backup, wait, log);
        try { Move(staged, install, wait, log); }
        catch
        {
            log?.Invoke("Could not place the new app. Putting the old one back.");
            Move(backup, install, wait, log);
            throw;
        }
    }

    /// <summary>Puts &lt;folder&gt;.bak back as the app folder. The failed new copy is deleted.</summary>
    public static void Rollback(string install, Action<string>? log = null, TimeSpan? wait = null)
    {
        install = SiDeskInstall.Normalize(install);
        var backup = BackupPath(install);
        if (!Directory.Exists(backup)) throw new IOException("There is no " + backup + " to roll back to.");
        var failed = install + ".failed";
        if (Directory.Exists(install))
        {
            if (Directory.Exists(failed)) Directory.Delete(failed, true);
            Move(install, failed, wait, log);
        }
        Move(backup, install, wait, log);
        try { if (Directory.Exists(failed)) Directory.Delete(failed, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log?.Invoke("Left " + failed + ": " + ex.Message); }
    }

    /// <summary>The new app started. The old copy is no longer needed.</summary>
    public static void Confirm(string install)
    {
        var backup = BackupPath(install);
        if (Directory.Exists(backup)) Directory.Delete(backup, true);
    }

    private static void Move(string from, string to, TimeSpan? wait, Action<string>? log)
    {
        var deadline = DateTime.UtcNow + (wait ?? TimeSpan.FromSeconds(30));
        while (true)
        {
            try
            {
                Directory.Move(from, to);
                log?.Invoke("Moved " + from + " -> " + to);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && DateTime.UtcNow < deadline && Directory.Exists(from) && !Directory.Exists(to))
            {
                Thread.Sleep(250);
            }
        }
    }
}

public sealed record SiDeskHelperRequest(int ProcessId, string InstallDirectory, string StagedDirectory, string Version, string ReadyFile, string? ProfileDirectory)
{
    public string[] ToArgs() =>
        ["--pid", ProcessId.ToString(CultureInfo.InvariantCulture), "--install", InstallDirectory, "--staged", StagedDirectory,
         "--version", Version, "--ready", ReadyFile, "--profile", ProfileDirectory ?? ""];

    public static SiDeskHelperRequest Parse(IReadOnlyList<string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < args.Count; i += 2) values[args[i]] = args[i + 1];
        string Need(string key) => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException("Missing " + key + ".");
        if (!int.TryParse(Need("--pid"), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) throw new ArgumentException("--pid must be a number.");
        return new(pid, Need("--install"), Need("--staged"), Need("--version"), Need("--ready"),
            values.TryGetValue("--profile", out var profile) && !string.IsNullOrWhiteSpace(profile) ? profile : null);
    }
}

public interface ISiDeskAppProcess : IDisposable
{
    bool HasExited { get; }
    void Kill();
}

public interface ISiDeskHelperHost
{
    /// <summary>True when the process is gone within <paramref name="timeout"/>.</summary>
    Task<bool> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken ct);
    ISiDeskAppProcess Launch(string exe, string workingDirectory, string? readyFile);
    Task DelayAsync(TimeSpan delay, CancellationToken ct);
}

/// <summary>Runs in the small helper after SI Desk asked to update: wait, swap, start, confirm or roll back.</summary>
public static class SiDeskUpdateHelper
{
    public const string ExeName = "SIDesk.UpdateHelper.exe";
    public const string ReadyVariable = "SIDESK_UPDATE_READY";

    public static async Task<int> RunAsync(SiDeskHelperRequest request, ISiDeskHelperHost host, IReadOnlyList<string> protectedPaths,
        TimeSpan readyTimeout, Action<string> log, CancellationToken ct = default)
    {
        string install;
        try
        {
            install = SiDeskInstall.ValidateInstall(request.InstallDirectory, protectedPaths);
            if (!string.Equals(SiDeskInstall.Normalize(request.StagedDirectory), SiDeskFolderSwap.StagedPath(install), StringComparison.OrdinalIgnoreCase))
                throw new SiDeskUpdateRefusedException("The staged update is not next to the app folder.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            log("Refused: " + ex.Message);
            return 2;
        }
        log("Waiting for SI Desk (process " + request.ProcessId + ") to exit.");
        if (!await host.WaitForExitAsync(request.ProcessId, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false))
        {
            log("SI Desk did not exit. Nothing was changed.");
            return 3;
        }
        try { SiDeskFolderSwap.Swap(install, request.StagedDirectory, log); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log("Swap failed, old app kept: " + ex.Message);
            Relaunch(host, install, log);
            return 4;
        }
        var exe = Path.Combine(install, SiDeskInstall.ExeName);
        TryDelete(request.ReadyFile);
        ISiDeskAppProcess? started = null;
        try
        {
            started = host.Launch(exe, install, request.ReadyFile);
            var deadline = DateTime.UtcNow + readyTimeout;
            while (DateTime.UtcNow < deadline && !started.HasExited && !Ready(request.ReadyFile, request.Version))
                await host.DelayAsync(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
            if (Ready(request.ReadyFile, request.Version) && !started.HasExited)
            {
                SiDeskFolderSwap.Confirm(install);
                log("SI Desk " + request.Version + " started. Removed the backup.");
                return 0;
            }
            log(started.HasExited ? "The new SI Desk exited before it was ready." : "The new SI Desk did not report ready in time.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            log("Starting the new SI Desk failed: " + ex.Message);
        }
        try
        {
            if (started is { HasExited: false }) started.Kill();
            started?.Dispose();
            SiDeskFolderSwap.Rollback(install, log);
            log("Rolled back to the previous SI Desk.");
            Relaunch(host, install, log);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            log("Rollback stopped: " + ex.Message + " The previous app is at " + SiDeskFolderSwap.BackupPath(install));
        }
        return 1;
    }

    /// <summary>Called by the new app once its window is up. Writes the version into the ready file the helper is waiting for.</summary>
    public static bool SignalReady(string version, string? readyFile = null)
    {
        var path = readyFile ?? Environment.GetEnvironmentVariable(ReadyVariable);
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var full = Path.GetFullPath(path);
            var temp = Path.GetFullPath(Path.GetTempPath());
            if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(full) != "ready.txt") return false;
            File.WriteAllText(full, version);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private static bool Ready(string readyFile, string version)
    {
        try
        {
            if (!File.Exists(readyFile)) return false;
            var text = File.ReadAllText(readyFile).Trim();
            return SiDeskVersion.TryParseRunning(text, out var reported) && SiDeskVersion.TryParse(version, out var expected) && reported == expected;
        }
        catch (IOException) { return false; }
    }

    private static void Relaunch(ISiDeskHelperHost host, string install, Action<string> log)
    {
        try { host.Launch(Path.Combine(install, SiDeskInstall.ExeName), install, null).Dispose(); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { log("Could not reopen SI Desk: " + ex.Message); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Real processes for the helper.</summary>
public sealed class SiDeskProcessHost : ISiDeskHelperHost
{
    public async Task<bool> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken ct)
    {
        Process process;
        try { process = Process.GetProcessById(processId); }
        catch (ArgumentException) { return true; }
        using (process)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            try { await process.WaitForExitAsync(limit.Token).ConfigureAwait(false); return true; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return process.HasExited; }
        }
    }

    public ISiDeskAppProcess Launch(string exe, string workingDirectory, string? readyFile)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = workingDirectory };
        if (readyFile is not null) start.Environment[SiDeskUpdateHelper.ReadyVariable] = readyFile;
        return new Running(Process.Start(start) ?? throw new InvalidOperationException("SI Desk did not start."));
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);

    private sealed class Running(Process process) : ISiDeskAppProcess
    {
        public bool HasExited => process.HasExited;
        public void Kill() { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
        public void Dispose() => process.Dispose();
    }
}
