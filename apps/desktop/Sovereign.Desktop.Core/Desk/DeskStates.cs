namespace Sovereign.Desktop.Core.Desk;

public enum DataState { NotFound, FailedToRead, LoadedEmpty, Stale, Loaded }
public enum JobState { Queued, Running, WaitingForApproval, Cancelling, Completed, Cancelled, Interrupted, Failed, BudgetExhausted, RegimeRefused }
public enum InquiryState { Unstarted, Investigating, Blocked, ReviewReady, Sufficient, Monitoring, Archived }
public enum DecisionState { Unassessed, NeedMoreEvidence, Watch, Conditional, Reject, HumanApprovedAction }
public enum OperationState { Reserved, Dispatched, Succeeded, Failed, Cancelled, OutcomeUnknown }

public static class DeskStates
{
    public static bool CanTransition(JobState from, JobState to, Guid? recoveryReceipt = null) => from switch
    {
        JobState.Queued => to is JobState.Running or JobState.Cancelled or JobState.Failed,
        JobState.Running => to is JobState.WaitingForApproval or JobState.Cancelling or JobState.Completed
            or JobState.Failed or JobState.BudgetExhausted or JobState.RegimeRefused or JobState.Interrupted,
        JobState.WaitingForApproval => to is JobState.Running or JobState.Cancelling or JobState.Cancelled or JobState.Interrupted,
        JobState.Cancelling => to is JobState.Cancelled or JobState.Interrupted or JobState.Failed,
        JobState.Interrupted => to == JobState.Queued && recoveryReceipt is { } id && id != Guid.Empty,
        _ => false
    };

    public static void RequireTransition(JobState from, JobState to, Guid? recoveryReceipt = null)
    {
        if (!CanTransition(from, to, recoveryReceipt))
            throw new DeskContractException("INVALID_INPUT", $"Invalid job transition: {from} to {to}.");
    }

    public static void RequireRevision(Guid current, Guid expected)
    {
        if (current == Guid.Empty || expected != current)
            throw new DeskContractException("INVALID_INPUT", "The case changed. Review the current revision before applying this change.");
    }
}

public sealed class DeskContractException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
