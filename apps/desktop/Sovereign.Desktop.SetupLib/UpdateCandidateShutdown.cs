using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Sovereign.Desktop.SetupLib;

/// <summary>Tracks only this update's candidate and its children, including orphaned packaged workers.</summary>
public sealed class UpdateCandidateShutdown
{
    private sealed record Identity(int Pid, long StartedUtcTicks, string Image);
    private readonly string _installRoot;
    private readonly long _launchStartedUtcTicks;
    private readonly Dictionary<int, Identity> _tracked = [];
    private readonly int _appPid;

    public UpdateCandidateShutdown(Process app, string installDirectory, DateTime launchStartedUtc)
    {
        _installRoot = Path.GetFullPath(installDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        _launchStartedUtcTicks = launchStartedUtc.ToUniversalTime().Ticks;
        _appPid = app.Id;
        // The original Process.Start object owns the launch handle and creation time. MainModule can
        // still be unreadable immediately after CreateProcess; seed its trusted absolute launch path
        // so children remain attributable even if the candidate crashes before its image is readable.
        if (ReadLaunchedIdentity(app) is { } identity) _tracked[identity.Pid] = identity;
        Observe();
    }

    // Capture while the candidate is alive so a child outside app/ remains attributable after its parent exits.
    public void Observe()
    {
        var parents = ReadParents();
        var live = new Dictionary<int, Identity>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
                if (ReadIdentity(process) is { } identity) live[identity.Pid] = identity;
        }
        foreach (var identity in live.Values)
        {
            // Packaged workers can outlive a UI crash before the first observation. Their exact image path,
            // installation and creation time identify them; other installations and existing hosts are excluded.
            if (identity.StartedUtcTicks >= _launchStartedUtcTicks &&
                identity.Image.StartsWith(_installRoot, StringComparison.OrdinalIgnoreCase))
                _tracked[identity.Pid] = identity;
        }
        bool added;
        do
        {
            added = false;
            foreach (var identity in live.Values)
            {
                if (_tracked.ContainsKey(identity.Pid) || !parents.TryGetValue(identity.Pid, out var parentPid) ||
                    !_tracked.TryGetValue(parentPid, out var parent) || identity.StartedUtcTicks < parent.StartedUtcTicks)
                    continue;
                // If a parent PID was reused, it is no longer evidence of ancestry.
                if (live.TryGetValue(parentPid, out var currentParent) && !SameIdentity(currentParent, parent)) continue;
                _tracked[identity.Pid] = identity;
                added = true;
            }
        } while (added);
    }

    public async Task StopAsync(List<string> log, TimeSpan? timeout = null)
    {
        var budget = timeout ?? TimeSpan.FromSeconds(15);
        var watch = Stopwatch.StartNew();
        Observe();
        if (_tracked.TryGetValue(_appPid, out var appIdentity))
        {
            using var app = OpenSameProcess(appIdentity);
            if (app is not null)
            {
                try
                {
                    if (app.CloseMainWindow()) await app.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1));
                }
                catch (TimeoutException) { }
                catch (InvalidOperationException) when (app.HasExited) { }
            }
        }
        do
        {
            Observe();
            var remaining = new List<int>();
            foreach (var identity in _tracked.Values.OrderBy(item => item.Pid == _appPid ? 0 : 1))
            {
                using var process = OpenSameProcess(identity);
                if (process is null) continue;
                try
                {
                    // Every target was verified by PID, creation time and image. Do not kill by name or an
                    // unchecked PID tree: that can target a different instance or a reused child PID.
                    process.Kill();
                    log.Add($"Stopped update candidate process {identity.Pid}: {identity.Image}");
                }
                catch (InvalidOperationException) when (process.HasExited) { }
                catch (Exception error) { log.Add($"Candidate stop {identity.Pid}: {error.Message}"); }
                if (!process.HasExited) remaining.Add(identity.Pid);
            }
            await Task.Delay(100);
            Observe();
            foreach (var identity in _tracked.Values)
            {
                using var process = OpenSameProcess(identity);
                if (process is not null && !remaining.Contains(identity.Pid)) remaining.Add(identity.Pid);
            }
            if (remaining.Count == 0)
            {
                log.Add("Update candidate and tracked workers exited.");
                return;
            }
            if (watch.Elapsed >= budget)
                throw new IOException("Update candidate processes have not exited (pids " + string.Join(",", remaining) + "). Previous app retained.");
        } while (true);
    }

    private static Identity? ReadIdentity(Process process)
    {
        try
        {
            if (process.HasExited || process.MainModule?.FileName is not { } image) return null;
            return new Identity(process.Id, process.StartTime.ToUniversalTime().Ticks, Path.GetFullPath(image));
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        { return null; }
    }

    private static Identity? ReadLaunchedIdentity(Process process)
    {
        try
        {
            var image = process.StartInfo.FileName;
            if (!Path.IsPathFullyQualified(image)) return ReadIdentity(process);
            return new Identity(process.Id, process.StartTime.ToUniversalTime().Ticks, Path.GetFullPath(image));
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        { return ReadIdentity(process); }
    }

    private static bool SameIdentity(Identity? current, Identity expected) => current is not null &&
        current.Pid == expected.Pid && current.StartedUtcTicks == expected.StartedUtcTicks &&
        string.Equals(current.Image, expected.Image, StringComparison.OrdinalIgnoreCase);

    private static Process? OpenSameProcess(Identity identity)
    {
        try
        {
            var process = Process.GetProcessById(identity.Pid);
            if (SameIdentity(ReadIdentity(process), identity)) return process;
            process.Dispose();
        }
        catch (ArgumentException) { }
        return null;
    }

    private static Dictionary<int, int> ReadParents()
    {
        var result = new Dictionary<int, int>();
        using var snapshot = CreateToolhelp32Snapshot(2, 0); // TH32CS_SNAPPROCESS
        if (snapshot.IsInvalid) return result;
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), ExeFile = "" };
        if (!Process32First(snapshot, ref entry)) return result;
        do { result[(int)entry.ProcessId] = (int)entry.ParentProcessId; }
        while (Process32Next(snapshot, ref entry));
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
}
