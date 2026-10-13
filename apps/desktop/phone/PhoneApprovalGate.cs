using Sovereign.Desktop.Client;

namespace Sovereign.Desktop.Phone;

public sealed record PhonePendingApproval(string Id, string Title, string Kind, string Input, bool Destructive, string Origin, string ChatHint);

public readonly record struct PhoneDecisionResult(bool Accepted, string? Error)
{
    public static PhoneDecisionResult Ok() => new(true, null);
    public static PhoneDecisionResult Fail(string error) => new(false, error);
}

/// <summary>The in-flight permission callback the desktop dialog and the phone both answer. First answer wins.</summary>
public sealed class PhoneApprovalGate
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Pending> _pending = new();

    public string Track(CursorPermissionRequest request, TaskCompletionSource<string?> completion, CancellationToken ct, string chatTitle)
    {
        var id = Guid.NewGuid().ToString("N");
        var pending = new Pending(request, completion, chatTitle);
        lock (_gate) _pending[id] = pending;
        if (ct.CanBeCanceled)
            ct.Register(() =>
            {
                lock (_gate) _pending.Remove(id);
                completion.TrySetCanceled(ct);
            });
        return id;
    }

    public void BindDesktopHide(string id, Action hide)
    {
        lock (_gate)
            if (_pending.TryGetValue(id, out var pending)) pending.HideDesktop = hide;
    }

    public void Release(string id)
    {
        lock (_gate) _pending.Remove(id);
    }

    public PhonePendingApproval[] Snapshot()
    {
        lock (_gate)
            return _pending.Select(pair => Describe(pair.Key, pair.Value)).ToArray();
    }

    public PhoneDecisionResult Decide(string id, bool allow, bool confirm)
    {
        Pending? pending;
        lock (_gate)
        {
            if (!_pending.TryGetValue(id, out pending)) return PhoneDecisionResult.Fail("That approval is no longer waiting.");
            if (pending.Completion.Task.IsCompleted)
            {
                _pending.Remove(id);
                return PhoneDecisionResult.Fail("That approval is no longer waiting.");
            }
            if (allow && IsDestructive(pending.Request.Tool) && !confirm) return PhoneDecisionResult.Fail("confirm_required");
            _pending.Remove(id);
        }
        var kind = allow ? "allow_once" : "reject_once";
        var option = pending.Request.Options.FirstOrDefault(item => item.Kind == kind);
        if (allow && option is null)
        {
            pending.Completion.TrySetResult(null);
            return PhoneDecisionResult.Fail("This action has no allow choice.");
        }
        if (!pending.Completion.TrySetResult(option?.Id))
            return PhoneDecisionResult.Fail("That approval is no longer waiting.");
        try { pending.HideDesktop?.Invoke(); } catch (Exception) { /* The desktop dialog may already be gone. */ }
        return PhoneDecisionResult.Ok();
    }

    public static bool IsDestructive(CursorToolActivity tool)
    {
        if (tool.Kind.Equals("edit", StringComparison.OrdinalIgnoreCase)
            || tool.Kind.Equals("execute", StringComparison.OrdinalIgnoreCase)
            || tool.Kind.Equals("write", StringComparison.OrdinalIgnoreCase)
            || tool.Kind.Equals("delete", StringComparison.OrdinalIgnoreCase))
            return true;
        if (ToolName(tool.Title) || ToolName(tool.Id)) return true;
        foreach (var line in tool.Input.Split('\n'))
            if (line.Trim().StartsWith("write ", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool ToolName(string value)
    {
        var token = value.Split(' ', '\n', '·', '.').FirstOrDefault()?.Trim() ?? "";
        return token is "terminal" or "create_file" or "edit_file" or "delete_file" or "remove_file" or "shell";
    }

    private static PhonePendingApproval Describe(string id, Pending pending)
    {
        var tool = pending.Request.Tool;
        var input = tool.Input.Length > 12000 ? tool.Input[..12000] + "\n[Display truncated — deny if details are insufficient]" : tool.Input;
        var origin = pending.Request.Subagent is { } child ? "Subagent · " + child.Name : "Agent";
        return new(id, tool.Title, tool.Kind, input, IsDestructive(tool), origin, pending.ChatTitle);
    }

    private sealed class Pending(CursorPermissionRequest request, TaskCompletionSource<string?> completion, string chatTitle)
    {
        public CursorPermissionRequest Request { get; } = request;
        public TaskCompletionSource<string?> Completion { get; } = completion;
        public string ChatTitle { get; } = chatTitle;
        public Action? HideDesktop { get; set; }
    }
}
