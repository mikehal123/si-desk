using Sovereign.Desktop.Client;

namespace Sovereign.Desktop;

internal sealed partial class ChatConversation
{
    private string _turnSkillCaption = "";
    private readonly Dictionary<string, string> _skillCaptions = [];
    private readonly HashSet<string> _skillNoted = [];

    internal string SkillCaption(string id) => _skillCaptions.GetValueOrDefault(id) ?? "";

    private void ApplySkillGuidance(string query)
    {
        _turnSkillCaption = "";
        var guidance = "";
        try
        {
            if (_owner.Skills.Enabled && (CursorChat || GrokBuildChat) && _saved.SubagentsEnabled)
            {
                var retrieval = _owner.Skills.Retrieve(query);
                _turnSkillCaption = retrieval.Caption;
                guidance = retrieval.Injection;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { guidance = ""; _chatStatus.Text = "Skill library could not be read: " + ex.Message; }
        if (_cursorChat is not null) _cursorChat.SkillGuidance = guidance;
        if (_grokBuild is not null) _grokBuild.SkillGuidance = guidance;
    }

    private void RememberSkill(CursorSubagent child)
    {
        if (!_skillNoted.Add(child.SessionId) || !_owner.Skills.Enabled) return;
        var tools = child.Tools ?? (child.Tool is null ? [] : [child.Tool]);
        var errors = child.ToolSummary is { } summary
            ? ClampCount(summary.Failed + summary.Blocked)
            : tools.Count(tool => tool.Status is "failed" or "blocked");
        var steps = child.ToolSummary is { } totals ? ClampCount(totals.Total) : tools.Length;
        var met = child.State == "completed" && string.IsNullOrWhiteSpace(child.Failure) && child.Output.Trim().Length >= 12;
        var stepsText = tools.Length == 0
            ? child.Output.Trim()
            : string.Join("\n", tools.Select((tool, index) => (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ". " + tool.Title + " (" + tool.Status + ")"));
        var run = new SkillRun(child.SessionId, string.IsNullOrWhiteSpace(child.Name) ? "Helper" : child.Name.Trim(),
            child.Task, child.State, steps, errors, met, string.IsNullOrWhiteSpace(child.Task) ? child.Name : child.Task,
            stepsText, tools.Select(tool => string.IsNullOrWhiteSpace(tool.Kind) ? tool.Title : tool.Kind).ToArray(),
            child.CompletedAt ?? DateTimeOffset.UtcNow);
        try { _owner.Skills.Consider(run); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _chatStatus.Text = "Skill library could not be saved: " + ex.Message; }
    }

    private static int ClampCount(long value) => value < 0 ? 0 : value > 10000 ? 10000 : (int)value;
}
