using System.Text;
using System.Text.Json;
using Sovereign.Desktop.Client;

var root = Path.GetFullPath(args[0]);
Directory.CreateDirectory(root);
var modeName = args.Skip(1).FirstOrDefault(a => a.StartsWith("--mode="))?[7..] ?? "plan";
if (modeName is not ("plan" or "ask" or "allow")) throw new ArgumentException("Use --mode=plan, ask or allow.");
var edit = args.Contains("--edit");
var codex = args.Contains("--codex");
var profile = args.Skip(1).FirstOrDefault(a => a.StartsWith("--subscription-profile="))?[23..];
if (edit && modeName == "plan") throw new ArgumentException("The edit proof requires ask or allow.");
var fixture = Path.Combine(root, "helper-check.md");
const string text = "# Helper check\nThe test marker is INDIGO-582. Return that marker.\n";
File.WriteAllText(fixture, text);
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var cancelOnSpawn = args.Contains("--cancel");
var cancellationObserved = false;
var children = new Dictionary<string, CursorSubagent>();
var response = new StringBuilder();
var activity = new List<CursorActivityEvent>();
var permissionRequests = new List<object>();
var options = new CursorProjectOptions {
    Mode = modeName == "plan" ? CursorProjectMode.Plan : CursorProjectMode.Agent,
    Permissions = modeName == "allow" ? CursorPermissionPolicy.AlwaysAllow : CursorPermissionPolicy.AlwaysAsk,
    EnableSubagents = true,
    RequestPermission = (p, _) => {
        var approve = p.Tool.Kind is "read" or "search" || edit && p.Tool.Kind == "edit"
            && (p.Tool.Input.Contains("helper-check.md", StringComparison.OrdinalIgnoreCase)
                || p.Tool.Changes.All(c => c.Path.Equals(fixture, StringComparison.OrdinalIgnoreCase)) && p.Tool.Changes.Length > 0);
        permissionRequests.Add(new { tool = p.Tool.Title, kind = p.Tool.Kind, approved = approve });
        return Task.FromResult(approve ? p.Options.FirstOrDefault(o => o.Kind == "allow_once")?.Id : null);
    }
};
void Observe(CursorActivityEvent e) { activity.Add(e); if (e.Subagent is { } child) { children[child.SessionId] = child; if (cancelOnSpawn && child.State == "running") timeout.Cancel(); } }
using var cursor = codex ? null : new CursorChatClient(root, options, Observe);
using var gpt = codex ? new CodexChatClient(root, options, Observe, profile) : null;
var watch = System.Diagnostics.Stopwatch.StartNew();
string? failure = null;
string? modelId = null;
int? ownPid = null;
try {
    if (codex) {
        if (!await gpt!.IsSubscriptionAsync(timeout.Token)) throw new IOException("ChatGPT subscription sign-in required.");
        modelId = (await gpt.ModelsAsync(timeout.Token)).FirstOrDefault(m => m == "gpt-6.1-sol") ?? throw new IOException("GPT-6.1 Sol unavailable.");
    }
    else modelId = (await cursor!.ModelsAsync(timeout.Token)).FirstOrDefault(m => m.Id.StartsWith("grok-4.7[", StringComparison.Ordinal) && m.Id.Contains("fast=true"))?.Id
        ?? throw new IOException("Grok 4.7 unavailable.");
    var clientType = codex ? typeof(CodexChatClient) : typeof(CursorChatClient);
    var ownProcess = (System.Diagnostics.Process?)clientType.GetField("_process", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(codex ? gpt : cursor);
    ownPid = ownProcess!.Id;
    var task = edit
        ? "read helper-check.md, then replace its entire contents with the single line INDIGO-582-EDITED (a trailing newline is allowed) using a file edit tool and return that marker"
        : "read helper-check.md and return its marker without editing anything";
    var prompt = "Test native subagent support: delegate exactly one " + (codex ? "" : "generalPurpose ") + "subagent to " + task
        + ". Do not perform the task yourself, invoke other helpers, or edit any other file. "
        + (codex ? "The helper may use read-only file commands to inspect only helper-check.md. Other shell commands are forbidden. " : "Do not run shell commands. ")
        + "If the helper fails, report its exact failure without retrying. After its result arrives, reply with the marker and stop.";
    if (codex) await gpt!.SendAsync(modelId, prompt, s => response.Append(s), timeout.Token);
    else await cursor!.SendAsync(modelId, prompt, s => response.Append(s), timeout.Token);
}
catch (OperationCanceledException) when (cancelOnSpawn && children.Count > 0) { cancellationObserved = true; }
catch (Exception ex) { failure = ex.Message; }
var stopReason = cursor?.LastStopReason;
cursor?.Dispose(); gpt?.Dispose();
bool processExited = ownPid is null;
if (ownPid is { } pid) {
    try { using var process = System.Diagnostics.Process.GetProcessById(pid); processExited = process.HasExited; }
    catch (ArgumentException) { processExited = true; }
}
var result = new { provider = codex ? "codex" : "cursor", mode = modeName, edit, failure, cancellationObserved, processExited, elapsed_seconds = watch.Elapsed.TotalSeconds,
    model = modelId, child_count = children.Count, children = children.Values,
    fixture_unchanged = File.ReadAllText(fixture) == text,
    edit_received = File.ReadAllText(fixture).Trim() == "INDIGO-582-EDITED",
    marker_received = response.ToString().Contains("INDIGO-582"), stop_reason = stopReason,
    permissionRequests };
Directory.CreateDirectory(Path.Combine(root, "evidence"));
File.WriteAllText(Path.Combine(root, "evidence", "live.json"), JsonSerializer.Serialize(new { result, activity, response = response.ToString() }));
Console.WriteLine(JsonSerializer.Serialize(result));
if (cancelOnSpawn ? !cancellationObserved || !processExited || !result.fixture_unchanged
    : failure is not null || children.Count != 1 || children.Values.Single().State != "completed"
        || !(edit ? result.edit_received : result.fixture_unchanged) || !result.marker_received) Environment.ExitCode = 2;
