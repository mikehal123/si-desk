using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Sovereign.Desktop.SetupLib;
using Sovereign.Desktop.Updates;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class UpdateRecoveryTests
{
    [Fact]
    public async Task Rollback_retries_the_actual_windows_directory_rename_then_restores_previous_and_preserves_profile()
    {
        using var fixture = new Fixture();
        using var handle = LockDirectory(fixture.Install);
        var release = Task.Run(async () => { await Task.Delay(750); handle.Dispose(); });
        var log = new List<string>();
        await UpdateRecovery.RestoreAsync(fixture.Install, fixture.Previous, fixture.Failed,
            fixture.Profile, fixture.Fingerprint, log, TimeSpan.FromSeconds(5));
        await release;
        Assert.Equal("working previous", File.ReadAllText(Path.Combine(fixture.Install, "app.txt")));
        Assert.Equal("failed candidate", File.ReadAllText(Path.Combine(fixture.Failed, "app.txt")));
        Assert.False(Directory.Exists(fixture.Previous));
        Assert.Equal(fixture.Fingerprint, DeskUpdateSafety.FingerprintClosedProfile(fixture.Profile));
        Assert.Contains(log, entry => entry.Contains("Directory rename succeeded after", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Persistent_candidate_lock_has_a_bounded_failure_and_preserves_both_apps_and_profile()
    {
        using var fixture = new Fixture();
        using var handle = LockDirectory(fixture.Install);
        var watch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<IOException>(() => UpdateRecovery.RestoreAsync(
            fixture.Install, fixture.Previous, fixture.Failed, fixture.Profile, fixture.Fingerprint,
            [], TimeSpan.FromMilliseconds(300)));
        Assert.Contains("Source files retained", error.Message);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal("working previous", File.ReadAllText(Path.Combine(fixture.Previous, "app.txt")));
        Assert.Equal("failed candidate", File.ReadAllText(Path.Combine(fixture.Install, "app.txt")));
        Assert.False(Directory.Exists(fixture.Failed));
        Assert.Equal(fixture.Fingerprint, DeskUpdateSafety.FingerprintClosedProfile(fixture.Profile));
    }

    [Fact]
    public async Task Failure_restoring_a_locked_previous_keeps_previous_and_quarantined_candidate()
    {
        using var fixture = new Fixture();
        using var handle = LockDirectory(fixture.Previous);
        await Assert.ThrowsAsync<IOException>(() => UpdateRecovery.RestoreAsync(
            fixture.Install, fixture.Previous, fixture.Failed, fixture.Profile, fixture.Fingerprint,
            [], TimeSpan.FromMilliseconds(300)));
        Assert.False(Directory.Exists(fixture.Install));
        Assert.Equal("working previous", File.ReadAllText(Path.Combine(fixture.Previous, "app.txt")));
        Assert.Equal("failed candidate", File.ReadAllText(Path.Combine(fixture.Failed, "app.txt")));
        Assert.Equal(fixture.Fingerprint, DeskUpdateSafety.FingerprintClosedProfile(fixture.Profile));
    }

    [Fact]
    public async Task Changed_durable_profile_refuses_rollback_before_moving_either_app()
    {
        using var fixture = new Fixture();
        var database = Path.Combine(fixture.Profile, "desk", "desk.sqlite");
        File.AppendAllText(database, "new writes");
        var latest = File.ReadAllBytes(database);
        var error = await Assert.ThrowsAsync<IOException>(() => UpdateRecovery.RestoreAsync(
            fixture.Install, fixture.Previous, fixture.Failed, fixture.Profile, fixture.Fingerprint, []));
        Assert.Contains("data changed", error.Message);
        Assert.True(Directory.Exists(fixture.Install));
        Assert.True(Directory.Exists(fixture.Previous));
        Assert.False(Directory.Exists(fixture.Failed));
        Assert.Equal(latest, File.ReadAllBytes(database));
    }

    [Fact]
    public async Task Candidate_shutdown_stops_an_observed_orphan_child_but_leaves_an_unrelated_process_running()
    {
        using var fixture = new Fixture();
        var childPidFile = Path.Combine(fixture.Root, "child-pid.txt");
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var sleep = Convert.ToBase64String(Encoding.Unicode.GetBytes("Start-Sleep -Seconds 60"));
        var launchStarted = DateTime.UtcNow;
        using var unrelated = StartPowerShell(powershell, "Start-Sleep -Seconds 60", fixture.Root);
        using var app = StartPowerShell(powershell,
            "$child = Start-Process -FilePath '" + Quote(powershell) + "' -WindowStyle Hidden -PassThru " +
            "-ArgumentList @('-NoProfile','-NonInteractive','-EncodedCommand','" + sleep + "'); " +
            "[System.IO.File]::WriteAllText('" + Quote(childPidFile) + "', [string]$child.Id); Start-Sleep -Seconds 60",
            fixture.Install);
        Process? child = null;
        try
        {
            var shutdown = new UpdateCandidateShutdown(app, fixture.Install, launchStarted);
            var wait = Stopwatch.StartNew();
            while (!File.Exists(childPidFile) && !app.HasExited && wait.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(100);
            Assert.True(File.Exists(childPidFile), "Candidate did not create the disposable child process.");
            child = Process.GetProcessById(int.Parse(File.ReadAllText(childPidFile)));
            shutdown.Observe();
            app.Kill();
            await app.WaitForExitAsync();
            Assert.False(child.HasExited);
            var log = new List<string>();
            await shutdown.StopAsync(log, TimeSpan.FromSeconds(5));
            Assert.True(child.WaitForExit(5000), "Orphan child survived candidate shutdown. " + string.Join(" | ", log));
            Assert.False(unrelated.HasExited);
            Assert.Contains(log, entry => entry.Contains("Stopped update candidate process " + child.Id + ":", StringComparison.Ordinal));
        }
        finally
        {
            Stop(child);
            Stop(app);
            Stop(unrelated);
            child?.Dispose();
        }
    }

    [Fact]
    public async Task Candidate_image_orphan_is_stopped_after_ui_exit_while_existing_host_and_other_install_survive()
    {
        using var fixture = new Fixture();
        var systemCmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var candidateCmd = Path.Combine(fixture.Install, "cmd.exe");
        var sibling = Path.Combine(fixture.Root, "other-install");
        Directory.CreateDirectory(sibling);
        var siblingCmd = Path.Combine(sibling, "cmd.exe");
        File.Copy(systemCmd, candidateCmd);
        File.Copy(systemCmd, siblingCmd);
        using var existingHost = StartCommand(candidateCmd, keepOpen: true);
        await Task.Delay(100);
        var launchStarted = DateTime.UtcNow;
        using var orphan = StartCommand(candidateCmd, keepOpen: true);
        using var unrelated = StartCommand(siblingCmd, keepOpen: true);
        using var app = StartCommand(candidateCmd, keepOpen: false);
        try
        {
            await app.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(orphan.HasExited);
            Assert.False(existingHost.HasExited);
            Assert.False(unrelated.HasExited);
            var shutdown = new UpdateCandidateShutdown(app, fixture.Install, launchStarted);
            await shutdown.StopAsync([], TimeSpan.FromSeconds(5));
            Assert.True(orphan.WaitForExit(5000));
            Assert.False(existingHost.HasExited);
            Assert.False(unrelated.HasExited);
        }
        finally
        {
            Stop(orphan);
            Stop(existingHost);
            Stop(unrelated);
            Stop(app);
        }
    }

    private static Process StartCommand(string executable, bool keepOpen)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        foreach (var argument in keepOpen ? new[] { "/d", "/q", "/k" } : new[] { "/d", "/q", "/c", "exit", "3" })
            start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("Could not start the disposable command process.");
    }

    private static Process StartPowerShell(string executable, string script, string workingDirectory)
    {
        var start = new ProcessStartInfo(executable)
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workingDirectory };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("Could not start the disposable test process.");
    }

    private static string Quote(string value) => value.Replace("'", "''", StringComparison.Ordinal);
    private static void Stop(Process? process)
    {
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(); process.WaitForExit(5000); }
        catch (InvalidOperationException) { }
    }

    private static SafeFileHandle LockDirectory(string directory)
    {
        // FILE_LIST_DIRECTORY + no FILE_SHARE_DELETE makes this a real rename blocker. A zero-access
        // metadata handle does not prevent Directory.Move on Windows, despite the same share flags.
        var handle = CreateFile(directory, 1, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open, 0x02000000, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share,
        IntPtr securityAttributes, FileMode creationDisposition, uint flags, IntPtr template);

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "sovereign-rollback-" + Guid.NewGuid().ToString("N"));
        public string Install => Path.Combine(Root, "app");
        public string Previous => Path.Combine(Root, "previous");
        public string Failed => Path.Combine(Root, "failed");
        public string Profile => Path.Combine(Root, "profile");
        public string Fingerprint { get; }

        public Fixture()
        {
            Directory.CreateDirectory(Install);
            Directory.CreateDirectory(Previous);
            Directory.CreateDirectory(Path.Combine(Profile, "desk"));
            File.WriteAllText(Path.Combine(Install, "app.txt"), "failed candidate");
            File.WriteAllText(Path.Combine(Previous, "app.txt"), "working previous");
            File.WriteAllText(Path.Combine(Profile, "desk", "desk.sqlite"), "retained profile");
            Fingerprint = DeskUpdateSafety.FingerprintClosedProfile(Profile);
        }

        public void Dispose() => Directory.Delete(Root, true);
    }
}
