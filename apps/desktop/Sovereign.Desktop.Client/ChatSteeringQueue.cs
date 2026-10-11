namespace Sovereign.Desktop.Client;

/// <summary>A captured instruction never reads the mutable composer again.</summary>
public sealed record SavedChatInstruction(string Id, string Text, string Model, ChatAttachment[] Attachments, bool IsSteering = true, bool MayHaveBeenSent = false);

/// <summary>Conversation-local ordered work. Loading, stopping, and failure require an explicit send.</summary>
public sealed class ChatSteeringQueue
{
    private readonly List<SavedChatInstruction> _pending = [];
    private readonly HashSet<string> _automatic = [];
    private readonly HashSet<string> _inFlight = [];

    public ChatSteeringQueue(IEnumerable<SavedChatInstruction>? restored = null)
    {
        foreach (var item in restored ?? []) _pending.Add(Capture(item));
    }

    public int Count => _pending.Count;
    public bool HasAutomaticPending => _pending.Any(p => _automatic.Contains(p.Id));
    public SavedChatInstruction[] Snapshot => _pending.Select(Capture).ToArray();
    public bool IsInFlight(string id) => _inFlight.Contains(id);
    public bool IsAutomatic(string id) => _automatic.Contains(id);

    /// <summary>An OpenAI turn accepted this instruction. It stays saved until the chat records it, and it must not start another turn.</summary>
    public bool KeepOutOfNextTurn(string id)
    {
        if (_inFlight.Contains(id) || !_pending.Any(p => p.Id == id)) return false;
        _automatic.Remove(id);
        return true;
    }

    /// <summary>The chat has recorded an accepted correction, so explicit send must not replay it.</summary>
    public bool DeliverToActiveTurn(string id)
    {
        if (_inFlight.Contains(id)) return false;
        _automatic.Remove(id);
        return _pending.RemoveAll(p => p.Id == id) > 0;
    }

    public void MarkMayHaveBeenSent(string id)
    {
        for (var i = 0; i < _pending.Count; i++)
            if (_pending[i].Id == id) _pending[i] = _pending[i] with { MayHaveBeenSent = true };
    }

    public SavedChatInstruction Accept(string text, string model, IEnumerable<ChatAttachment> attachments, bool isSteering)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(model)) throw new ArgumentException("An instruction needs text and a model.");
        var item = new SavedChatInstruction(Guid.NewGuid().ToString("N"), text, model, attachments.ToArray(), isSteering);
        _pending.Add(item); _automatic.Add(item.Id);
        return Capture(item);
    }

    // Combine messages already waiting for the same model, in their original order.
    // New messages arriving during that response form the next batch.
    public SavedChatInstruction[] BeginBatch()
    {
        if (_inFlight.Count > 0) throw new InvalidOperationException("The previous response has not finished.");
        var ready = _pending.Where(p => _automatic.Contains(p.Id)).ToArray();
        if (ready.Length == 0) return [];
        var batch = ready.TakeWhile(p => p.Model == ready[0].Model).ToArray();
        foreach (var item in batch) { _automatic.Remove(item.Id); _inFlight.Add(item.Id); }
        return batch.Select(Capture).ToArray();
    }

    public SavedChatInstruction[] MarkDispatched(IEnumerable<SavedChatInstruction> batch)
    {
        var ids = batch.Select(i => i.Id).ToHashSet();
        for (var i = 0; i < _pending.Count; i++)
            if (ids.Contains(_pending[i].Id)) _pending[i] = _pending[i] with { MayHaveBeenSent = true };
        return _pending.Where(p => ids.Contains(p.Id)).Select(Capture).ToArray();
    }

    public void CompleteBatch(IEnumerable<SavedChatInstruction> batch, bool delivered)
    {
        foreach (var item in batch)
        {
            _inFlight.Remove(item.Id);
            if (delivered) _pending.RemoveAll(p => p.Id == item.Id);
        }
        if (!delivered) Pause();
    }

    public void Pause() => _automatic.Clear();
    public void ResumeAll()
    {
        foreach (var item in _pending.Where(p => !_inFlight.Contains(p.Id))) _automatic.Add(item.Id);
    }
    public bool Remove(string id)
    {
        if (_inFlight.Contains(id)) return false;
        _automatic.Remove(id);
        return _pending.RemoveAll(p => p.Id == id) > 0;
    }

    private static SavedChatInstruction Capture(SavedChatInstruction item) => item with { Attachments = item.Attachments.ToArray() };
}
