using Sovereign.Desktop.Core;

namespace Sovereign.Desktop.Client.Adapters;

public sealed class EventInbox
{
    private readonly List<ClientEvent> _events = [];
    private readonly HashSet<string> _ids = [];
    private string? _lastCursor;
    private bool _gap;

    public static EventInbox From(IEnumerable<ClientEvent> events)
    {
        var inbox = new EventInbox();
        foreach (var e in events.OrderBy(e => e.Cursor, StringComparer.Ordinal))
        {
            inbox.Accept(e);
        }

        return inbox;
    }

    public bool Gap => _gap;
    public string? LastCursor => _lastCursor;

    public void Accept(ClientEvent incoming)
    {
        if (!_ids.Add(incoming.Id))
        {
            return;
        }

        if (incoming.GapBefore || (_lastCursor is not null && !IsSuccessor(_lastCursor, incoming.Cursor)))
        {
            _gap = true;
        }

        _events.Add(incoming);
        _lastCursor = incoming.Cursor;
    }

    public IReadOnlyList<ClientEvent> Snapshot() => _events.ToArray();

    /// <summary>
    /// Discard mixed projections after a cursor gap. LastCursor is cleared so the
    /// next host read starts a full resynchronization rather than an incremental tail.
    /// </summary>
    public void ResetForResync()
    {
        _events.Clear();
        _ids.Clear();
        _lastCursor = null;
        _gap = false;
    }

    /// <summary>
    /// After a gap is handled, continue from this sequence so the following
    /// <c>events.list</c> is not stuck reporting the same hole.
    /// </summary>
    public void RememberCursor(string cursor)
    {
        _lastCursor = cursor;
    }

    public IReadOnlyList<ClientEvent> Resume(string? cursor)
    {
        if (_gap && cursor is not null && cursor != _lastCursor)
        {
            return
            [
                new ClientEvent(
                    "resync-required",
                    cursor,
                    DateTimeOffset.UtcNow,
                    "resync",
                    "Update cursor gap detected. State is not current; resync required.",
                    null,
                    null,
                    true)
            ];
        }

        if (cursor is null)
        {
            return Snapshot();
        }

        return _events.Where(e => string.CompareOrdinal(e.Cursor, cursor) > 0).ToArray();
    }

    private static bool IsSuccessor(string previous, string next) =>
        string.CompareOrdinal(next, previous) > 0;
}

public sealed class OperationGuard
{
    private readonly Dictionary<string, OperationResult> _completed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inflight = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public bool TryBegin(string operation, string idempotencyKey, out OperationResult? existing)
    {
        lock (_gate)
        {
            if (_completed.TryGetValue(idempotencyKey, out existing))
            {
                return false;
            }

            if (!_inflight.Add(idempotencyKey))
            {
                existing = new OperationResult(
                    operation,
                    idempotencyKey,
                    OperationStatus.Unknown,
                    null,
                    "Duplicate in-flight request reused the same idempotency key. A second unrelated intent was not created.",
                    false);
                return false;
            }

            existing = null;
            return true;
        }
    }

    public void Complete(string idempotencyKey, OperationResult result)
    {
        lock (_gate)
        {
            _completed[idempotencyKey] = result;
        }
    }

    public void End(string idempotencyKey)
    {
        lock (_gate)
        {
            _inflight.Remove(idempotencyKey);
        }
    }
}
