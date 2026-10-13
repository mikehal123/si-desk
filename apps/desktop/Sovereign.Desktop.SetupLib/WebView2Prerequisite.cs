using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Win32;

namespace Sovereign.Desktop.SetupLib;

public interface IWebView2PrerequisitePlatform
{
    string? InstalledVersion();
    void DownloadBootstrapper(string destination);
    bool HasValidMicrosoftSignature(string path);
    int InstallBootstrapper(string path);
}

/// <summary>The documented Evergreen bootstrap flow. No elevation is requested.</summary>
public static class WebView2Prerequisite
{
    public const int FailedExitCode = 8;
    public const string DownloadPage = "https://developer.microsoft.com/microsoft-edge/webview2/";

    public static bool IsInstalledVersion(string? value) =>
        Version.TryParse(value, out var version) && version > new Version(0, 0, 0, 0);

    public static SetupResult? Ensure(IWebView2PrerequisitePlatform platform, List<string> log, Action<string>? progress = null)
    {
        void Report(string message) { log.Add(message); progress?.Invoke(message); }
        string? temporary = null;
        try
        {
            var version = platform.InstalledVersion();
            if (IsInstalledVersion(version))
            {
                Report("Microsoft Edge WebView2 Runtime is installed: " + version);
                return null;
            }
            Report("Microsoft Edge WebView2 Runtime is required. Setup will download Microsoft's signed Evergreen bootstrapper and install the runtime. Internet access is required; Windows may apply your organization's installation policy.");
            temporary = Path.Combine(Path.GetTempPath(), "xAI Desk-WebView2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            var installer = Path.Combine(temporary, "MicrosoftEdgeWebview2Setup.exe");
            platform.DownloadBootstrapper(installer);
            if (!platform.HasValidMicrosoftSignature(installer))
                throw new InvalidOperationException("The download does not have a valid Microsoft Corporation signature; it was not run.");
            var exit = platform.InstallBootstrapper(installer);
            version = platform.InstalledVersion();
            if (exit != 0 && exit != 3010)
                throw new InvalidOperationException("Microsoft's WebView2 installer returned exit code " + exit + ".");
            if (!IsInstalledVersion(version))
                throw new InvalidOperationException("The WebView2 Runtime was not detected after installation. Restart Windows if the installer requested it, then run xAI Desk setup again.");
            Report("Microsoft Edge WebView2 Runtime is ready: " + version);
            return null;
        }
        catch (Exception ex)
        {
            var message = "WEBVIEW2_UNAVAILABLE: " + ex.Message + " App files have not been replaced. Install the Evergreen Runtime from " + DownloadPage + " and retry setup.";
            Report(message);
            return new SetupResult(FailedExitCode, message, log);
        }
        finally
        {
            if (temporary is not null)
            {
                try { Directory.Delete(temporary, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}

public sealed class WindowsWebView2Prerequisite : IWebView2PrerequisitePlatform
{
    public const string BootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
    private const string ClientKey = @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
    public const long MaximumBootstrapperBytes = 8 * 1024 * 1024;

    public string? InstalledVersion()
    {
        // Microsoft's documented per-user and 32-bit-registry per-machine locations.
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            using var root = RegistryKey.OpenBaseKey(hive, hive == RegistryHive.LocalMachine ? RegistryView.Registry32 : RegistryView.Default);
            using var key = root.OpenSubKey(ClientKey, writable: false);
            if (key?.GetValue("pv") is string version && WebView2Prerequisite.IsInstalledVersion(version)) return version;
        }
        return null;
    }

    public void DownloadBootstrapper(string destination)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var address = new Uri(BootstrapperUrl);
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            if (!IsMicrosoftDownloadUri(address)) throw new InvalidOperationException("The WebView2 download redirected outside Microsoft's HTTPS download service.");
            using var response = client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, deadline.Token).GetAwaiter().GetResult();
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                address = response.Headers.Location is Uri next ? new Uri(address, next) : throw new InvalidOperationException("The download redirect has no destination.");
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumBootstrapperBytes)
                throw new InvalidOperationException("The WebView2 bootstrapper exceeds the download limit.");
            using var input = response.Content.ReadAsStream(deadline.Token);
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[64 * 1024];
            long total = 0;
            while (true)
            {
                var count = input.ReadAsync(buffer, deadline.Token).AsTask().GetAwaiter().GetResult();
                if (count == 0) break;
                total += count;
                if (total > MaximumBootstrapperBytes) throw new InvalidOperationException("The WebView2 bootstrapper exceeds the download limit.");
                output.Write(buffer, 0, count);
            }
            if (total == 0) throw new InvalidOperationException("The WebView2 download was empty.");
            return;
        }
        throw new InvalidOperationException("Too many WebView2 download redirects.");
    }

    public static bool IsMicrosoftDownloadUri(Uri value) => value.Scheme == Uri.UriSchemeHttps &&
        value.IsDefaultPort && string.IsNullOrEmpty(value.UserInfo) &&
        (value.IdnHost.Equals("microsoft.com", StringComparison.OrdinalIgnoreCase) ||
         value.IdnHost.EndsWith(".microsoft.com", StringComparison.OrdinalIgnoreCase));

    public bool HasValidMicrosoftSignature(string path)
    {
        // Windows Authenticode validates the complete file, certificate chain and timestamp.
        // ArgumentList and encoded script avoid shell expansion of the temporary path.
        var literal = path.Replace("'", "''", StringComparison.Ordinal);
        var command = "$ErrorActionPreference='Stop'; $s=Get-AuthenticodeSignature -LiteralPath '" + literal +
            "'; if($s.Status -eq 'Valid' -and $s.SignerCertificate.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName,$false) -eq 'Microsoft Corporation'){exit 0}else{exit 1}";
        var info = Hidden(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"));
        // Do not inherit PowerShell 7's module search path into Windows PowerShell.
        info.Environment["PSModulePath"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\Modules");
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) }) info.ArgumentList.Add(argument);
        return RunBounded(info, 30_000) == 0;
    }

    public int InstallBootstrapper(string path)
    {
        var info = Hidden(path);
        info.ArgumentList.Add("/silent");
        info.ArgumentList.Add("/install");
        return RunBounded(info, 240_000);
    }

    private static ProcessStartInfo Hidden(string path) => new(path)
    {
        UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
    };

    private static int RunBounded(ProcessStartInfo info, int milliseconds)
    {
        using var process = Process.Start(info) ?? throw new InvalidOperationException("The WebView2 prerequisite helper could not start.");
        if (process.WaitForExit(milliseconds)) return process.ExitCode;
        try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        throw new TimeoutException("The WebView2 prerequisite helper timed out.");
    }
}
