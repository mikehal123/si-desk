using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Sovereign.Desktop.Client;

public sealed class RoomTunnel : IDisposable
{
    private Process? _process;
    public async Task<Uri> StartAsync(string install, string profile, Uri local, CancellationToken ct)
    {
        if (!local.IsLoopback || local.Scheme != "http") throw new ArgumentException("Tunnel origin must be loopback.");
        var executable = Path.Combine(install, "room-tunnel", "cloudflared.exe");
        FileStream file;
        try { file = File.OpenRead(executable); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) {
            throw new IOException("Online rooms need the room tunnel (cloudflared.exe), which is missing from this install. Reinstall or update SI Desk.", ex);
        }
        await using (file) {
            // Pinned cloudflared 2026.9.1 windows-amd64. See apps/desktop/room-tunnel/cloudflared.pin.
            if (!Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).Equals("2837888cc0f5d58f15b6dc478376de90b4d3ba5241c7947455d1e0a0df429712", StringComparison.OrdinalIgnoreCase)) throw new IOException("Room tunnel hash mismatch. Repair the app.");
        }
        var home = Path.Combine(profile, "shared-rooms", "tunnel-home"); Directory.CreateDirectory(home);
        var config = Path.Combine(home, "empty.yml"); await File.WriteAllTextAsync(config, "{}\n", ct);
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = home };
        info.Environment.Clear();
        foreach (var name in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP" }) if (Environment.GetEnvironmentVariable(name) is { } value) info.Environment[name] = value;
        info.Environment["USERPROFILE"] = home; info.Environment["HOME"] = home;
        foreach (var arg in new[] { "tunnel", "--config", config, "--no-autoupdate", "--url", local.ToString(), "--protocol", "http2", "--metrics", "127.0.0.1:0" }) info.ArgumentList.Add(arg);
        var process = Process.Start(info) ?? throw new IOException("Could not start room relay."); _process = process;
        var ready = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Drain(StreamReader reader) {
            try { while (await reader.ReadLineAsync() is { } line) { var match = Regex.Match(line, @"https://[a-z0-9-]+\.trycloudflare\.com"); if (match.Success) ready.TrySetResult(new Uri(match.Value)); } }
            catch (IOException) { }
        }
        _ = Drain(process.StandardOutput); _ = Drain(process.StandardError);
        _ = Task.Run(async () => { try { await process.WaitForExitAsync(); ready.TrySetException(new IOException("Room relay stopped. Check your internet connection.")); } catch (InvalidOperationException) { } });
        try { return await ready.Task.WaitAsync(TimeSpan.FromSeconds(60), ct); }
        catch { Dispose(); throw; }
    }
    public void Dispose() { var p = _process; _process = null; try { if (p is { HasExited: false }) { p.Kill(true); p.WaitForExit(5000); } } catch (InvalidOperationException) { } finally { p?.Dispose(); } }
}
