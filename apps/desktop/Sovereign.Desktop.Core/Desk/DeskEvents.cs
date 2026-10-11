namespace Sovereign.Desktop.Core.Desk;

public sealed record StateChange(JobState From, JobState To, string Reason, Guid? RecoveryReceiptId);
public sealed record StateEvent(int SchemaVersion, Guid EventId, Guid RunId, long Sequence, DateTimeOffset Timestamp,
    string Kind, int PayloadVersion, Guid? OperationId, StateChange Payload);

/// <summary>Consumer sequencing only. Persistence-before-notification is enforced by the store, not this projection.</summary>
public sealed class EventProjection(Guid runId, JobState initialState = JobState.Queued)
{
    private readonly Dictionary<Guid, StateEvent> _seen = [];
    public long LastSequence { get; private set; }
    public JobState State { get; private set; } = initialState;

    public bool Apply(StateEvent value)
    {
        if (value.RunId != runId || value.EventId == Guid.Empty || value.SchemaVersion != 1 || value.PayloadVersion != 1 ||
            value.Kind != "state_changed" || value.Timestamp.Offset != TimeSpan.Zero || value.Sequence < 1)
            throw new DeskContractException("INVALID_INPUT", "Invalid event identity or version.");
        if (_seen.TryGetValue(value.EventId, out var previous))
        {
            if (previous != value) throw new DeskContractException("INVALID_INPUT", "Conflicting duplicate event.");
            return false;
        }
        if (value.Sequence != LastSequence + 1 || value.Payload.From != State)
            throw new DeskContractException("INVALID_INPUT", "Event gap or conflicting state; replay from last committed sequence.");
        DeskStates.RequireTransition(State, value.Payload.To, value.Payload.RecoveryReceiptId);
        _seen.Add(value.EventId, value); LastSequence = value.Sequence; State = value.Payload.To;
        return true;
    }

    public static IEnumerable<StateEvent> Replay(IEnumerable<StateEvent> committed, long afterSequence)
    {
        if (afterSequence < 0) throw new DeskContractException("INVALID_INPUT", "Negative event cursor.");
        return committed.Where(e => e.Sequence > afterSequence).OrderBy(e => e.Sequence);
    }
}
