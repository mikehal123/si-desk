using System.Text.Json;

namespace Sovereign.Desktop.Client;

/// <summary>
/// Pure ACP readers for Grok Build.
/// Ideas adapted from github.com/RongleCat/grok-app (MIT):
/// src-tauri/src/acp_client.rs, agent_subagents.rs, permission.rs.
/// </summary>
public static class GrokBuildAcp
{
    public static readonly TimeSpan PromptSilenceWindow = TimeSpan.FromSeconds(3);

    public static bool IsSessionUpdate(string? method) =>
        method is "session/update" or "_x.ai/session_notification" or "_x.ai/session/update";

    public static bool IsPromptComplete(string? method) => method == "_x.ai/session/prompt_complete";

    /// <summary>Notifications. They reset the silence clock and do not require a JSON-RPC result.</summary>
    public static bool IsSessionTraffic(string? method) => IsSessionUpdate(method) || IsPromptComplete(method);

    public static bool IsSelectablePermissionKind(string? kind) =>
        kind is "allow_once" or "reject_once"
        || (kind is not null && kind.StartsWith("allow_always", StringComparison.Ordinal));

    /// <summary>
    /// True only when a loadSession flag is present and boolean true.
    /// Accepts the initialize result, agentCapabilities, or agentCapabilities.sessionCapabilities.
    /// </summary>
    public static bool SupportsLoadSession(JsonElement initialize)
    {
        if (initialize.ValueKind != JsonValueKind.Object) return false;
        if (IsTrue(initialize, "loadSession")) return true;
        if (initialize.TryGetProperty("agentCapabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Object)
        {
            if (IsTrue(capabilities, "loadSession")) return true;
            if (capabilities.TryGetProperty("sessionCapabilities", out var sessions)
                && sessions.ValueKind == JsonValueKind.Object
                && IsTrue(sessions, "loadSession"))
                return true;
        }
        return initialize.TryGetProperty("capabilities", out var legacy)
            && legacy.ValueKind == JsonValueKind.Object
            && IsTrue(legacy, "loadSession");
    }

    public static bool TryParseSubagent(JsonElement update, string? kind, out GrokBuildSubagent parsed)
    {
        parsed = null!;
        if (update.ValueKind != JsonValueKind.Object) return false;
        var phase = kind switch
        {
            "subagent_spawned" => "spawned",
            "subagent_progress" => "progress",
            "subagent_finished" => "finished",
            _ => null
        };
        if (phase is null) return false;
        var id = Text(update, "subagent_id", "subagentId");
        if (string.IsNullOrEmpty(id)) return false;
        parsed = new GrokBuildSubagent(
            id,
            phase,
            Text(update, "subagent_type", "subagentType", "type"),
            Text(update, "description", "summary"),
            Text(update, "status"),
            Number(update, "duration_ms", "durationMs"),
            Number(update, "turns", "turn_count", "turnCount"),
            Number(update, "tool_calls", "tool_call_count", "toolCallCount"),
            Number(update, "tokens_used", "tokensUsed"),
            Text(update, "output", "result"));
        return true;
    }

    /// <summary>String tool name from <c>_meta["x.ai/tool"]</c>. An object form uses its name field.</summary>
    public static string? ToolName(JsonElement update)
    {
        if (update.ValueKind != JsonValueKind.Object || !update.TryGetProperty("_meta", out var meta) || meta.ValueKind != JsonValueKind.Object)
            return null;
        if (!meta.TryGetProperty("x.ai/tool", out var tool)) return null;
        if (tool.ValueKind == JsonValueKind.String) return BlankToNull(tool.GetString());
        if (tool.ValueKind == JsonValueKind.Object && tool.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            return BlankToNull(name.GetString());
        return null;
    }

    public static string PlanText(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object) return "";
        var text = Text(parameters, "planContent", "plan_content", "plan");
        if (text is not null) return text;
        if (parameters.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object)
            return Text(input, "plan") ?? "";
        return "";
    }

    public static string? ToolCallId(JsonElement parameters) =>
        parameters.ValueKind == JsonValueKind.Object ? Text(parameters, "toolCallId", "tool_call_id") : null;

    public static GrokBuildQuestion[] Questions(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("questions", out var questions) || questions.ValueKind != JsonValueKind.Array)
            return [];
        var list = new List<GrokBuildQuestion>();
        foreach (var item in questions.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var text = Text(item, "question", "prompt", "text") ?? "";
            var multi = item.TryGetProperty("multiSelect", out var flag) && flag.ValueKind == JsonValueKind.True;
            var options = new List<string>();
            if (item.TryGetProperty("options", out var values) && values.ValueKind == JsonValueKind.Array)
            {
                foreach (var option in values.EnumerateArray())
                {
                    var label = option.ValueKind == JsonValueKind.String
                        ? BlankToNull(option.GetString())
                        : option.ValueKind == JsonValueKind.Object ? Text(option, "label", "name") : null;
                    if (label is not null) options.Add(label);
                }
            }
            if (text.Length == 0 && options.Count == 0) continue;
            list.Add(new GrokBuildQuestion(text, options.ToArray(), multi));
        }
        return list.ToArray();
    }

    private static bool IsTrue(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (!obj.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) continue;
            var text = BlankToNull(value.GetString());
            if (text is not null) return text;
        }
        return null;
    }

    private static long? Number(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (!obj.TryGetProperty(name, out var value)) continue;
            if (GrokBuildJson.TryGetInt64(value, out var number) && number >= 0) return number;
        }
        return null;
    }

    private static string? BlankToNull(string? text)
    {
        var value = text?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}

public sealed record GrokBuildSubagent(
    string Id,
    string Phase,
    string? Type,
    string? Description,
    string? Status,
    long? DurationMs,
    long? Turns,
    long? ToolCalls,
    long? TokensUsed,
    string? Output)
{
    public string State => !string.IsNullOrWhiteSpace(Status) ? Status! : Phase == "finished" ? "completed" : "running";

    public string Progress
    {
        get
        {
            var parts = new List<string> { State };
            if (Turns is { } turns) parts.Add(turns + (turns == 1 ? " turn" : " turns"));
            if (ToolCalls is { } calls) parts.Add(calls + (calls == 1 ? " tool call" : " tool calls"));
            if (TokensUsed is { } tokens) parts.Add(tokens + " tokens");
            if (DurationMs is { } duration) parts.Add(duration + " ms");
            return string.Join(" · ", parts);
        }
    }
}

public sealed record GrokBuildQuestion(string Question, string[] Options, bool MultiSelect);

public sealed record GrokBuildPlanCard(string Plan, string? ToolCallId);

public sealed record GrokBuildPlanAnswer(string Outcome, string? Feedback)
{
    public static GrokBuildPlanAnswer Approved() => new("approved", null);
    public static GrokBuildPlanAnswer Cancelled(string? feedback = null) => new("cancelled", feedback);
}

public sealed record GrokBuildQuestionCard(GrokBuildQuestion[] Questions, string? ToolCallId);

public sealed record GrokBuildQuestionAnswer(string Outcome, IReadOnlyDictionary<string, object>? Answers)
{
    public static GrokBuildQuestionAnswer Cancelled() => new("cancelled", null);
    public static GrokBuildQuestionAnswer Accepted(IReadOnlyDictionary<string, object> answers) => new("accepted", answers);
}

/// <summary>Process arguments and environment edits for <c>grok agent stdio</c>.</summary>
public sealed record GrokBuildLaunch(IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment, IReadOnlyList<string> RemovedEnvironment);
