using System.Globalization;

namespace Sovereign.Desktop.Connector;

public enum SwarmRunEventKind { Status, Task, Handoff, Failed, Finished }

/// <summary>One thing that happened on a live swarm run. Summary is one line; Detail carries a handoff block.</summary>
public sealed record SwarmRunEvent(DateTimeOffset At, string RunId, SwarmRunEventKind Kind, string Summary, string? Detail = null)
{
    /// <summary>Routine task moves (todo, doing, review) are recorded but never wake Grok Bot.</summary>
    public bool Wakes => Kind != SwarmRunEventKind.Task;

    public string KindName => Name(Kind);

    public static string Name(SwarmRunEventKind kind) => kind switch
    {
        SwarmRunEventKind.Status => "status",
        SwarmRunEventKind.Task => "task",
        SwarmRunEventKind.Handoff => "handoff",
        SwarmRunEventKind.Failed => "failed",
        _ => "finished"
    };

    /// <summary>UTC time, run id, kind, summary, tab separated. A handoff adds its SOVEREIGN_HANDOFF text on the lines after.</summary>
    public string Line()
    {
        var head = At.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            + "\t" + RunId + "\t" + KindName + "\t" + Summary;
        return string.IsNullOrWhiteSpace(Detail) ? head : head + "\n" + Detail.Trim();
    }
}

/// <summary>
/// Detects swarm run events (pure, static) and keeps a bounded in-memory log per run for swarm_run_events.
/// Nothing here writes the board or any state on disk.
/// </summary>
public sealed class SwarmRunEvents
{
    /// <summary>Events kept per run. Older ones fall off the front.</summary>
    public const int Keep = 200;
    /// <summary>Default and maximum rows one swarm_run_events call returns.</summary>
    public const int Take = 50;
    public const int HandoffClip = 2000;

    private readonly object _gate = new();
    private readonly Dictionary<string, List<SwarmRunEvent>> _runs = new(StringComparer.Ordinal);

    /// <summary>
    /// Task changes between two reads of the same run board. A null <paramref name="previous"/> is the baseline
    /// and yields nothing. A move to failed is a failed event; the run emptying of open work is a finished event.
    /// </summary>
    public static IReadOnlyList<SwarmRunEvent> Diff(SwarmBoard? previous, SwarmBoard current, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        var events = new List<SwarmRunEvent>();
        if (previous is null || !string.Equals(previous.Id, current.Id, StringComparison.Ordinal)) return events;
        var runId = current.Id ?? "";
        foreach (var task in current.Tasks)
        {
            if (task is null || string.IsNullOrWhiteSpace(task.Id)) continue;
            var before = previous.Tasks.FirstOrDefault(item => item is not null && string.Equals(item.Id, task.Id, StringComparison.Ordinal));
            var was = before is null ? null : SwarmSection.StatusLabel(before.Status);
            var label = SwarmSection.StatusLabel(task.Status);
            if (was is not null && string.Equals(was, label, StringComparison.Ordinal)) continue;
            var role = string.IsNullOrWhiteSpace(task.Role) ? "worker" : task.Role.Trim();
            var title = string.IsNullOrWhiteSpace(task.Title) ? task.Id : task.Title.Trim();
            var move = (was ?? "new") + " -> " + label;
            var summary = task.Id + " (" + role + ") " + title + ": " + move;
            if (label == SwarmStatus.Failed)
            {
                var reason = FirstLine(string.IsNullOrWhiteSpace(task.VerifierNote) ? task.Result : task.VerifierNote, 160);
                events.Add(new SwarmRunEvent(now, runId, SwarmRunEventKind.Failed, summary + (reason.Length == 0 ? "" : " · " + reason)));
            }
            else events.Add(new SwarmRunEvent(now, runId, SwarmRunEventKind.Task, summary));
        }
        if (current.Tasks.Count > 0 && SwarmSchedule.HasOpenWork(previous) && !SwarmSchedule.HasOpenWork(current))
        {
            var progress = SwarmSection.Measure(current.Tasks);
            events.Add(new SwarmRunEvent(now, runId, SwarmRunEventKind.Finished,
                "run finished: done " + progress.Done + "/" + progress.Total + ", failed " + progress.Failed));
        }
        return events;
    }

    /// <summary>
    /// Events carried by a reply. A manager reply while the run is live is a status update. A SOVEREIGN_HANDOFF
    /// block in a manager or worker reply is a handoff, with the block text as detail.
    /// </summary>
    public static IReadOnlyList<SwarmRunEvent> FromReply(string runId, string? reply, bool manager, bool live, DateTimeOffset now)
    {
        var events = new List<SwarmRunEvent>();
        if (string.IsNullOrWhiteSpace(reply)) return events;
        var who = manager ? "manager" : "worker";
        if (manager && live)
            events.Add(new SwarmRunEvent(now, runId ?? "", SwarmRunEventKind.Status, "manager update: " + FirstLine(reply, 160)));
        if (SwarmHandoffReview.HandoffText(reply) is { } handoff)
        {
            var clipped = GrokbotWake.Scrub(handoff).Trim();
            if (clipped.Length > HandoffClip) clipped = clipped[..HandoffClip];
            events.Add(new SwarmRunEvent(now, runId ?? "", SwarmRunEventKind.Handoff, who + " handoff ready: " + HandoffLead(handoff), clipped));
        }
        return events;
    }

    public static SwarmRunEvent Stopped(string runId, DateTimeOffset now) =>
        new(now, runId ?? "", SwarmRunEventKind.Finished, "run stopped");

    public void Record(IEnumerable<SwarmRunEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        lock (_gate)
        {
            foreach (var item in events)
            {
                if (item is null || string.IsNullOrWhiteSpace(item.RunId)) continue;
                if (!_runs.TryGetValue(item.RunId, out var list))
                {
                    list = [];
                    _runs[item.RunId] = list;
                }
                list.Add(item);
                while (list.Count > Keep) list.RemoveAt(0);
            }
        }
    }

    public void Record(SwarmRunEvent item) => Record([item]);

    /// <summary>Events newer than <paramref name="since"/> (exclusive), oldest first, at most <paramref name="take"/> of the newest.</summary>
    public IReadOnlyList<SwarmRunEvent> Since(string runId, DateTimeOffset? since, int take = Take)
    {
        if (string.IsNullOrWhiteSpace(runId)) throw new ArgumentException("run_id is required.");
        var limit = Math.Clamp(take, 1, Take);
        lock (_gate)
        {
            if (!_runs.TryGetValue(runId.Trim(), out var list)) return [];
            IEnumerable<SwarmRunEvent> rows = list;
            if (since is DateTimeOffset cutoff) rows = rows.Where(item => item.At > cutoff);
            return rows.TakeLast(limit).ToArray();
        }
    }

    public static DateTimeOffset? ParseSince(string? since)
    {
        if (string.IsNullOrWhiteSpace(since)) return null;
        if (!DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            throw new ArgumentException("since must be an ISO-8601 time.");
        return parsed;
    }

    public static string Text(IEnumerable<SwarmRunEvent> rows) => string.Join("\n", rows.Select(item => item.Line()));

    private static string HandoffLead(string handoff)
    {
        foreach (var raw in handoff.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("SOVEREIGN_HANDOFF", StringComparison.Ordinal) || line.StartsWith("END_SOVEREIGN_HANDOFF", StringComparison.Ordinal)) continue;
            return FirstLine(line, 160);
        }
        return "SOVEREIGN_HANDOFF";
    }

    private static string FirstLine(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = GrokbotWake.Scrub(raw.Trim());
            if (line.Length == 0) continue;
            return line.Length <= max ? line : line[..max];
        }
        return "";
    }
}
