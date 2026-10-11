using System.Text.Json;

namespace Sovereign.Desktop.Client;

public sealed record CursorSubagent(string SessionId, string Name, string Task, string State, string Output = "",
    string CurrentWork = "Working", DateTimeOffset? StartedAt = null, DateTimeOffset? UpdatedAt = null,
    DateTimeOffset? CompletedAt = null, string Reasoning = "", CursorToolActivity? Tool = null,
    CursorToolActivity[]? Tools = null, string? ModelId = null, string? ModelName = null,
    string? ModelProvider = null, string? ModelSource = null,
    string? RequestedModelId = null, string? RequestedModelName = null,
    string? RequestedModelProvider = null, string? RequestedModelSource = null, string? Failure = null,
    CursorToolSummary? ToolSummary = null, CursorFileChange[]? Changes = null);

/// <summary>Routes native Cursor children without mixing their messages into the parent answer.</summary>
internal sealed class CursorSubagents
{
    private readonly Dictionary<string, CursorSubagent> _children = [];
    private readonly Dictionary<string, Dictionary<string, CursorToolActivity>> _tools = [];
    private readonly HashSet<string> _unconfirmedTerminal = [];
    public bool Contains(string id) => _children.ContainsKey(id);
    public bool HasRunning => _children.Values.Any(child => child.State == "running");
    public CursorSubagent? Get(string id) => _children.GetValueOrDefault(id);
    public CursorToolActivity? GetTool(string session, string id) => _tools.TryGetValue(session, out var tools) ? CursorToolParser.Find(tools, session + ":" + id, 64) : null;

    /// <summary>Records only metadata reported for this helper by its runtime, never a launch preference.</summary>
    public CursorSubagent? SetReportedModel(string session, string? modelId, string? modelName = null,
        string? modelProvider = null, string? modelSource = null)
    {
        if (SubagentModelPresentation.IsRequestedSource(modelSource))
            return SetRequestedModel(session, modelId, modelName, modelProvider, modelSource);
        if (!_children.TryGetValue(session, out var child)) throw new IOException("Model metadata belongs to an unknown subagent.");
        var id = ModelValue(modelId, 512);
        var name = ModelValue(modelName, 256);
        var provider = ModelValue(modelProvider, 120);
        var source = ModelValue(modelSource, 120);
        if (id is null && name is null) return null;
        var changed = id is not null && !string.Equals(id, child.ModelId, StringComparison.Ordinal);
        var updated = child with { ModelId = id ?? child.ModelId, ModelName = name ?? (changed ? null : child.ModelName),
            ModelProvider = provider ?? (changed ? null : child.ModelProvider),
            ModelSource = source ?? (changed ? null : child.ModelSource) };
        if (updated == child) return null;
        return _children[session] = updated with { UpdatedAt = child.State == "running" ? DateTimeOffset.UtcNow : child.UpdatedAt };
    }

    public CursorSubagent? SetRequestedModel(string session, string? modelId, string? modelName = null,
        string? modelProvider = null, string? modelSource = null)
    {
        if (!_children.TryGetValue(session, out var child)) throw new IOException("Model metadata belongs to an unknown subagent.");
        var id = ModelValue(modelId, 512);
        var name = ModelValue(modelName, 256);
        var provider = ModelValue(modelProvider, 120);
        var source = ModelValue(modelSource, 120);
        if (id is null && name is null) return null;
        var changed = id is not null && !string.Equals(id, child.RequestedModelId, StringComparison.Ordinal);
        var updated = child with { RequestedModelId = id ?? child.RequestedModelId,
            RequestedModelName = name ?? (changed ? null : child.RequestedModelName),
            RequestedModelProvider = provider ?? (changed ? null : child.RequestedModelProvider),
            RequestedModelSource = source ?? (changed ? null : child.RequestedModelSource) };
        if (updated == child) return null;
        return _children[session] = updated with { UpdatedAt = child.State == "running" ? DateTimeOffset.UtcNow : child.UpdatedAt };
    }

    /// <summary>Reads the model id Cursor resolved for this helper run. A missing value stays unknown.</summary>
    private CursorSubagent? ApplyRuntimeModel(string session, JsonElement update)
    {
        if (!update.TryGetProperty("_meta", out var metadata) || metadata.ValueKind != JsonValueKind.Object ||
            !metadata.TryGetProperty("cursor", out var cursor) || cursor.ValueKind != JsonValueKind.Object ||
            !cursor.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.String)
            return null;
        var id = model.GetString();
        return SetReportedModel(session, id, modelProvider: SubagentModelPresentation.KnownProvider(id), modelSource: "runtime assignment");
    }

    private static string? ModelValue(string? text, int limit)
    {
        var value = text?.Trim();
        return string.IsNullOrEmpty(value) || value.Length > limit || value.Any(char.IsControl) ? null : value;
    }

    public CursorSubagent? SetFailure(string session, string? failure)
    {
        if (!_children.TryGetValue(session, out var child)) throw new IOException("Failure detail belongs to an unknown subagent.");
        if (string.IsNullOrWhiteSpace(failure)) return null;
        var value = Clip(failure.Trim(), 6000);
        if (child.Failure == value) return null;
        return _children[session] = child with { Failure = value };
    }

    /// <summary>The caller must correlate an authoritative final result to this helper's current/recent turn.</summary>
    public CursorSubagent? SetFinalOutput(string session, string output)
    {
        if (!_children.TryGetValue(session, out var child)) throw new IOException("Final output belongs to an unknown subagent.");
        if (string.IsNullOrEmpty(output)) return null;
        var value = Clip(output, 6000);
        if (child.Output == value) return null;
        return _children[session] = child with { Output = value };
    }

    public CursorToolActivity MergeTool(string session, JsonElement update)
    {
        if (!_children.TryGetValue(session, out var child) || child.State != "running")
            throw new IOException("Cursor tool belongs to an inactive or unknown subagent.");
        return CursorToolParser.Merge(update, _tools[session], session + ":", limit: 64);
    }

    public CursorSubagent SetWork(string session, string work)
    {
        var child = _children[session];
        return _children[session] = child with { CurrentWork = Clip(work, 240), UpdatedAt = DateTimeOffset.UtcNow };
    }

    public CursorSubagent ObserveTool(string session, JsonElement update)
    {
        var tool = MergeTool(session, update);
        return ObserveToolActivity(session, tool);
    }

    public CursorSubagent ObserveToolActivity(string session, CursorToolActivity tool)
    {
        var child = _children[session];
        if (child.State != "running") throw new IOException("Cursor tool belongs to an inactive subagent.");
        var history = CursorToolParser.History(_tools[session], 64);
        history.Store(tool);
        var work = tool.Status switch { "failed" => "Tool failed · " + tool.Title,
            "blocked" => "Tool blocked · " + tool.Title, "completed" => "Finished · " + tool.Title, _ => tool.Title };
        return _children[session] = child with { Tool = Preview(tool), Tools = _tools[session].Values.Select(Preview).ToArray(),
            ToolSummary = history.Summary, Changes = history.Changes,
            CurrentWork = Clip(work, 240), UpdatedAt = DateTimeOffset.UtcNow };
    }

    public CursorSubagent Details(string session, string name, string task)
    {
        var child = _children[session];
        var nextTask = string.IsNullOrWhiteSpace(child.Task) && !string.IsNullOrWhiteSpace(task) ? Clip(task, 2000) : child.Task;
        var nextName = string.IsNullOrWhiteSpace(name) ? child.Name : Clip(name, 120);
        if (nextName == child.Name && nextTask == child.Task) return child;
        return _children[session] = child with { Name = nextName, Task = nextTask, UpdatedAt = DateTimeOffset.UtcNow };
    }

    public CursorSubagent? Result(string session, string output)
    {
        var child = _children[session];
        if (child.Output.Length > 0 || string.IsNullOrEmpty(output)) return null;
        return _children[session] = child with { Output = Clip(output, 6000), UpdatedAt = DateTimeOffset.UtcNow };
    }

    public CursorSubagent Resume(string session, string task)
    {
        var child = _children[session];
        if (child.State == "running") return child;
        if (_children.Values.Count(c => c.State == "running") >= 3) throw new IOException("Subagent concurrency limit exceeded.");
        _unconfirmedTerminal.Remove(session);
        CursorToolParser.Reset(_tools[session]);
        var now = DateTimeOffset.UtcNow;
        return _children[session] = child with { State = "running",
            Task = string.IsNullOrWhiteSpace(task) ? child.Task : Clip(task, 2000),
            Output = "", Reasoning = "", Tool = null, Tools = [],
            CurrentWork = "Working", StartedAt = now, UpdatedAt = now, CompletedAt = null, Failure = null,
            // Reported identity is scoped to the ended run. Retain the configured
            // preference until this new run supplies its own execution metadata.
            ModelId = null, ModelName = null, ModelProvider = null, ModelSource = null,
            ToolSummary = null, Changes = [] };
    }
    public CursorSubagent? Observe(string parent, string session, JsonElement update, string? displayName = null)
    {
        string Text(string key) => update.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        var kind = Text("sessionUpdate");
        var now = DateTimeOffset.UtcNow;
        if (kind == "subagent_spawned")
        {
            // This first version permits only direct helpers. A recursive tree is
            // not silently granted more authority or unbounded UI/storage space.
            if (session != parent) throw new IOException("Nested subagents are not supported in this chat.");
            var id = Text("subagentSessionId");
            if (string.IsNullOrWhiteSpace(id) || id == parent || _children.ContainsKey(id) || _children.Count >= 16)
                throw new IOException("Invalid or excessive subagent identity.");
            if (_children.Values.Count(c => c.State == "running") >= 3)
                throw new IOException("Subagent concurrency limit exceeded. This response was stopped.");
            _tools[id] = [];
            var name = Text("name");
            if (name is "" or "generalPurpose" && !string.IsNullOrWhiteSpace(displayName)) name = displayName;
            var spawned = _children[id] = new(id, Clip(name, 120), Clip(Text("task"), 2000), "running", StartedAt: now, UpdatedAt: now);
            var requested = SetRequestedModel(id, Text("requestedModelId"), Text("requestedModelName"), Text("requestedModelProvider"), "runtime request") ?? spawned;
            // This runtime puts the resolved helper model on _meta.cursor.model.
            // It does not emit current_model_update for the child session.
            return ApplyRuntimeModel(id, update) ?? requested;
        }
        if (kind == "subagent_state_update")
        {
            if (session != parent || !_children.TryGetValue(Text("subagentSessionId"), out var child))
                throw new IOException("Subagent state belongs to an unknown session.");
            var state = Text("state");
            if (state is not ("completed" or "failed" or "cancelled" or "disconnected"))
                throw new IOException("Unknown subagent state.");
            if (child.State != "running" && !_unconfirmedTerminal.Remove(child.SessionId))
            {
                if (child.State == state) return null;
                throw new IOException("Subagent terminal state changed unexpectedly.");
            }
            var failure = Text("failure");
            if (failure.Length == 0 && update.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String) failure = message.GetString() ?? "";
            var history = CursorToolParser.History(_tools[child.SessionId], 64);
            history.Finish();
            _children[child.SessionId] = child with { State = state, CurrentWork = state switch
                { "completed" => "Completed", "failed" => "Failed", "cancelled" => "Cancelled", _ => "Disconnected" },
                UpdatedAt = now, CompletedAt = now, Failure = failure.Length > 0 ? Clip(failure, 6000) : child.Failure,
                ToolSummary = history.Summary, Changes = history.Changes, Tools = _tools[child.SessionId].Values.Select(Preview).ToArray() };
            return ApplyRuntimeModel(child.SessionId, update) ?? _children[child.SessionId];
        }
        if (session == parent) return null;
        if (!_children.TryGetValue(session, out var current)) throw new IOException("Unknown Cursor child session.");
        if (kind == "current_model_update")
            return SetReportedModel(session, Text("currentModelId"), Text("modelName"), Text("modelProvider"), "runtime model update");
        if (current.State != "running") return null;
        if (kind is "tool_call" or "tool_call_update")
            return ObserveTool(session, update);
        if (kind == "plan") return SetWork(session, "Updating plan");
        if (kind is "agent_message_chunk" or "agent_thought_chunk" && update.TryGetProperty("content", out var content) &&
            content.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "text" &&
            content.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
        {
            var value = text.GetString() ?? "";
            if (value.Length == 0) return null;
            return _children[session] = kind == "agent_thought_chunk"
                ? current with { Reasoning = Clip(current.Reasoning + value, 6000), CurrentWork = "Thinking", UpdatedAt = now }
                : current with { Output = Clip(current.Output + value, 6000), CurrentWork = "Writing response", UpdatedAt = now };
        }
        return null;
    }

    public IEnumerable<CursorSubagent> FinishRunning(string work)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (id, child) in _children.ToArray())
            if (child.State == "running")
            {
                _unconfirmedTerminal.Add(id);
                var history = CursorToolParser.History(_tools[id], 64); history.Finish();
                yield return _children[id] = child with { State = "disconnected", CurrentWork = work, UpdatedAt = now, CompletedAt = now,
                    ToolSummary = history.Summary, Changes = history.Changes,
                    Tool = child.Tool is { Status: "pending" or "in_progress" } tool ? tool with { Status = "unconfirmed" } : child.Tool,
                    Tools = child.Tools?.Select(t => t.Status is "pending" or "in_progress" ? t with { Status = "unconfirmed" } : t).ToArray() };
            }
    }

    private static CursorToolActivity Preview(CursorToolActivity tool)
    {
        var remaining = 32000;
        var changes = new List<CursorFileChange>();
        foreach (var change in tool.Changes.Take(64))
        {
            var length = change.Path.Length + (change.Before?.Length ?? 0) + change.After.Length + (change.UnifiedDiff?.Length ?? 0);
            if (length > remaining) continue; // Do not manufacture partial file contents as complete diffs.
            changes.Add(change); remaining -= length;
        }
        var omitted = changes.Count < tool.Changes.Length ? "\n[Some file change details omitted from saved preview]" : "";
        return tool with { Title = Clip(tool.Title, 240), Input = Clip(tool.Input, 2000),
            Output = Clip(tool.Output, 6000) + omitted, Changes = changes.ToArray() };
    }
    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];
}
