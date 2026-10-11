using System.Diagnostics;
using System.Text.Json;
using Sovereign.Desktop.Client.DeskRuntime;
using Sovereign.Desktop.Core.Desk;

if (args[0] == "--supervisor") { await SupervisorSmoke.RunAsync(args[1], args[2]); return; }

var root = Path.GetFullPath(args[0]);
var profile = Path.GetFullPath(args[1]);
Directory.CreateDirectory(profile);
var nonce = Guid.NewGuid();
var start = new ProcessStartInfo(Path.Combine(root, "python", "python.exe")) {
    UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
    RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = profile };
start.Environment.Clear();
foreach (var key in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP" })
    if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
foreach (var arg in new[] { "-I", "-B", Path.Combine(root, "desk", "runtime", "worker.py"), "--profile", profile, "--session", nonce.ToString("D") }) start.ArgumentList.Add(arg);
using var process = Process.Start(start)!;
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
var checks = new List<string>();
void Check(bool value, string name) { if (!value) throw new IOException(name); checks.Add(name); }
try
{
    await using var transport = new DeskTransport(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, nonce);
    var reading = transport.Start();
    JsonElement Doc(object item) => JsonSerializer.SerializeToElement(item);
    var result = await transport.CallAsync("initialize", Doc(new { protocol_version = 1, app_version = "T02-QA",
        profile_id = Guid.NewGuid().ToString("D"), locale = "en-US", timezone = "UTC", capabilities = Array.Empty<string>(), session_nonce = nonce.ToString("D") }), timeout.Token);
    var handshake = result.GetProperty("value").Deserialize<InitializeResult>(DeskJson.Options)!;
    Check(handshake.ProtocolVersion == 1 && handshake.SessionNonce == nonce && handshake.Capabilities.Length == 0, "handshake matched; unimplemented capabilities absent");
    var health = await transport.CallAsync("health", Doc(new { session_nonce = nonce.ToString("D") }), timeout.Token);
    Check(health.GetProperty("value").GetProperty("status").GetString() == "degraded", "unregistered services reported degraded");
    var missing = await transport.CallAsync("case.list", Doc(new { query = "", after_case_id = (string?)null, limit = 10, session_nonce = nonce.ToString("D") }), timeout.Token);
    Check(missing.GetProperty("data").GetProperty("code").GetString() == "NOT_CONFIGURED", "missing store never returns fabricated cases");
    var stop = await transport.CallAsync("shutdown", Doc(new { session_nonce = nonce.ToString("D"), operation_id = Guid.NewGuid().ToString("D") }), timeout.Token);
    Check(stop.GetProperty("value").GetProperty("accepted").GetBoolean(), "shutdown acknowledged");
    process.StandardInput.Close();
    await process.WaitForExitAsync(timeout.Token);
    await reading;
    Check(process.ExitCode == 0, "worker exited cleanly");
    Check((await process.StandardError.ReadToEndAsync(timeout.Token)).Length == 0, "no diagnostic leakage");
    Console.WriteLine(JsonSerializer.Serialize(new { passed = checks.Count, checks, profile, production_store = false, production_supervisor = false }));
}
finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
