namespace Sovereign.Desktop.Core.Desk;

public sealed record DispatchIntent(Guid SessionNonce, Guid RunId, Guid OperationId, Guid ReservationId,
    Guid ApprovalId, string ScopeSha256, string CapabilityId);
public sealed record DispatchAcknowledgement(Guid SessionNonce, Guid RunId, Guid OperationId, Guid ReservationId,
    string ScopeSha256, bool FirstDispatch, OperationReceipt Receipt);

/// <summary>Implemented by the single-writer store. Values must come from the current profile, never request JSON.</summary>
public interface IDeskAuthorityReader
{
    RunRecord? FindRun(Guid id);
    ApprovalReceipt? FindApproval(Guid id);
    BudgetReservation? FindReservation(Guid id);
    OperationReceipt? FindOperation(Guid runId, Guid operationId);
    bool IsCapabilityApproved(Guid runId, string scopeSha256, string capabilityId);
    bool IsProvenanceLinked(Guid caseRevisionId, string kind, Guid referenceId);
}

public static class DispatchContract
{
    // The store must invoke this inside the same transaction as Reserved -> Dispatched.
    // Pure validation does not persist, reserve budget, or grant permission to call a provider.
    public static OperationReceipt ValidateStoredIntent(DispatchIntent intent, Guid sessionNonce,
        IDeskAuthorityReader store, DateTimeOffset now)
    {
        if (sessionNonce == Guid.Empty || intent.SessionNonce != sessionNonce || intent.RunId == Guid.Empty ||
            intent.OperationId == Guid.Empty || intent.ReservationId == Guid.Empty || intent.ApprovalId == Guid.Empty ||
            !DeskAuthority.Capabilities.TryGetValue(intent.CapabilityId, out var capability) || !capability.External)
            throw Denied();
        var run = store.FindRun(intent.RunId) ?? throw Denied();
        var approval = store.FindApproval(intent.ApprovalId) ?? throw Denied();
        var reservation = store.FindReservation(intent.ReservationId) ?? throw Denied();
        var operation = store.FindOperation(intent.RunId, intent.OperationId) ?? throw Denied();
        if (run.SchemaVersion != 1 || operation.SchemaVersion != 1 || reservation.SchemaVersion != 1 ||
            reservation.Type != capability.Effect || reservation.MaxUnits < 1 || reservation.SettledAt is not null ||
            operation.DispatchedAt is not null || operation.SettledAt is not null ||
            run.RunId != intent.RunId || run.State != JobState.Running || run.ApprovalId != intent.ApprovalId ||
            approval.ApprovalId != intent.ApprovalId || reservation.ReservationId != intent.ReservationId ||
            reservation.OperationId != intent.OperationId || reservation.RunId != intent.RunId ||
            reservation.State != OperationState.Reserved || operation.State != OperationState.Reserved ||
            operation.OperationId != intent.OperationId || operation.RunId != intent.RunId ||
            operation.ReservationId != intent.ReservationId || operation.ScopeSha256 != intent.ScopeSha256 ||
            !store.IsCapabilityApproved(intent.RunId, intent.ScopeSha256, intent.CapabilityId)) throw Denied();
        DeskAuthority.ValidateApproval(approval, run.PlanSha256, now);
        DeskAuthority.ValidateBudget(run.Budget);
        return operation;
    }

    public static void ValidateAcknowledgement(DispatchIntent intent, DispatchAcknowledgement ack)
    {
        var receipt = ack.Receipt;
        if (!ack.FirstDispatch || ack.SessionNonce != intent.SessionNonce || ack.RunId != intent.RunId ||
            ack.OperationId != intent.OperationId || ack.ReservationId != intent.ReservationId || ack.ScopeSha256 != intent.ScopeSha256 ||
            receipt.SchemaVersion != 1 || receipt.RunId != intent.RunId || receipt.OperationId != intent.OperationId ||
            receipt.ReservationId != intent.ReservationId || receipt.ScopeSha256 != intent.ScopeSha256 ||
            receipt.State != OperationState.Dispatched || receipt.DispatchedAt is null || receipt.SettledAt is not null)
            throw Denied();
    }
    private static DeskContractException Denied() => new("PERMISSION_DENIED", "Stored dispatch authority or acknowledgement does not match.");
}

/// <summary>Per-session replay guard. Burn a matching committed acknowledgement before external I/O.</summary>
public sealed class DispatchSession(Guid sessionNonce)
{
    private readonly HashSet<Guid> _consumed = [];
    private readonly HashSet<Guid> _cancelledRuns = [];
    private readonly object _gate = new();
    public void CancelRun(Guid runId) { lock (_gate) _cancelledRuns.Add(runId); }
    public bool TryConsume(DispatchIntent intent, DispatchAcknowledgement acknowledgement, bool cancelled)
    {
        lock (_gate)
        {
            if (sessionNonce == Guid.Empty || intent.SessionNonce != sessionNonce || cancelled || _cancelledRuns.Contains(intent.RunId)) return false;
            DispatchContract.ValidateAcknowledgement(intent, acknowledgement);
            return _consumed.Add(intent.OperationId);
        }
    }
}
