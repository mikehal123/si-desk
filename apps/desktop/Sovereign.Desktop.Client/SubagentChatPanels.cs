namespace Sovereign.Desktop.Client;

/// <summary>Which helper chats are open beside the main conversation. Display only; it does not start or steer a runtime session.</summary>
public static class SubagentChatPanels
{
    public const int SideBySide = 1;
    public const int MaxOpen = 16;

    public readonly record struct Arrangement(string[] Visible, string[] Tabs);
    public readonly record struct Crumb(string? Id, string Label);
    public readonly record struct StreamPatch(string? ReasoningAppend, string? AnswerAppend, CursorToolActivity[] Tools, string[] ChangePaths, bool Reset, bool Terminal, string Outcome);

    /// <summary>Missing preference keeps the dock on. An explicit false restores inline cards.</summary>
    public static bool Enabled(bool? stored) => stored ?? true;

    /// <summary>This runtime can steer the parent session only, not one helper.</summary>
    public static bool FollowUpEnabled => false;

    public static bool Running(string? state) => state is "running" or "pending" or "queued" or "waiting";

    /// <summary>A helper that is still working starts open. A finished one starts as a header only.</summary>
    public static bool StartsExpanded(string? state) => Running(state);

    public static bool IsToggleKey(string? key) =>
        key is not null && (key.Equals("Enter", StringComparison.OrdinalIgnoreCase) || key.Equals("Space", StringComparison.OrdinalIgnoreCase));

    public static bool StopAvailable(bool readOnly, string? state) => !readOnly && Running(state);

    public static string[] Open(IReadOnlyList<string>? open, string sessionId)
    {
        var ids = List(open);
        if (CleanId(sessionId) is not { } id) return ids.ToArray();
        ids.Remove(id);
        if (ids.Count >= MaxOpen) ids.RemoveAt(0);
        ids.Add(id);
        return ids.ToArray();
    }

    public static string[] Close(IReadOnlyList<string>? open, string sessionId) =>
        List(open).Where(id => id != CleanId(sessionId)).ToArray();

    /// <summary>Main chat, then each ancestor helper, then the selected helper. A null id is the main chat.</summary>
    public static Crumb[] Breadcrumb(string? selectedId, IReadOnlyDictionary<string, string> names)
    {
        var crumbs = new List<Crumb> { new(null, "Root") };
        if (string.IsNullOrWhiteSpace(selectedId) || !names.ContainsKey(selectedId)) return crumbs.ToArray();
        var chain = new List<string>();
        var current = selectedId;
        for (var guard = 0; guard < MaxOpen && names.ContainsKey(current); guard++)
        {
            chain.Add(current);
            var parent = ParentHelperId(current, names);
            if (parent is null) break;
            current = parent;
        }
        chain.Reverse();
        foreach (var id in chain) crumbs.Add(new(id, names[id]));
        return crumbs.ToArray();
    }

    public static string? ParentHelperId(string id, IReadOnlyDictionary<string, string> names)
    {
        var slash = id.LastIndexOf('/');
        while (slash > 0)
        {
            var parent = id[..slash];
            if (names.ContainsKey(parent)) return parent;
            slash = parent.LastIndexOf('/');
        }
        return null;
    }

    /// <summary>The most recently opened helper fills the side area. Earlier helpers stay on a tab strip.</summary>
    public static Arrangement Arrange(IReadOnlyList<string>? open)
    {
        var ids = List(open).ToArray();
        if (ids.Length <= SideBySide) return new(ids, []);
        return new(ids[^SideBySide..], ids);
    }

    public static string[]? SanitizeIds(IReadOnlyList<string>? ids)
    {
        if (ids is null) return null;
        var clean = List(ids);
        return clean.Count == 0 ? null : clean.ToArray();
    }

    public static StreamPatch Diff(CursorSubagent? previous, CursorSubagent next)
    {
        var tools = Tools(next);
        // A finished activity view ignores later patches, including confirmed results after a disconnect.
        var reset = previous is not null && (
            !next.Reasoning.StartsWith(previous.Reasoning, StringComparison.Ordinal) ||
            !next.Output.StartsWith(previous.Output, StringComparison.Ordinal) ||
            previous.Task != next.Task ||
            !Running(previous.State) && (previous.State != next.State ||
                previous.Reasoning != next.Reasoning || previous.Output != next.Output ||
                !SameTools(Tools(previous), tools)));
        var reasoning = reset || previous is null ? EmptyToNull(next.Reasoning) : Suffix(previous.Reasoning, next.Reasoning);
        var answer = reset || previous is null ? EmptyToNull(next.Output) : Suffix(previous.Output, next.Output);
        var changes = (next.Changes ?? []).Select(change => change.Path).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct().ToArray();
        return new(reasoning, answer, tools, changes, reset, !Running(next.State), ActivityOutcome(next.State));
    }

    public static string StatusText(string? state) => state switch
    {
        "running" or "pending" or "queued" or "waiting" => "Working",
        "completed" => "Finished",
        "failed" => "Failed",
        "cancelled" or "stopped" => "Stopped",
        "stop requested" => "Stop requested",
        "disconnected" or "interrupted" or "unconfirmed" => "Interrupted",
        "blocked" => "Blocked",
        _ => "Status unknown"
    };

    public static string Life(string? state) => state switch
    {
        "running" or "pending" or "queued" or "waiting" => "running",
        "failed" => "failed",
        _ => "idle"
    };

    public static string ActivityOutcome(string? state) => state switch
    {
        "completed" => "completed",
        "failed" => "failed",
        "cancelled" or "stopped" => "stopped",
        _ => "interrupted"
    };

    public static string Elapsed(CursorSubagent child)
    {
        var end = child.CompletedAt ?? (Running(child.State) ? DateTimeOffset.UtcNow : child.UpdatedAt);
        if (child.StartedAt is not { } started || end is null) return "Not recorded";
        var duration = end.Value - started;
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        return duration.TotalHours >= 1 ? $"{(int)duration.TotalHours}h {duration.Minutes}m"
            : duration.TotalMinutes >= 1 ? $"{(int)duration.TotalMinutes}m {duration.Seconds}s" : $"{duration.Seconds}s";
    }

    private static List<string> List(IReadOnlyList<string>? ids)
    {
        var clean = new List<string>();
        if (ids is null) return clean;
        foreach (var id in ids)
        {
            if (CleanId(id) is not { } value) continue;
            if (clean.Contains(value)) continue;
            clean.Add(value);
            if (clean.Count == MaxOpen) break;
        }
        return clean;
    }

    private static string? CleanId(string? id)
    {
        var value = id?.Trim();
        return string.IsNullOrEmpty(value) || value.Length > 200 || value.Any(char.IsControl) ? null : value;
    }

    private static CursorToolActivity[] Tools(CursorSubagent child) =>
        child.Tools ?? (child.Tool is { } single ? [single] : []);

    private static bool SameTools(CursorToolActivity[] left, CursorToolActivity[] right) =>
        left.Length == right.Length && left.Zip(right).All(pair =>
            pair.First.Id == pair.Second.Id && pair.First.Title == pair.Second.Title &&
            pair.First.Kind == pair.Second.Kind && pair.First.Status == pair.Second.Status &&
            pair.First.Input == pair.Second.Input && pair.First.Output == pair.Second.Output &&
            pair.First.Diagnostics == pair.Second.Diagnostics &&
            (pair.First.Changes ?? []).SequenceEqual(pair.Second.Changes ?? []));

    private static string? Suffix(string previous, string next)
    {
        if (next.Length <= previous.Length) return null;
        return next[previous.Length..];
    }

    private static string? EmptyToNull(string text) => text.Length == 0 ? null : text;

    /// <summary>Real helper output and failure already stored on the children. Empty records are omitted.</summary>
    public static string ReportedForPrompt(IEnumerable<CursorSubagent> helpers)
    {
        var blocks = new List<string>();
        foreach (var child in helpers)
        {
            var output = child.Output.Trim();
            var failure = child.Failure?.Trim() ?? "";
            if (output.Length == 0 && failure.Length == 0) continue;
            if (output.Length > 6000) output = output[..6000] + "\n[Reported result truncated]";
            if (failure.Length > 1500) failure = failure[..1500] + "\n[Reported failure truncated]";
            var name = string.IsNullOrWhiteSpace(child.Name) ? child.SessionId : child.Name.Trim();
            var block = "Helper \"" + name + "\" state " + child.State + ".";
            if (output.Length > 0) block += "\nReported result:\n" + output;
            if (failure.Length > 0) block += "\nReported failure:\n" + failure;
            blocks.Add(block);
            if (blocks.Count == 6) break;
        }
        return blocks.Count == 0 ? "" : "Reported helper results already produced in this chat. Use these records. Do not invent helper results that are not listed.\n\n" + string.Join("\n\n", blocks);
    }
}

/// <summary>Collapsed or expanded helper cards for the chat that is open. Nested ids stay independent.</summary>
public sealed class SubagentCardFold
{
    private readonly Dictionary<string, bool> _open = new(StringComparer.Ordinal);

    public bool Expanded(string id, string? state)
    {
        var key = Key(id);
        if (_open.TryGetValue(key, out var stored)) return stored;
        return _open[key] = SubagentChatPanels.StartsExpanded(state);
    }

    public void Remember(string id, bool expanded) => _open[Key(id)] = expanded;

    public bool Toggle(string id, string? state)
    {
        var next = !Expanded(id, state);
        Remember(id, next);
        return next;
    }

    private static string Key(string id) => string.IsNullOrWhiteSpace(id) ? "" : id;
}
