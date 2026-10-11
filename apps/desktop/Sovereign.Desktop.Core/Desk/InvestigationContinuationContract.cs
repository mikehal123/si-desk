using System.Text.Json;

namespace Sovereign.Desktop.Core.Desk;

public sealed record ReviewedInvestigationPlan(bool Reviewed, string[] Questions, string[] Tools, string[] StopConditions);
public sealed record InvestigationStageOperation(string Stage, Guid DispatchOperationId, OperationState State, int ReservedOutputTokens);
public sealed record InvestigationReservedUnits(int ModelCalls, int SourceCalls, int OutputTokens);
public sealed record InvestigationContinuation(RunRecord Run, ReviewedInvestigationPlan? ReviewedPlan,
    string[] CompletedStages, InvestigationStageOperation[] StageOperations, InvestigationReservedUnits ReservedUnits,
    int RemainingWallSeconds, bool CanResume, string? RefusalCode);

/// <summary>The store retains approval authority; these checks prevent the host from displaying inconsistent continuation receipts.</summary>
public static class InvestigationContinuationContract
{
    private static readonly string[] Stages = ["intake", "plan", "evidence", "extraction", "calculation", "challenge", "synthesis", "review-ready"];

    public static void Validate(InvestigationContinuation saved, Guid runId)
    {
        var run = saved.Run; var units = saved.ReservedUnits;
        if (run.RunId != runId || runId == Guid.Empty || run.WorkflowId != InvestigationModelContract.WorkflowId ||
            run.WorkflowVersion != InvestigationModelContract.WorkflowVersion || run.ApprovalId == Guid.Empty ||
            saved.RemainingWallSeconds < 0 || saved.RemainingWallSeconds > run.Budget.MaxWallSeconds ||
            units.ModelCalls < 0 || units.ModelCalls > run.Budget.MaxModelCalls ||
            units.SourceCalls < 0 || units.SourceCalls > run.Budget.MaxSourceCalls ||
            units.OutputTokens < 0 || units.OutputTokens > run.Budget.MaxOutputTokens ||
            saved.CompletedStages.Distinct().Count() != saved.CompletedStages.Length ||
            saved.CompletedStages.Any(stage => !Stages.Contains(stage)) ||
            saved.StageOperations.Select(op => op.Stage).Distinct().Count() != saved.StageOperations.Length ||
            saved.StageOperations.Select(op => op.DispatchOperationId).Distinct().Count() != saved.StageOperations.Length ||
            saved.StageOperations.Any(op => op.Stage is not ("extraction" or "challenge" or "synthesis") ||
                op.DispatchOperationId == Guid.Empty || op.ReservedOutputTokens is < 1 or > InvestigationModelContract.MaxStageOutputTokens) ||
            saved.CanResume != (saved.RefusalCode is null) ||
            (saved.RefusalCode is not null && !DeskProtocol.ErrorCodes.Contains(saved.RefusalCode)))
            throw Invalid();
        DeskAuthority.ValidateBudget(run.Budget);
        if (saved.ReviewedPlan is { } plan && (!plan.Reviewed || plan.Questions.Length == 0 || plan.Tools.Length == 0 ||
            plan.StopConditions.Length == 0 || plan.Questions.Concat(plan.Tools).Concat(plan.StopConditions).Any(string.IsNullOrWhiteSpace)))
            throw Invalid();
        if (saved.CanResume && (run.State != JobState.Interrupted || saved.RemainingWallSeconds == 0 || saved.ReviewedPlan is null ||
            saved.StageOperations.Any(op => op.State is not (OperationState.Reserved or OperationState.Succeeded))))
            throw Invalid();
    }

    public static void ValidateResumed(InvestigationContinuation before, InvestigationContinuation after)
    {
        Validate(before, before.Run.RunId); Validate(after, before.Run.RunId);
        var old = before.Run; var current = after.Run;
        if (!before.CanResume || current.State != JobState.Running || current.ApprovalId == old.ApprovalId ||
            current.CaseId != old.CaseId || current.InputRevisionId != old.InputRevisionId || current.PlanSha256 != old.PlanSha256 ||
            current.ProviderRoute != old.ProviderRoute || current.Budget != old.Budget || current.StartedAt != old.StartedAt ||
            current.CreatedAt != old.CreatedAt || current.ComponentBuildId != old.ComponentBuildId ||
            !Same(old.ToolVersions, current.ToolVersions) || !Same(before.ReviewedPlan, after.ReviewedPlan) ||
            !before.CompletedStages.SequenceEqual(after.CompletedStages) || !before.StageOperations.SequenceEqual(after.StageOperations) ||
            before.ReservedUnits != after.ReservedUnits || after.RemainingWallSeconds > before.RemainingWallSeconds)
            throw new DeskContractException("PERMISSION_DENIED", "Continuation changed the reviewed plan, captured operations, or remaining budget.");
    }

    private static bool Same<T>(T left, T right) => JsonElement.DeepEquals(
        JsonSerializer.SerializeToElement(left, DeskJson.Options), JsonSerializer.SerializeToElement(right, DeskJson.Options));
    private static DeskContractException Invalid() => new("OUTPUT_INVALID", "The saved continuation receipt is inconsistent.");
}
