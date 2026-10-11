using System.Diagnostics;
using System.Text.Json;
using Sovereign.Desktop.Client.DeskRuntime;

internal static class SupervisorSmoke
{
    public static async Task RunAsync(string root, string profile)
    {
        using var inventory = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "qa-inventory.json")));
        var files = inventory.RootElement.EnumerateArray().Select(row => new RuntimeFile(
            row.GetProperty("path").GetString()!, row.GetProperty("sha256").GetString()!, row.GetProperty("bytes").GetInt64())).ToArray();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        var checks = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new IOException(name); checks.Add(name); }
        async Task Reject(RuntimeFile[] bad, string name)
        {
            try { await using var unexpected = await DeskWorkerSession.StartAsync(root, bad, profile, Guid.NewGuid(), deadline.Token); }
            catch (IOException) { checks.Add(name); return; }
            throw new IOException("Accepted " + name);
        }
        await Reject(files.Append(files[0]).ToArray(), "duplicate inventory rejected");
        await Reject(files.Select((f, i) => i == 0 ? f with { Sha256 = new string('0', 64) } : f).ToArray(), "hash mismatch rejected");
        await Reject([new RuntimeFile("../escape.py", new string('0', 64), 0)], "path traversal rejected");
        await Reject(files.Where(f => !f.Path.EndsWith("worker.py", StringComparison.Ordinal)).ToArray(), "missing inventoried entry point rejected");
        await Reject(files.Where(f => !f.Path.EndsWith("profile_lock.py", StringComparison.Ordinal)).ToArray(), "unlisted importable code rejected");
        var first = await DeskWorkerSession.StartAsync(root, files, profile, Guid.NewGuid(), deadline.Token);
        var pid = first.ProcessId;
        try
        {
            var result = await first.Transport.CallAsync("health", JsonSerializer.SerializeToElement(new { session_nonce = first.Nonce.ToString("D") }), deadline.Token);
            Check(result.GetProperty("value").GetProperty("status").GetString() == "degraded", "supervised worker handshake and health");
            try { await using var second = await DeskWorkerSession.StartAsync(root, files, profile, Guid.NewGuid(), deadline.Token); throw new Exception("Second worker accepted."); }
            catch (IOException) { checks.Add("second worker refused for same profile"); }
            Check(!Process.GetProcessById(pid).HasExited, "second-worker failure leaves first worker alive");
        }
        finally { await first.DisposeAsync(); }
        Check(!Alive(pid), "dispose terminates worker");
        await using (var reopened = await DeskWorkerSession.StartAsync(root, files, profile, Guid.NewGuid(), deadline.Token))
            checks.Add("profile lock released for next session");

        var start = new ProcessStartInfo(Path.Combine(root, "python/python.exe")) { UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("-I"); start.ArgumentList.Add("-B"); start.ArgumentList.Add("-c");
        start.ArgumentList.Add("import sys,subprocess,time;sys.stdin.readline();p=subprocess.Popen([sys.executable,'-I','-c','import time;time.sleep(90)']);print(p.pid,flush=True);time.sleep(90)");
        using var parent = Process.Start(start)!;
        using var job = new WorkerJob();
        int childId = 0;
        try
        {
            job.Assign(parent);
            await parent.StandardInput.WriteLineAsync("start"); await parent.StandardInput.FlushAsync();
            childId = int.Parse((await parent.StandardOutput.ReadLineAsync(deadline.Token))!);
            Check(Alive(childId), "descendant running inside assigned job");
            var watch = Stopwatch.StartNew(); job.Dispose();
            await parent.WaitForExitAsync(deadline.Token);
            while (Alive(childId) && watch.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20, deadline.Token);
            Check(!Alive(childId) && watch.Elapsed < TimeSpan.FromSeconds(5), "job close kills worker and descendant within five seconds");
        }
        finally
        {
            job.Dispose();
            if (!parent.HasExited) parent.Kill(true);
            if (childId != 0 && Alive(childId)) Process.GetProcessById(childId).Kill(true);
        }
        Console.WriteLine(JsonSerializer.Serialize(new { passed = checks.Count, checks, production_manifest = false, clean_windows = false }));
    }

    private static bool Alive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
}
