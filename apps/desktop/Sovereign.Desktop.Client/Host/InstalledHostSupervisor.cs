using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Sovereign.Desktop.Core;

namespace Sovereign.Desktop.Client.Host;

/// <summary>
/// Starts the installed private Python host for one user profile.
/// Reuses a live owned process; never starts a second copy for the same profile;
/// never stops a process whose image path is not the installed python.exe.
/// </summary>
public static class InstalledHostSupervisor
{
    public const string DefaultProfileId = "desktop";
    public const string ProfileIdFileName = "desktop-host-id.txt";
    private static readonly Regex ProfileIdPattern = new("^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant);

    public static string ProfileIdFor(string hostProfileDirectory, string? preferred = null)
    {
        Directory.CreateDirectory(hostProfileDirectory);
        var stored = Path.Combine(hostProfileDirectory, ProfileIdFileName);
        if (IsDistinctProfileId(preferred))
        {
            File.WriteAllText(stored, preferred!.Trim());
            return preferred.Trim();
        }

        if (File.Exists(stored))
        {
            var existing = File.ReadAllText(stored).Trim();
            if (IsDistinctProfileId(existing))
            {
                return existing;
            }
        }

        var derived = DeriveProfileId(hostProfileDirectory);
        File.WriteAllText(stored, derived);
        return derived;
    }

    public static string ExpectedPipeName(string profileId) =>
        @"\\.\pipe\SovereignAI.Offline." + profileId + ".v2";

    public static string DeriveProfileId(string hostProfileDirectory)
    {
        var material = Path.GetFullPath(hostProfileDirectory).TrimEnd('\\').ToUpperInvariant();
        var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        return "p" + hex[..15];
    }

    public static bool IsDistinctProfileId(string? profileId) =>
        !string.IsNullOrWhiteSpace(profileId)
        && ProfileIdPattern.IsMatch(profileId)
        && !string.Equals(profileId, DefaultProfileId, StringComparison.Ordinal);

    public static HostEndpoint EnsureRunning(InstalledEngine engine, string hostProfileDirectory, string profileId)
    {
        if (EngineBundleVerifier.IsForbiddenEngineRoot(engine.EngineRoot, out var staging))
        {
            throw new InvalidOperationException(staging + " The desktop will not start a development engine.");
        }

        if (!HostDiscovery.IsAuthorizedProfile(hostProfileDirectory, out var profileWhy))
        {
            throw new InvalidOperationException(profileWhy);
        }

        Directory.CreateDirectory(hostProfileDirectory);
        var mutexName = MutexName(engine.EngineRoot, hostProfileDirectory);
        using var mutex = new Mutex(false, mutexName);
        if (!mutex.WaitOne(TimeSpan.FromSeconds(45)))
        {
            throw new InvalidOperationException("HOST_START_LOCK_TIMEOUT: another desktop instance is starting this host.");
        }

        try
        {
            if (TryOwnedLiveEndpoint(engine, hostProfileDirectory, profileId) is { } live)
            {
                return live;
            }

            HostProcessControl.Start(engine.EngineRoot, hostProfileDirectory, profileId);
            var endpoint = WaitForOwnedEndpoint(engine, hostProfileDirectory, profileId, TimeSpan.FromSeconds(30));
            if (endpoint is null)
            {
                throw new InvalidOperationException(
                    "HOST_ENDPOINT_TIMEOUT: installed python started but endpoint-v2.json was not published for this profile. Preview fixtures are not substituted.");
            }

            return endpoint;
        }
        finally
        {
            try
            {
                mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
        }
    }

    public static HostEndpoint? TryOwnedLiveEndpoint(InstalledEngine engine, string hostProfileDirectory, string profileId)
    {
        var endpoint = HostDiscovery.TryRead(hostProfileDirectory, out _, profileId);
        if (endpoint is null || endpoint.Pid is null)
        {
            return null;
        }

        if (!TryGetProcessImage(endpoint.Pid.Value, out var image) || image is null)
        {
            return null;
        }

        if (!PathsEqual(image, engine.PythonExe))
        {
            return null;
        }

        return endpoint;
    }

    public static bool TryGetProcessImage(int pid, out string? image)
    {
        image = null;
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return false;
            }

            image = process.MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(image);
        }
        catch
        {
            return false;
        }
    }

    public static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static HostEndpoint? WaitForOwnedEndpoint(
        InstalledEngine engine,
        string hostProfileDirectory,
        string profileId,
        TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            if (TryOwnedLiveEndpoint(engine, hostProfileDirectory, profileId) is { } endpoint)
            {
                return endpoint;
            }

            Thread.Sleep(50);
        }

        return null;
    }

    private static string MutexName(string engineRoot, string profileRoot)
    {
        var material = Path.GetFullPath(engineRoot).ToUpperInvariant() + "|" + Path.GetFullPath(profileRoot).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(material)))[..16];
        return @"Local\SIDesk.host." + hash;
    }
}
