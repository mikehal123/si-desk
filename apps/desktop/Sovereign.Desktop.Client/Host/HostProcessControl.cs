using System.Diagnostics;

namespace Sovereign.Desktop.Client.Host;

/// <summary>
/// Starts and stops only a desktop-owned staged host via the published bundle scripts.
/// </summary>
public static class HostProcessControl
{
    public static string Start(string bundleRoot, string profileRoot, string profileId)
    {
        var script = Path.Combine(bundleRoot, "Start-SovereignHost.ps1");
        if (!File.Exists(script))
        {
            throw new InvalidOperationException("START_SCRIPT_MISSING: " + script);
        }

        return RunPowerShell(script, "-ProfileRoot", profileRoot, "-ProfileId", profileId);
    }

    public static string Stop(string bundleRoot, string profileRoot)
    {
        var python = Path.GetFullPath(Path.Combine(bundleRoot, "python", "python.exe"));
        var endpoint = HostDiscovery.TryRead(profileRoot, out _);
        if (endpoint?.Pid is int pid)
        {
            if (!InstalledHostSupervisor.TryGetProcessImage(pid, out var image) || image is null
                || !InstalledHostSupervisor.PathsEqual(image, python))
            {
                throw new InvalidOperationException(
                    "HOST_PROCESS_IDENTITY_MISMATCH: refusing to stop pid " + pid + " because it is not the installed private python.exe.");
            }
        }

        var script = Path.Combine(bundleRoot, "Stop-SovereignHost.ps1");
        if (!File.Exists(script))
        {
            throw new InvalidOperationException("STOP_SCRIPT_MISSING: " + script);
        }

        return RunPowerShell(script, "-ProfileRoot", profileRoot);
    }

    private static string RunPowerShell(string script, params string[] args)
    {
        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            ArgumentList =
            {
                "-NoProfile",
                "-ExecutionPolicy", "Bypass",
                "-File", script
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("POWERSHELL_START_FAILED");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("HOST_SCRIPT_EXIT_" + process.ExitCode + " " + stderr + stdout);
        }

        return stdout;
    }
}
