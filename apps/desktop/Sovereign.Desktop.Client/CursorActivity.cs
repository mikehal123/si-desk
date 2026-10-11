using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Security.Cryptography;

namespace Sovereign.Desktop.Client;

/// <summary>Display-only events. Observers cannot grant tools or change the session mode.</summary>
public sealed record CursorActivityEvent(string Kind, string Text = "", CursorToolActivity? Tool = null, CursorPlanItem[]? Plan = null, bool Merge = false, CursorSubagent? Subagent = null,
    CursorToolSummary? Summary = null, CursorFileChange[]? Changes = null, CursorChangeArchive? ChangeArchive = null)
{
    [JsonIgnore] internal CursorTurnActivity? Continuation { get; init; }
}
public sealed record CursorPlanItem(string Id, string Content, string Status);
public sealed record CursorToolSummary(long Total, long Active, long Completed, long Failed, long Blocked, long Unconfirmed,
    long Compacted, int ChangedFiles, bool ChangeDetailsTruncated = false, int Retained = 0, long Cancelled = 0, long Other = 0,
    string? StorageError = null, bool CountsIncomplete = false);
public sealed record CursorChangeArchive(string Directory, int Count);

/// <summary>One turn's observed activity. Completion is never inferred from tool or reasoning text.</summary>
public sealed class CursorTurnActivity : IDisposable
{
    private readonly string _waitingLabel;
    private readonly Dictionary<string, CursorToolActivity> _tools = [];
    private readonly Dictionary<string, CursorPlanItem> _plan = [];
    private readonly CursorToolRetention _history;
    private readonly string? _trustedRoot;
    private readonly Dictionary<string, CursorSubagent> _children = [];
    private readonly StringBuilder _reasoning = new();
    private bool _reasoningTruncated;
    public IReadOnlyCollection<CursorToolActivity> Tools => _tools.Values;
    public IReadOnlyCollection<CursorPlanItem> Plan => _plan.Values;
    public IReadOnlyCollection<CursorFileChange> Changes => _history.Changes;
    public CursorToolSummary ToolSummary => _history.Summary;
    public long TotalTools => ToolSummary.Total;
    public long ActiveTools => ToolSummary.Active;
    public long CompletedTools => ToolSummary.Completed;
    public long CompactedTools => ToolSummary.Compacted;
    public int ChangedFiles => ToolSummary.ChangedFiles;
    public bool ChangeDetailsTruncated => ToolSummary.ChangeDetailsTruncated;
    public long UnconfirmedTools => ToolSummary.Unconfirmed;
    public long CancelledTools => ToolSummary.Cancelled;
    public CursorChangeArchive? ChangeArchive => _history.ChangeArchive;
    public CursorChangeArchive? CaptureChanges(string directory) => _history.ExportChanges(directory);
    internal IReadOnlyCollection<CursorSubagent> Children => _children.Values;
    public string Reasoning => _reasoning.ToString() + (_reasoningTruncated ? "\n[Reasoning display limit reached]" : "");
    public bool HasDisplayedReasoning => _reasoning.Length > 0 || _reasoningTruncated;
    public bool ReasoningDisplayLimitReached => _reasoningTruncated;
    /// <summary>Resets this display projection only. It does not affect the provider, tools, or archived events.</summary>
    public void ClearReasoningDisplay()
    {
        _reasoning.Clear();
        _reasoningTruncated = false;
    }
    public string Phase { get; private set; }
    public string Result { get; private set; } = "running";
    public TimeSpan LastEventAt { get; private set; }
    public long BlockedTools => ToolSummary.Blocked;
    public string ExecutionNotice
    {
        get
        {
            var summary = ToolSummary;
            var blocked = summary.Blocked > 0
                ? $"{summary.Blocked} tool request(s) blocked and did not run. Any claimed test results from these commands are unverified. Review Activity for details."
                : "";
            var storage = summary.StorageError ?? "";
            var counts = summary.CountsIncomplete ? "Some activity counts may be incomplete." : "";
            return string.Join(" ", new[] { blocked, storage, counts }.Where(text => text.Length > 0));
        }
    }
    public long FailedTools => ToolSummary.Failed;

    public CursorTurnActivity(string waitingLabel = "Waiting for Cursor", string? trustedRoot = null)
    {
        _waitingLabel = waitingLabel; Phase = waitingLabel; _history = new(_tools, 1000); _trustedRoot = trustedRoot;
    }

    public void Apply(CursorActivityEvent update, TimeSpan elapsed)
    {
        if (Result != "running") return;
        LastEventAt = elapsed;
        switch (update.Kind)
        {
            case "subagent" when update.Subagent is { } child:
                _children[child.SessionId] = child;
                // Full live tool events and bounded restored snapshots keep helper edits
                // in Changes without copying helper messages into the parent answer.
                var helperTools = update.Tool is { } liveTool ? new[] { liveTool } : child.Tools ?? [];
                foreach (var helperTool in helperTools)
                {
                    var attributed = helperTool with { Title = child.Name + " · " + helperTool.Title };
                    // Historical previews may clip inputs/output or omit large diffs.
                    // They never supersede a tool already observed in chronological events.
                    var previous = _history.Find(attributed.Id);
                    if (update.Tool is null && previous is not null) continue;
                    if (previous is not null && SameTool(previous, attributed)) continue;
                    // Archive tool events may already have applied this snapshot.
                    Apply(new("tool", Tool: attributed), elapsed);
                }
                break;
            case "reasoning":
                if (string.IsNullOrEmpty(update.Text)) break;
                Phase = "Thinking";
                var remaining = Math.Max(0, 32000 - _reasoning.Length);
                _reasoning.Append(update.Text.AsSpan(0, Math.Min(remaining, update.Text.Length)));
                _reasoningTruncated |= update.Text.Length > remaining;
                break;
            case "message": Phase = "Writing response"; break;
            case "permission": Phase = "Waiting for your approval"; break;
            case "resumed": Phase = _waitingLabel; break;
            case "plan":
                if (!update.Merge) _plan.Clear();
                foreach (var item in update.Plan ?? []) if (_plan.Count < 100 || _plan.ContainsKey(item.Id)) _plan[item.Id] = item;
                Phase = "Updating plan";
                break;
            case "tool" when update.Tool is { } tool:
                // Cursor may report a finished request even when its sandbox refused execution.
                // Only recognize the explicit execution refusal, not text in files/search results.
                if (tool.Kind == "execute" && tool.Status == "completed"
                    && tool.Output.TrimStart().StartsWith("Not in allowlist:", StringComparison.OrdinalIgnoreCase))
                    tool = tool with { Status = "blocked" };
                _history.Store(tool);
                var active = _tools.Values.LastOrDefault(t => t.Status is "pending" or "in_progress");
                Phase = active is not null ? active.Title : tool.Status == "failed" ? "Tool failed · " + tool.Title : _waitingLabel;
                break;
            case "summary" when update.Summary is { } summary:
                _history.Import(summary, update.Changes ?? []);
                if (update.ChangeArchive is { } archive && _trustedRoot is not null) _history.ImportArchive(archive, _trustedRoot);
                break;
        }
    }

    private static bool SameTool(CursorToolActivity left, CursorToolActivity right) =>
        left.Id == right.Id && left.Title == right.Title && left.Kind == right.Kind && left.Status == right.Status
        && left.Input == right.Input && left.Output == right.Output && left.Changes.SequenceEqual(right.Changes);

    public void Finish(string result)
    {
        if (Result != "running") return;
        if (result is not ("completed" or "stopped" or "failed" or "interrupted" or "redirected")) throw new ArgumentException("Invalid turn outcome.", nameof(result));
        Result = result;
        _history.Finish();
        Phase = result switch { "redirected" => "Redirected · completed edits kept", "interrupted" => "Interrupted · partial response", "stopped" => "Stopped · completed edits kept", "failed" => "Response failed · review activity",
            _ => BlockedTools > 0 ? "Finished · command blocked; execution unverified" : FailedTools > 0 ? "Finished · some tools failed" : "Finished" };
    }

    public string CompactStatus(TimeSpan elapsed)
    {
        var active = _tools.Values.LastOrDefault(t => t.Status is "pending" or "in_progress");
        var label = Phase == "Waiting for your approval" ? Phase : active?.Kind switch {
            "read" => "Reading files", "search" => "Searching", "execute" => "Running a command",
            "edit" => "Editing files", "fetch" => "Reading sources", null => Phase.StartsWith("Tool failed", StringComparison.Ordinal) ? "Tool failed · open Activity" : Phase,
            _ => "Working with tools"
        };
        var quiet = elapsed - LastEventAt;
        if (Result == "running" && quiet >= TimeSpan.FromSeconds(12) && Phase != "Waiting for your approval")
            label += " · no new events for " + (int)quiet.TotalSeconds + "s";
        return label + $" · {TotalTools} " + (TotalTools == 1 ? "step" : "steps") + " · " + (elapsed.TotalMinutes >= 1 ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s" : $"{(int)elapsed.TotalSeconds}s");
    }

    public string ComposerFlowStatus(TimeSpan elapsed)
    {
        var clock = elapsed.TotalMinutes >= 1 ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s" : $"{(int)elapsed.TotalSeconds}s";
        var steps = $"{TotalTools} " + (TotalTools == 1 ? "step" : "steps");
        if (Phase == "Waiting for your approval")
            return "Waiting for your approval… " + clock + " · " + steps;
        var active = _tools.Values.LastOrDefault(t => t.Status is "pending" or "in_progress");
        var phase = active is not null ? active.Kind switch {
            "read" => "Reading files", "search" => "Searching", "execute" => "Running a command",
            "edit" => "Editing files", "fetch" => "Reading sources", _ => "Working"
        } : Phase switch {
            "Waiting for provider" or "Waiting for Cursor" or "Waiting for response" => "Waiting for response", "Writing response" => "Responding", "Thinking" => "Thinking",
            _ when Phase.StartsWith("Tool failed", StringComparison.Ordinal) => "Tool failed", _ => Phase
        };
        return phase + "… " + clock + " · " + steps;
    }

    public string Status(TimeSpan elapsed)
    {
        var quiet = elapsed - LastEventAt;
        var detail = Result == "running" && quiet >= TimeSpan.FromSeconds(12) && Phase != "Waiting for your approval"
            ? Phase + " · no new events for " + (int)quiet.TotalSeconds + "s" : Phase;
        return detail + " · " + (elapsed.TotalMinutes >= 1 ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s" : $"{(int)elapsed.TotalSeconds}s");
    }
    public void Dispose() => _history.Dispose();
}

/// <summary>Saved display projection. Old tool details do not grant authority or resume provider sessions.</summary>
public static class CursorActivityRetention
{
    public static CursorActivityEvent[] Compact(IEnumerable<CursorActivityEvent> activity, string? finalOutcome = null, string? artifactDirectory = null)
    {
        var events = activity.ToArray();
        var summaryIndex = Array.FindLastIndex(events, item => item.Kind == "summary" && item.Summary is not null);
        var prior = summaryIndex >= 0 ? events[summaryIndex] : null;
        var state = prior?.Continuation ?? new CursorTurnActivity(trustedRoot: artifactDirectory);
        var start = prior?.Continuation is not null ? summaryIndex + 1 : 0;
        for (var i = start; i < events.Length; i++) state.Apply(events[i], TimeSpan.Zero);
        if (artifactDirectory is not null) state.CaptureChanges(artifactDirectory);
        if (finalOutcome is not null) state.Finish(finalOutcome);
        var retained = new List<CursorActivityEvent>();
        retained.AddRange(events.Where(item => item.Kind is not ("tool" or "subagent" or "summary" or "reasoning")).TakeLast(100));
        if (state.Reasoning.Length > 0) retained.Add(new("reasoning", state.Reasoning));
        retained.AddRange(state.Tools.Select(tool => new CursorActivityEvent("tool", Tool: tool)));
        retained.AddRange(state.Children.Select(child => new CursorActivityEvent("subagent", Subagent: child)));
        retained.Add(new("summary", Summary: state.ToolSummary, Changes: state.Changes.ToArray(), ChangeArchive: state.ChangeArchive) { Continuation = finalOutcome is null ? state : null });
        return retained.ToArray();
    }
    public static IEnumerable<CursorFileChange> AllChanges(CursorChangeArchive archive, string trustedRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trustedRoot));
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(archive.Directory));
        if (archive.Count < 0 || !directory.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The saved change archive is outside the owned activity root.");
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "change-*.json"))
        {
            if (++count > archive.Count) throw new IOException("The saved change archive count is invalid.");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 64 * 1024 * 1024) throw new IOException("Saved file-change details exceed the load limit.");
            var change = JsonSerializer.Deserialize<CursorFileChange>(stream);
            if (change is null || string.IsNullOrEmpty(change.Path) || change.After is null || Path.GetFileName(path) !=
                "change-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(change.Path.ToUpperInvariant()))) + ".json")
                throw new IOException("The saved file-change identity is invalid.");
            yield return change;
        }
        if (count != archive.Count) throw new IOException("Some saved file-change details are missing.");
    }
}
