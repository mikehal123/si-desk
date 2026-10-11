namespace Sovereign.Desktop.Client;

/// <summary>One identity line and one lifecycle line for a helper card. Configuration is not execution evidence.</summary>
public static class SubagentCardText
{
    public static string IdentityLine(CursorSubagent child)
    {
        if (HasReportedIdentity(child))
        {
            var vendor = Vendor(SubagentModelPresentation.ProviderLabel(child), child.ModelId);
            var model = SubagentModelPresentation.Display(child);
            return vendor.Length == 0 ? model : vendor + " · " + model;
        }
        if (SubagentModelPresentation.HasRequestedModel(child))
        {
            var model = SubagentModelPresentation.RequestedDisplay(child);
            if (model == SubagentModelPresentation.Unknown) return "Model not reported";
            var vendor = Vendor(SubagentModelPresentation.RequestedProviderLabel(child), child.RequestedModelId);
            return (vendor.Length == 0 ? model : vendor + " · " + model) + " · configured";
        }
        return "Model not reported";
    }

    public static string IdentityDetail(CursorSubagent child)
    {
        var lines = new List<string> { "Identity: " + IdentityLine(child) };
        if (HasReportedIdentity(child))
        {
            lines.Add("Reported for this helper. Source: " + SubagentModelPresentation.SourceLabel(child) + ".");
            if (SubagentModelPresentation.HasRequestedModel(child))
                lines.Add("Configured: " + ConfiguredPhrase(child) + ".");
        }
        else if (SubagentModelPresentation.HasRequestedModel(child) && SubagentModelPresentation.RequestedDisplay(child) != SubagentModelPresentation.Unknown)
            lines.Add("Execution model was not separately reported.");
        lines.Add("Role: " + RoleLine(child) + ".");
        return string.Join("\n", lines);
    }

    public static string RoleLine(CursorSubagent child) => "Role not reported";

    public static string TaskLine(CursorSubagent child) =>
        string.IsNullOrWhiteSpace(child.Task) ? "Task details not supplied by provider" : child.Task;

    public static string LifecycleLine(CursorSubagent child)
    {
        var tools = ToolCounts(child.ToolSummary);
        return child.State switch
        {
            "running" or "pending" or "queued" or "waiting" => Join("Working", ActionPhrase(child)),
            "completed" => Join("Finished", tools),
            "failed" => Join("Failed", tools),
            "cancelled" or "stopped" => Join("Stopped", tools),
            "stop requested" => Join("Stop requested", tools),
            "disconnected" or "interrupted" or "unconfirmed" => Join("Interrupted", tools),
            "blocked" => Join("Blocked", tools),
            _ => child.State
        };
    }

    public static string DetailBlock(CursorSubagent child, string elapsed, string started)
    {
        var text = IdentityDetail(child) + $"\nStarted: {started}\nRuntime: {elapsed}";
        if (child.ToolSummary is { } activity)
            text += $"\nTools: {(activity.CountsIncomplete ? "At least " : "")}{activity.Total} steps · {activity.Completed} completed · {activity.Failed} failed · {activity.Blocked} blocked · {activity.Unconfirmed} unconfirmed"
                + (activity.Cancelled > 0 ? $" · {activity.Cancelled} stopped" : "")
                + (activity.Other > 0 ? $" · {activity.Other} other" : "")
                + $"\nFiles changed: {activity.ChangedFiles}";
        return text;
    }

    private static bool HasReportedIdentity(CursorSubagent child) =>
        !SubagentModelPresentation.IsRequestedSource(child.ModelSource)
        && SubagentModelPresentation.Display(child) != SubagentModelPresentation.Unknown;

    private static string ConfiguredPhrase(CursorSubagent child)
    {
        var model = SubagentModelPresentation.RequestedDisplay(child);
        var vendor = Vendor(SubagentModelPresentation.RequestedProviderLabel(child), child.RequestedModelId);
        return vendor.Length == 0 ? model : vendor + " · " + model;
    }

    private static string Vendor(string label, string? modelId)
    {
        if (label != "Provider unknown") return label;
        return SubagentModelPresentation.KnownProvider(modelId) switch
        {
            "openai" => "OpenAI",
            "xai" => "xAI",
            _ => ""
        };
    }

    private static string ActionPhrase(CursorSubagent child)
    {
        var work = child.CurrentWork?.Trim() ?? "";
        if (work.Length == 0 || work is "Working" or "Completed" or "Failed" or "Cancelled" or "Disconnected" or "Interrupted") return "";
        const string finished = "Finished · ";
        return work.StartsWith(finished, StringComparison.Ordinal) ? work[finished.Length..] : work;
    }

    private static string ToolCounts(CursorToolSummary? summary)
    {
        if (summary is null) return "";
        var parts = new List<string>();
        if (summary.Failed > 0) parts.Add(summary.Failed == 1 ? "1 tool error" : summary.Failed + " tool errors");
        if (summary.Unconfirmed > 0) parts.Add(summary.Unconfirmed == 1 ? "1 unconfirmed" : summary.Unconfirmed + " unconfirmed");
        if (summary.Blocked > 0) parts.Add(summary.Blocked == 1 ? "1 blocked" : summary.Blocked + " blocked");
        return string.Join(" · ", parts);
    }

    private static string Join(string state, string extra) => extra.Length == 0 ? state : state + " · " + extra;
}
