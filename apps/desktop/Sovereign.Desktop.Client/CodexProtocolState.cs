using System.Text;
using System.Text.Json;

namespace Sovereign.Desktop.Client;

/// <summary>Maps official app-server items into the shared activity/sidebar model.</summary>
internal sealed class CodexProtocolState(string workspace, CursorProjectOptions? project, Action<CursorActivityEvent>? activity)
{
    private readonly object _sync = new();
    private readonly CursorSubagents _children = new();
    private readonly Dictionary<string, CursorToolActivity> _tools = [];
    private readonly Dictionary<string, string> _turns = [];
    private readonly Dictionary<string, string> _endedTurns = [];
    private readonly Dictionary<string, string> _messages = [];
    private readonly HashSet<string> _completedMessages = [];
    private readonly Queue<string> _completedOrder = new();
    private readonly Dictionary<string, (string Thread, string ToolId)> _processes = [];
    private readonly Dictionary<string, string> _processBuffers = [];
    private readonly Dictionary<string, string> _itemBuffers = [];
    private readonly Dictionary<string, string> _cells = [];
    private readonly HashSet<string> _observedTurns = [];
    private readonly Dictionary<string, string> _agentNames = [];
    private string? _lastParentMessage;
    private TaskCompletionSource<bool> _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string? ParentThread { get; private set; }
    public Action<string>? ChildSpawned { get; set; }
    private static string Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString()! : "";
    public void AssignParent(string id)
    {
        lock (_sync) { if (ParentThread is not null && ParentThread != id) throw new IOException("Codex returned an unexpected parent thread."); ParentThread = id; }
    }
    public bool IsParent(JsonElement p) => Text(p, "threadId") == ParentThread;
    public bool Owns(JsonElement p)
    {
        lock (_sync)
        {
            var thread = Text(p, "threadId"); var turn = Text(p, "turnId");
            return (thread == ParentThread || _children.Get(thread)?.State == "running") &&
                _turns.TryGetValue(thread, out var current) && turn == current;
        }
    }
    public List<(string Thread, string Turn)> ActiveTurns()
    {
        lock (_sync) return _turns.Where(t => t.Key == ParentThread || _children.Get(t.Key)?.State == "running").Select(t => (t.Key, t.Value)).ToList();
    }
    public void Finish(string reason, string? actualFailure = null)
    {
        lock (_sync)
        {
            foreach (var thread in _turns.Keys.Where(id => id != ParentThread).ToArray()) _endedTurns[thread] = _turns[thread];
            foreach (var child in _children.FinishRunning(reason))
            {
                var failed = actualFailure is null ? null : _children.SetFailure(child.SessionId, actualFailure);
                Report(failed ?? child);
            }
            _settled.TrySetResult(true);
        }
    }
    public Task WaitForChildrenAsync(CancellationToken ct)
    {
        lock (_sync) return _children.HasRunning ? _settled.Task.WaitAsync(ct) : Task.CompletedTask;
    }
    public void Observe(string method, JsonElement p, Action<string> delta)
    {
        lock (_sync)
        {
            if (method == "command/exec/outputDelta") { ExecDelta(p); return; }
            if (method == "thread/started") { Thread(p.GetProperty("thread")); return; }
            var thread = Text(p, "threadId");
            if (thread != ParentThread && !_children.Contains(thread)) return;
            var child = thread != ParentThread;
            if (method == "turn/started")
            {
                if (!child) _lastParentMessage = null;
                _observedTurns.Add(thread);
                _turns[thread] = Text(p.GetProperty("turn"), "id");
                _endedTurns.Remove(thread);
                if (child && _children.Get(thread)?.State != "running") { ReopenSettler(); Report(_children.Resume(thread, _children.Get(thread)?.Task ?? "")); }
                return;
            }
            if (method == "turn/completed")
            {
                var turn = p.GetProperty("turn");
                if (_turns.TryGetValue(thread, out var known) && Text(turn, "id") != known) return;
                if (child && !_turns.ContainsKey(thread) && _endedTurns.TryGetValue(thread, out var ended) && Text(turn, "id") != ended) return;
                _observedTurns.Add(thread);
                if (TurnItemsReady(turn)) ApplyTurnSnapshot(thread, turn, delta, includeCollaboration: child);
                if (child) { _endedTurns[thread] = Text(turn, "id"); Failure(thread, turn); }
                else _turns.Remove(thread);
                if (child) Terminal(thread, Text(turn, "status") switch { "completed" => "completed", "interrupted" => "cancelled", _ => "failed" });
                return;
            }
            var eventTurn = Text(p, "turnId");
            var finalMessage = method == "item/agentMessage/delta" || method == "item/completed" &&
                p.TryGetProperty("item", out var finalItem) && Text(finalItem, "type") == "agentMessage";
            var lateFinal = child && finalMessage && !_turns.ContainsKey(thread) &&
                _children.Get(thread)?.State != "running" && _endedTurns.GetValueOrDefault(thread) == eventTurn && eventTurn.Length > 0;
            if (eventTurn.Length > 0 && (!_turns.TryGetValue(thread, out var currentTurn) || currentTurn != eventTurn) && !lateFinal) return;
            if (method == "error" && child)
            {
                Failure(thread, p);
                if (!p.TryGetProperty("willRetry", out var retry) || retry.ValueKind != JsonValueKind.True) Terminal(thread, "failed");
                return;
            }
            if (method == "model/rerouted" && child && _children.Get(thread)?.State == "running" &&
                eventTurn.Length > 0 && Text(p, "toModel") is { Length: > 0 } actualModel)
            {
                var updated = _children.SetReportedModel(thread, actualModel, modelSource: "runtime reroute");
                if (updated is not null) Report(updated);
                return;
            }
            if (method == "thread/status/changed" && child)
            {
                if (p.TryGetProperty("status", out var threadStatus) && Text(threadStatus, "type") == "active") Report(_children.SetWork(thread, "Working"));
                return;
            }
            if (method == "item/agentMessage/delta")
            {
                var text = Text(p, "delta"); var key = thread + ":" + Text(p, "itemId");
                if (_completedMessages.Contains(key)) return;
                RememberStream(key, Clip(_messages.GetValueOrDefault(key, "") + text, child ? 6000 : 200000));
                Message(thread, text, false, delta, Text(p, "itemId")); return;
            }
            if (method is "item/reasoning/summaryTextDelta" or "item/reasoning/textDelta") { Message(thread, Text(p, "delta"), true, delta); return; }
            if (method == "turn/plan/updated")
            {
                if (child) Report(_children.SetWork(thread, "Updating plan"));
                else if (p.TryGetProperty("plan", out var plan)) activity?.Invoke(new("plan", Plan: plan.EnumerateArray().Take(100).Select((v, i) => new CursorPlanItem(i.ToString(), Text(v, "step"), Text(v, "status") switch { "inProgress" => "in_progress", var s => s })).ToArray()));
                return;
            }
            if (method is "item/commandExecution/outputDelta" or "item/fileChange/outputDelta")
            {
                var key = thread + ":" + Text(p, "itemId");
                var piece = Text(p, "delta");
                if (piece.Length == 0) return;
                if (_tools.TryGetValue(key, out var tool) && tool.Status is "in_progress" or "pending")
                    Tool(thread, tool with { Output = Clip(CommandPresentation.Redact(tool.Output + piece), 32000) });
                else if (!_tools.ContainsKey(key))
                    _itemBuffers[key] = Clip(CommandPresentation.Redact((_itemBuffers.GetValueOrDefault(key) ?? "") + piece), 32000);
                return;
            }
            if (method is not ("item/started" or "item/completed")) return;
            ApplyObservedItem(thread, p.GetProperty("item"), method == "item/started", delta);
        }
    }
    public void Hydrate(JsonElement result, string? expectedThread = null)
    {
        lock (_sync)
        {
            if (!result.TryGetProperty("thread", out var thread)) return;
            if (expectedThread is not null && Text(thread, "id") != expectedThread) throw new IOException("Codex helper subscription returned an unexpected thread.");
            if (!Thread(thread)) return;
            var id = Text(thread, "id");
            ReportModel(id, result);
            if (!_children.Contains(id)) return;
            if (thread.TryGetProperty("turns", out var turns)) foreach (var turn in turns.EnumerateArray())
                if (Text(turn, "status") == "inProgress" && !_observedTurns.Contains(id) && _children.Get(id)?.State == "running") _turns[id] = Text(turn, "id");
            if (!thread.TryGetProperty("turns", out turns) || turns.ValueKind != JsonValueKind.Array || turns.GetArrayLength() == 0) return;
            var latest = turns.EnumerateArray().Last(); var latestId = Text(latest, "id");
            if (latestId.Length == 0) return;
            if (Text(latest, "status") == "inProgress")
            {
                if (_observedTurns.Contains(id) || _children.Get(id)?.State != "running") return;
                ApplyTurnSnapshot(id, latest, _ => { }, includeCollaboration: false);
                _observedTurns.Add(id);
                return;
            }
            if (_turns.TryGetValue(id, out var active) && active != latestId ||
                !_turns.ContainsKey(id) && _observedTurns.Contains(id) &&
                (_endedTurns.GetValueOrDefault(id) != latestId || _children.Get(id)?.State == "running")) return;
            if (latest.TryGetProperty("items", out var items)) foreach (var item in items.EnumerateArray())
            {
                var itemType = Text(item, "type");
                if (itemType != "agentMessage")
                {
                    if (itemType is "" or "userMessage" or "reasoning" or "plan" or "hookPrompt" or "subAgentActivity" or "collabAgentToolCall") continue;
                    ApplyObservedItem(id, item, false, _ => { });
                    continue;
                }
                var key = id + ":" + Text(item, "id");
                if (_completedMessages.Contains(key)) continue;
                var updated = _children.SetFinalOutput(id, Text(item, "text"));
                if (updated is not null) Report(updated);
                MarkCompleted(key);
            }
            _observedTurns.Add(id); _endedTurns[id] = latestId;
            Failure(id, latest);
            Terminal(id, Text(latest, "status") switch { "completed" => "completed", "interrupted" => "cancelled", _ => "failed" });
        }
    }
    private bool Thread(JsonElement thread)
    {
        var id = Text(thread, "id"); var parent = Text(thread, "parentThreadId");
        var sourceParent = "";
        if (thread.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object &&
            source.TryGetProperty("subAgent", out var sub) && sub.ValueKind == JsonValueKind.Object && sub.TryGetProperty("thread_spawn", out var spawn)) sourceParent = Text(spawn, "parent_thread_id");
        if (parent.Length == 0) parent = sourceParent;
        if (parent.Length == 0) { if (ParentThread is null) AssignParent(id); return false; }
        if (sourceParent.Length > 0 && sourceParent != parent) return false;
        if (parent != ParentThread)
        {
            if (_children.Contains(parent)) throw new IOException("Nested Codex subagents are not supported in this chat.");
            return false;
        }
        // preview is usually the first user message, often inherited from the parent. It is not this child's task.
        Spawn(id, Text(thread, "agentNickname") is { Length: > 0 } nickname ? nickname : Text(thread, "agentRole"), "");
        ReportModel(id, thread);
        return true;
    }
    private void ReportModel(string id, JsonElement value)
    {
        // Configured identity is not execution telemetry and never replaces it.
        if (!_children.Contains(id)) return;
        var updated = _children.SetRequestedModel(id, Text(value, "model"), modelProvider: Text(value, "modelProvider"), modelSource: "runtime configuration");
        if (updated is not null) Report(updated);
    }
    private void Failure(string id, JsonElement value)
    {
        var detail = value.TryGetProperty("error", out var error) ? Text(error, "message") : "";
        var updated = _children.SetFailure(id, detail);
        if (updated is not null) Report(updated);
    }
    public void SubscriptionFailure(string id, string detail)
    {
        lock (_sync)
        {
            if (!_children.Contains(id)) return;
            var updated = _children.SetFailure(id, detail);
            if (updated is not null) Report(updated);
        }
    }
    private void Spawn(string id, string name, string task)
    {
        if (project?.SubagentsAllowed != true) throw new IOException("Codex spawned a helper while subagents are disabled.");
        if (_agentNames.TryGetValue(id, out var taskName)) name = taskName;
        if (_children.Contains(id))
        {
            if (_children.Get(id)?.State == "running") Report(_children.Details(id, name, task));
            return;
        }
        ReopenSettler();
        Report(_children.Observe(ParentThread!, ParentThread!, JsonSerializer.SerializeToElement(new { sessionUpdate = "subagent_spawned",
            subagentSessionId = id, name = name.Length == 0 ? "Helper" : name, task }))!);
        ChildSpawned?.Invoke(id);
    }
    private void SubagentActivity(string sender, JsonElement item, bool started)
    {
        var id = Text(item, "agentThreadId"); var kind = Text(item, "kind");
        if (id.Length == 0) throw new IOException("Codex returned helper activity without its thread identity.");
        if (sender != ParentThread)
        {
            // Native send_message(target: /root) records an interaction in the
            // sending child's thread, with the parent as its receiver. This is
            // not a new helper identity or a change to the parent's lifecycle.
            if (id == ParentThread && kind == "interacted")
            {
                if (_children.Get(sender)?.State == "running") Report(_children.SetWork(sender, "Reporting to parent"));
                return;
            }
            if (!_children.Contains(id)) throw new IOException("Nested Codex subagents are not supported in this chat.");
            return;
        }
        var path = Text(item, "agentPath").TrimEnd('/');
        // Registered direct ancestry is authoritative; absolute runtime paths
        // may contain a parent namespace. Unknown deeper identities stay denied.
        if (!_children.Contains(id) && path.Split('/', StringSplitOptions.RemoveEmptyEntries).Length > 2)
            throw new IOException("Nested Codex subagents are not supported in this chat.");
        var name = path[(path.LastIndexOf('/') + 1)..];
        if (name.Length > 0) _agentNames[id] = Clip(name, 120);
        Spawn(id, name, "");
        // Both started/completed notifications describe one lifecycle item. Do not
        // reopen a fast helper when the spawn item's completion follows its turn.
        if (started && kind == "interacted" && _children.Get(id)?.State != "running")
        { ReopenSettler(); Report(_children.Resume(id, _children.Get(id)?.Task ?? "")); }
        if (!started && kind is "completed" or "interrupted")
            Terminal(id, kind == "completed" ? "completed" : "cancelled");
    }
    private void Collaboration(string sender, JsonElement item, bool started)
    {
        var reportedSender = Text(item, "senderThreadId");
        if (Text(item, "tool") == "spawnAgent" && (sender != ParentThread || reportedSender.Length > 0 && reportedSender != ParentThread))
            throw new IOException("Nested Codex subagents are not supported.");
        var tool = Text(item, "tool"); var prompt = Text(item, "prompt");
        foreach (var idValue in item.GetProperty("receiverThreadIds").EnumerateArray())
        {
            var id = idValue.GetString()!;
            if (tool == "spawnAgent") Spawn(id, "", prompt);
            else if (started && _children.Contains(id) && tool is "resumeAgent" or "followupTask" or "sendInput")
            { ReopenSettler(); Report(_children.Resume(id, prompt)); }
        }
        if (item.TryGetProperty("agentsStates", out var states)) foreach (var value in states.EnumerateObject())
        {
            if (!_children.Contains(value.Name)) continue;
            var status = Text(value.Value, "status"); var message = Text(value.Value, "message");
            var child = _children.Get(value.Name)!;
            if (message.Length > 0 && child.Output.Length == 0 && status is "completed" or "errored" or "interrupted")
            {
                var result = _children.Result(value.Name, message);
                if (result is not null) Report(result);
            }
            if (status is "completed" or "errored" or "interrupted" or "shutdown" or "notFound")
                Terminal(value.Name, status switch { "completed" => "completed", "errored" => "failed", "interrupted" => "cancelled", _ => "disconnected" });
        }
    }
    private void Terminal(string thread, string state)
    {
        if (_turns.Remove(thread, out var ended)) _endedTurns[thread] = ended;
        if (_children.Get(thread)?.State != "running") return;
        var updated = _children.Observe(ParentThread!, ParentThread!, JsonSerializer.SerializeToElement(new { sessionUpdate = "subagent_state_update", subagentSessionId = thread, state }));
        if (updated is not null) Report(updated);
        if (!_children.HasRunning) _settled.TrySetResult(true);
    }
    private void Message(string thread, string text, bool thought, Action<string> delta, string? itemId = null)
    {
        if (text.Length == 0) return;
        if (thread == ParentThread)
        {
            if (!thought && itemId is { Length: > 0 })
            {
                if (_lastParentMessage is not null && _lastParentMessage != itemId) delta("\n\n");
                _lastParentMessage = itemId;
            }
            activity?.Invoke(new(thought ? "reasoning" : "message", thought ? text : "")); if (!thought) delta(text);
        }
        else
        {
            if (!thought && _children.Get(thread)?.State != "running" && itemId is not null)
            {
                var updated = _children.SetFinalOutput(thread, _messages.GetValueOrDefault(thread + ":" + itemId, ""));
                if (updated is not null) Report(updated);
                return;
            }
            var child = _children.Observe(ParentThread!, thread, JsonSerializer.SerializeToElement(new { sessionUpdate = thought ? "agent_thought_chunk" : "agent_message_chunk", content = new { type = "text", text } }));
            if (child is not null) Report(child);
        }
    }
    private void Tool(string thread, CursorToolActivity tool)
    {
        Remember(tool);
        if (thread == ParentThread) { activity?.Invoke(new("tool", Tool: tool)); project?.ToolActivity?.Invoke(tool); }
        else if (_children.Get(thread)?.State == "running") Report(_children.ObserveToolActivity(thread, tool), tool);
    }
    private void Remember(CursorToolActivity tool)
    {
        var known = _tools.ContainsKey(tool.Id);
        if (!known)
            while (_tools.Count >= 1000 && TrimFinished()) { }
        if (known || _tools.Count < 1000 || Retain(tool)) _tools[tool.Id] = tool;
    }
    private static bool Retain(CursorToolActivity tool) => tool.Status is "pending" or "in_progress" || tool.Changes.Length > 0;
    private bool TrimFinished()
    {
        string? oldest = null;
        foreach (var pair in _tools)
        {
            if (Retain(pair.Value)) continue;
            oldest = pair.Key;
            break;
        }
        if (oldest is null) return false;
        _tools.Remove(oldest);
        string? process = null;
        foreach (var pair in _processes)
            if (pair.Value.ToolId == oldest) { process = pair.Key; break; }
        if (process is not null) _processes.Remove(process);
        return true;
    }
    private void RememberStream(string key, string value)
    {
        if (!_messages.ContainsKey(key) && _messages.Count >= 1000)
        {
            string? oldest = null;
            foreach (var existing in _messages.Keys)
            {
                oldest = existing;
                break;
            }
            if (oldest is not null) _messages.Remove(oldest);
        }
        _messages[key] = value;
    }
    private void MarkCompleted(string key)
    {
        _messages.Remove(key);
        if (!_completedMessages.Add(key)) return;
        _completedOrder.Enqueue(key);
        while (_completedOrder.Count > 1000)
            _completedMessages.Remove(_completedOrder.Dequeue());
    }
    private void ExecDelta(JsonElement p)
    {
        var process = Text(p, "processId");
        if (process.Length == 0) return;
        var piece = Decode(Text(p, "deltaBase64"));
        if (piece.Length == 0) return;
        if (!_processes.TryGetValue(process, out var located) || !_tools.TryGetValue(located.ToolId, out var tool) || tool.Status is not ("in_progress" or "pending"))
        {
            if (!_processes.ContainsKey(process))
                _processBuffers[process] = Clip(CommandPresentation.Redact((_processBuffers.GetValueOrDefault(process) ?? "") + piece), 32000);
            return;
        }
        Tool(located.Thread, tool with { Output = Clip(CommandPresentation.Redact(tool.Output + piece), 32000) });
    }
    private void IndexProcess(string thread, JsonElement item, string toolId)
    {
        var process = Text(item, "processId");
        if (process.Length == 0) process = Text(item, "process_id");
        if (process.Length == 0) return;
        _processes[process] = (thread, toolId);
        if (!_processBuffers.TryGetValue(process, out var buffered) || buffered.Length == 0 || !_tools.TryGetValue(toolId, out var tool) || tool.Status is not ("in_progress" or "pending")) return;
        _processBuffers.Remove(process);
        Tool(thread, tool with { Output = Clip(CommandPresentation.Redact(tool.Output + buffered), 32000) });
    }
    private void ApplyTurnSnapshot(string thread, JsonElement turn, Action<string> delta, bool includeCollaboration)
    {
        if (!turn.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return;
        var collaboration = new List<JsonElement>();
        foreach (var item in items.EnumerateArray())
        {
            var itemType = Text(item, "type");
            if (itemType == "collabAgentToolCall")
            {
                if (includeCollaboration) collaboration.Add(item.Clone());
                continue;
            }
            if (itemType is "" or "userMessage" or "reasoning" or "plan" or "hookPrompt" or "subAgentActivity") continue;
            if (itemType == "agentMessage" && thread == ParentThread) continue;
            if (itemType == "agentMessage")
            {
                var key = thread + ":" + Text(item, "id");
                if (_completedMessages.Contains(key)) continue;
                var updated = _children.SetFinalOutput(thread, Text(item, "text"));
                if (updated is not null) Report(updated);
                MarkCompleted(key);
                continue;
            }
            ApplyObservedItem(thread, item, false, delta);
        }
        foreach (var item in collaboration) ApplyObservedItem(thread, item, false, delta);
    }
    private static bool TurnItemsReady(JsonElement turn)
    {
        if (!turn.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return false;
        if (!turn.TryGetProperty("itemsView", out var view)) return true;
        return view.ValueKind == JsonValueKind.String && view.GetString() is "full" or "";
    }
    private void ApplyObservedItem(string thread, JsonElement item, bool started, Action<string> delta)
    {
        var type = Text(item, "type");
        if (type == "subAgentActivity") { SubagentActivity(thread, item, started); return; }
        if (type == "collabAgentToolCall") { Collaboration(thread, item, started); return; }
        var child = thread != ParentThread;
        if (type == "agentMessage" && !started)
        {
            var key = thread + ":" + Text(item, "id"); var full = Text(item, "text"); var prior = _messages.GetValueOrDefault(key, "");
            if (_completedMessages.Contains(key)) return;
            if (full.StartsWith(prior, StringComparison.Ordinal)) Message(thread, full[prior.Length..], false, delta, Text(item, "id"));
            if (child && full.Length > 0)
            {
                var updated = _children.SetFinalOutput(thread, full);
                if (updated is not null) Report(updated);
            }
            MarkCompleted(key); return;
        }
        if (type is "userMessage" or "reasoning" or "plan" or "hookPrompt" or "agentMessage") return;
        var id = thread + ":" + Text(item, "id");
        var status = Text(item, "status") switch { "completed" => "completed", "failed" => "failed", "declined" => "blocked", "interrupted" => "unconfirmed", _ => "in_progress" };
        var previous = _tools.GetValueOrDefault(id);
        if (type == "commandExecution")
        {
            var pending = "";
            if (_itemBuffers.TryGetValue(id, out var buffered)) { pending = buffered; _itemBuffers.Remove(id); }
            var prior = (previous?.Output ?? "") + pending;
            var presented = CommandPresentation.Describe(item, status, prior.Length == 0 ? null : prior);
            Tool(thread, new(id, presented.Title, CommandKind(item), presented.Status, Clip(presented.Input, 2000), [], presented.Output, presented.Diagnostics));
            IndexProcess(thread, item, id);
            return;
        }
        if (type == "fileChange")
        {
            CursorFileChange[] changes = [];
            if (item.TryGetProperty("changes", out var changeItems) && changeItems.ValueKind == JsonValueKind.Array)
                changes = changeItems.EnumerateArray().Take(64).Select(c => new CursorFileChange(
                    Path.IsPathFullyQualified(Text(c, "path")) ? Text(c, "path") : Path.GetFullPath(Path.Combine(workspace, Text(c, "path"))), null, "", Text(c, "diff"))).ToArray();
            var output = previous?.Output ?? "";
            if (_itemBuffers.TryGetValue(id, out var pending)) { output += pending; _itemBuffers.Remove(id); }
            Tool(thread, new(id, "Edit " + string.Join(", ", changes.Select(c => Path.GetFileName(c.Path))), "edit", status, "", changes, Clip(CommandPresentation.Redact(output), 32000)));
            return;
        }
        if (type == "webSearch")
        {
            Tool(thread, new(id, "Web search", "fetch", status, Clip(CommandPresentation.Redact(item.GetRawText()), 32000), [], previous?.Output ?? ""));
            return;
        }
        if (type == "mcpToolCall") { MapMcp(thread, id, item, status, previous); return; }
        var name = Text(item, "tool");
        if (name.Length == 0) name = Text(item, "name");
        if (type is "dynamicToolCall" or "functionCallOutput" && name is "exec" or "wait") { MapCodeMode(thread, id, item, name, status); return; }
        if (type == "dynamicToolCall")
        {
            var body = ItemBody(item);
            var output = body.Length > 0 ? body : previous?.Output ?? "";
            Tool(thread, new(id, name.Length == 0 ? "dynamicToolCall" : name, "other", status, Clip(CommandPresentation.Redact(Arguments(item)), 2000), [], Clip(CommandPresentation.Redact(output), 32000), Clip(CommandPresentation.Redact(item.GetRawText()), 4000)));
            return;
        }
        Diagnostic(thread, id, item, type, status);
    }
    private void MapCodeMode(string thread, string id, JsonElement item, string name, string status)
    {
        var body = ItemBody(item);
        var exit = Number(item, "exitCode") ?? Number(item, "exit_code");
        var mapped = CommandPresentation.CodeActivityStatus(status, body, exit);
        var cell = CommandPresentation.CellId(body);
        var toolId = id;
        if (cell is { Length: > 0 })
        {
            var cellKey = thread + ":" + cell;
            var closing = mapped is "failed" or "unconfirmed";
            if (closing && !_tools.ContainsKey(id) && _cells.TryGetValue(cellKey, out var existing)) toolId = existing;
            else if (!_cells.ContainsKey(cellKey)) _cells[cellKey] = id;
        }
        var running = mapped == "in_progress" && CommandPresentation.RunningCellNotice(body);
        var output = running ? "" : body;
        if (exit is int code && mapped == "failed")
            output = "Command failed — exit " + code + (output.Length == 0 ? "" : "\n" + output);
        else if (mapped == "failed" && output.Length == 0) output = "Command failed";
        else if (mapped == "unconfirmed" && output.Length == 0) output = "Cell ended without a confirmed result";
        var title = cell is { Length: > 0 } ? name + " · cell " + cell : name;
        Tool(thread, new(toolId, title, "execute", mapped, Clip(CommandPresentation.Redact(Arguments(item)), 2000), [], Clip(CommandPresentation.Redact(output), 32000), Clip(CommandPresentation.Redact(item.ValueKind == JsonValueKind.Object ? item.GetRawText() : ""), 4000)));
    }
    private void MapMcp(string thread, string id, JsonElement item, string status, CursorToolActivity? previous)
    {
        var output = previous?.Output ?? "";
        if (item.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var message = Text(error, "message");
            if (message.Length > 0) output = message;
            if (status == "completed") status = "failed";
        }
        else
        {
            var body = ItemBody(item);
            if (body.Length > 0) output = body;
        }
        var name = Text(item, "tool");
        var server = Text(item, "server");
        var title = name.Length == 0 ? "MCP tool" : server.Length == 0 ? name : server + " · " + name;
        Tool(thread, new(id, Clip(title, 240), "other", status, Clip(CommandPresentation.Redact(Arguments(item)), 2000), [], Clip(CommandPresentation.Redact(output), 32000), Clip(CommandPresentation.Redact(item.GetRawText()), 4000)));
    }
    private void Diagnostic(string thread, string id, JsonElement item, string type, string status)
    {
        var shown = status == "completed" ? "unconfirmed" : status;
        var label = type.Length == 0 ? "unrecognized" : type;
        Tool(thread, new(id, "Unrecognized Codex event · " + Clip(label, 80), "other", shown, "", [],
            "Unrecognized Codex event (" + label + ") was not mapped as a completed tool.",
            Clip(CommandPresentation.Redact(item.ValueKind == JsonValueKind.Object ? item.GetRawText() : ""), 4000)));
    }
    private static string ItemBody(JsonElement item)
    {
        var parts = new List<string>();
        void Add(string value) { if (value.Length > 0) parts.Add(value); }
        if (item.TryGetProperty("contentItems", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var part in items.EnumerateArray()) Add(Text(part, "text"));
        if (item.TryGetProperty("output", out var output))
        {
            if (output.ValueKind == JsonValueKind.String) Add(output.GetString() ?? "");
            else if (output.ValueKind == JsonValueKind.Array)
                foreach (var part in output.EnumerateArray()) Add(Text(part, "text"));
        }
        if (item.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object && result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var part in content.EnumerateArray()) Add(Text(part, "text"));
        Add(Text(item, "aggregatedOutput"));
        Add(Text(item, "stdout"));
        Add(Text(item, "stderr"));
        return string.Join("\n", parts);
    }
    private static string Arguments(JsonElement item)
    {
        if (!item.TryGetProperty("arguments", out var arguments)) return "";
        return arguments.ValueKind == JsonValueKind.String ? arguments.GetString() ?? "" : arguments.GetRawText();
    }
    private static int? Number(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number) return null;
        return value.TryGetInt32(out var number) ? number : null;
    }
    private static string Decode(string value)
    {
        if (value.Length == 0) return "";
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
        catch (FormatException) { return ""; }
    }
    private void Report(CursorSubagent child, CursorToolActivity? tool = null) => activity?.Invoke(new("subagent", Subagent: child, Tool: tool));
    public CursorPermissionRequest Permission(JsonElement p, bool file)
    {
        lock (_sync)
        {
            var thread = Text(p, "threadId"); var id = thread + ":" + Text(p, "itemId");
            var tool = _tools.GetValueOrDefault(id) ?? new CursorToolActivity(id, file ? "Edit files" : Text(p, "command"), file ? "edit" : "execute", "pending", "", []);
            var title = file ? tool.Title : Text(p, "kind") == "writeStdin" ? "Write to running command" : Text(p, "command") is { Length: > 0 } command ? command : tool.Title;
            if (p.TryGetProperty("networkApprovalContext", out var network) && network.ValueKind == JsonValueKind.Object) title = "Network access · " + Text(network, "host");
            tool = tool with { Title = Clip(title, 240), Input = Clip(p.GetRawText(), 32000), Status = "pending" };
            Remember(tool);
            var child = thread == ParentThread ? null : _children.Get(thread);
            if (child is not null) Report(_children.SetWork(thread, "Waiting for your approval · " + tool.Title));
            var choices = new List<CursorPermissionOption>();
            var offered = p.TryGetProperty("availableDecisions", out var available) && available.ValueKind == JsonValueKind.Array
                ? available.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()).ToArray() : new[] { "accept", "decline" };
            if (offered.Contains("accept")) choices.Add(new("accept", "Allow once", "allow_once"));
            if (offered.Contains("decline")) choices.Add(new("decline", "Reject", "reject_once"));
            return new(tool, choices.ToArray(), child);
        }
    }
    private void ReopenSettler() { if (_settled.Task.IsCompleted) _settled = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    private static string CommandKind(JsonElement item)
    {
        if (!item.TryGetProperty("commandActions", out var actions) || actions.ValueKind != JsonValueKind.Array || actions.GetArrayLength() == 0) return "execute";
        var types = actions.EnumerateArray().Select(a => Text(a, "type")).ToArray();
        return types.All(t => t is "read" or "listFiles") ? "read" : types.All(t => t is "read" or "listFiles" or "search") ? "search" : "execute";
    }
    private static string Clip(string value, int limit) => value.Length <= limit ? value : value[..limit];
}
