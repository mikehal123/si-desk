using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sovereign.Desktop.SetupLib;

/// <summary>
/// Stops only processes whose image is this installation's packaged python.exe.
/// Does not kill by process name, and never targets the builder host.
/// </summary>
public static class InstalledHostShutdown
{
    public const int HostShutdownFailedExitCode = 8;
    public static readonly TimeSpan HandleReleaseBudget = TimeSpan.FromSeconds(30);
    private static readonly Regex ProfileRootArgument = new(
        @"--profile-root(?:\s+|=)(?<q>""(?<p>[^""]+)""|(?<p>\S+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static SetupResult? Coordinate(string installDir, string? requestedProfileDir, List<string> log)
    {
        var python = Path.Combine(installDir, "engine-bundle", "python", "python.exe");
        if (!File.Exists(python))
        {
            log.Add("owned-host shutdown skipped: no packaged python.exe in this install");
            return null;
        }

        python = Path.GetFullPath(python);
        var engineRoot = Path.GetFullPath(Path.Combine(installDir, "engine-bundle"));
        var stopScript = Path.Combine(engineRoot, "Stop-SovereignHost.ps1");
        var owned = ListOwnedPids(python);
        if (owned.Count == 0)
        {
            log.Add("owned-host shutdown: no live processes for " + python);
            return WaitUntilUnlocked(installDir, log);
        }

        log.Add("owned-host shutdown: " + owned.Count + " process(es) use this install python");
        var profiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectKnownProfiles(installDir, requestedProfileDir, profiles);
        foreach (var pid in owned)
        {
            var command = TryReadCommandLine(pid);
            if (command is null)
            {
                continue;
            }

            var match = ProfileRootArgument.Match(command);
            if (match.Success)
            {
                profiles.Add(Path.GetFullPath(match.Groups["p"].Value));
            }
        }

        var stopIssued = false;
        foreach (var profile in profiles)
        {
            if (!TryReadEndpointPid(profile, out var pid) || !owned.Contains(pid))
            {
                continue;
            }

            if (!File.Exists(stopScript))
            {
                log.Add("STOP_SCRIPT_MISSING: " + stopScript);
                continue;
            }

            try
            {
                log.Add(RunStop(stopScript, profile));
                stopIssued = true;
            }
            catch (Exception ex)
            {
                log.Add("owned-host stop failed for " + profile + ": " + ex.Message);
            }
        }

        if (!stopIssued)
        {
            return Fail(
                "HOST_SHUTDOWN_FAILED: install-owned python still running (pids " +
                string.Join(",", owned) +
                ") and no identity-checked Stop-SovereignHost could be issued. Repair/upgrade/uninstall refused. Unrelated Python and the builder host were not killed.",
                log);
        }

        var deadline = DateTime.UtcNow + HandleReleaseBudget;
        List<string> locked;
        do
        {
            owned = ListOwnedPids(python);
            locked = LockedInstallFiles(installDir);
            if (owned.Count == 0 && locked.Count == 0)
            {
                log.Add("owned-host shutdown: processes exited and file handles released");
                return null;
            }

            Thread.Sleep(250);
        } while (DateTime.UtcNow < deadline);

        if (owned.Count > 0)
        {
            return Fail(
                "HOST_SHUTDOWN_FAILED: install-owned python still running (pids " +
                string.Join(",", owned) +
                "). Repair/upgrade/uninstall refused. Unrelated Python and the builder host were not killed.",
                log);
        }

        return Fail(
            "HOST_SHUTDOWN_FAILED: install files still locked after owned-host stop: " +
            string.Join(", ", locked.Take(8)) +
            ". Unrelated Python and the builder host were not killed.",
            log);
    }

    public static IReadOnlyList<int> ListOwnedPids(string pythonExe)
    {
        var owned = new List<int>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.HasExited)
                {
                    continue;
                }

                var image = process.MainModule?.FileName;
                if (image is not null
                    && string.Equals(Path.GetFullPath(image), Path.GetFullPath(pythonExe), StringComparison.OrdinalIgnoreCase))
                {
                    owned.Add(process.Id);
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        return owned;
    }

    private static void CollectKnownProfiles(string installDir, string? requestedProfileDir, HashSet<string> profiles)
    {
        AddProfileCandidates(requestedProfileDir, profiles);
        try
        {
            var receiptPath = Path.Combine(installDir, "preview-setup-receipt.json");
            if (File.Exists(receiptPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(receiptPath));
                if (doc.RootElement.TryGetProperty("profile_dir", out var node)
                    && node.ValueKind == JsonValueKind.String
                    && node.GetString() is { } stored)
                {
                    AddProfileCandidates(stored, profiles);
                }
            }
        }
        catch
        {
        }

        AddPreviewChildren(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "xAIDesk"),
            profiles);
    }

    private static void AddPreviewChildren(string root, HashSet<string> profiles)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        AddProfileCandidates(root, profiles);
        try
        {
            foreach (var child in Directory.GetDirectories(root))
            {
                AddProfileCandidates(child, profiles);
            }
        }
        catch
        {
        }
    }

    private static void AddProfileCandidates(string? path, HashSet<string> profiles)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var full = Path.GetFullPath(path);
            profiles.Add(full);
            profiles.Add(Path.Combine(full, "host"));
        }
        catch
        {
        }
    }

    private static bool TryReadEndpointPid(string profileRoot, out int pid)
    {
        pid = 0;
        var path = Path.Combine(profileRoot, "endpoint-v2.json");
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("pid", out var node)
                && node.ValueKind == JsonValueKind.Number
                && node.TryGetInt32(out pid);
        }
        catch
        {
            return false;
        }
    }

    private static string? TryReadCommandLine(int pid)
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("(Get-CimInstance Win32_Process -Filter \"ProcessId=" + pid + "\").CommandLine");
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return string.IsNullOrWhiteSpace(stdout) ? null : stdout.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string RunStop(string script, string profileRoot)
    {
        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("-ProfileRoot");
        start.ArgumentList.Add(profileRoot);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("POWERSHELL_START_FAILED");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("HOST_SCRIPT_EXIT_" + process.ExitCode + " " + stderr + stdout);
        }

        return stdout.Trim();
    }

    private static SetupResult? WaitUntilUnlocked(string installDir, List<string> log)
    {
        var deadline = DateTime.UtcNow + HandleReleaseBudget;
        List<string> locked;
        do
        {
            locked = LockedInstallFiles(installDir);
            if (locked.Count == 0)
            {
                log.Add("owned-host shutdown: install files are not locked");
                return null;
            }

            Thread.Sleep(250);
        } while (DateTime.UtcNow < deadline);

        return Fail(
            "HOST_SHUTDOWN_FAILED: install files still locked after owned-host stop: " +
            string.Join(", ", locked.Take(8)) +
            ". Unrelated Python and the builder host were not killed.",
            log);
    }

    private static List<string> LockedInstallFiles(string installDir)
    {
        var locked = new List<string>();
        foreach (var relative in new[]
                 {
                     Path.Combine("engine-bundle", "python", "python.exe"),
                     Path.Combine("engine-bundle", "python", "libcrypto-3.dll"),
                     Path.Combine("engine-bundle", "python", "DLLs", "libcrypto-3.dll")
                 })
        {
            var path = Path.Combine(installDir, relative);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                locked.Add(path);
            }
            catch (UnauthorizedAccessException)
            {
                locked.Add(path);
            }
        }

        return locked;
    }

    private static SetupResult Fail(string message, List<string> log)
    {
        log.Add(message);
        return new SetupResult(HostShutdownFailedExitCode, message, log);
    }
}
