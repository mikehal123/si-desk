using System.Text.Json;

namespace Sovereign.Desktop.Core.Desk;

public sealed record InvestigationModelOperation(int SchemaVersion, string Stage, Guid OperationId, Guid RunId,
    Guid ReservationId, string ScopeSha256, Guid ApprovalId, Guid ResultCommitOperationId,
    DraftModelRequest ModelRequest, BudgetReservation Reservation);
public sealed record InvestigationModelContext(RunRecord Run, InvestigationModelOperation Model);
public sealed record InvestigationModelResult(DraftArtifactRef Artifact, UsageSummary Usage, Guid DispatchOperationId,
    Guid ReservationId, string ScopeSha256, string State, DateTimeOffset CreatedAt, DateTimeOffset DispatchedAt,
    DateTimeOffset SettledAt, JsonElement StageResult);

/// <summary>Validates the durable model capture. Only the store can authorize or settle it.</summary>
public static class InvestigationModelContract
{
    public const string WorkflowId = "bounded_investigation";
    public const string WorkflowVersion = "1.0.0";
    public const int MaxStageOutputTokens = 2048;

    public static JsonElement BrokerParameters(InvestigationModelOperation model, Guid nonce) =>
        JsonSerializer.SerializeToElement(new
        {
            session_nonce = nonce, run_id = model.RunId, operation_id = model.OperationId,
            reservation_id = model.ReservationId, scope_sha256 = model.ScopeSha256, approval_id = model.ApprovalId,
            provider_route = model.ModelRequest.ProviderRoute, output_schema_id = model.ModelRequest.OutputSchemaId,
            messages = model.ModelRequest.Messages, max_output_tokens = model.ModelRequest.MaxOutputTokens
        }, DeskJson.Options);

    public static void ValidateCapture(InvestigationModelContext captured, Guid runId, Guid operationId)
    {
        var run = captured.Run; var model = captured.Model; var reserve = model.Reservation; var request = model.ModelRequest;
        if (run.SchemaVersion != 1 || run.RunId != runId || run.CaseId == Guid.Empty || run.InputRevisionId == Guid.Empty ||
            run.WorkflowId != WorkflowId || run.WorkflowVersion != WorkflowVersion || model.SchemaVersion != 1 ||
            model.RunId != runId || model.OperationId != operationId || operationId == Guid.Empty ||
            model.ResultCommitOperationId == Guid.Empty || model.ResultCommitOperationId == operationId ||
            model.ReservationId == Guid.Empty || model.ApprovalId == Guid.Empty || model.ApprovalId != run.ApprovalId ||
            model.ScopeSha256 != run.PlanSha256 || model.ScopeSha256.Length != 64 ||
            model.ScopeSha256.Any(value => !char.IsAsciiHexDigitLower(value)) ||
            model.Stage is not ("extraction" or "challenge" or "synthesis") ||
            !run.ToolVersions.TryGetValue("broker.model_generate", out var version) || version != "1.0.0" ||
            request.ProviderRoute != run.ProviderRoute || request.OutputSchemaId != DraftContract.OutputSchemaId ||
            request.MaxOutputTokens is < 1 or > MaxStageOutputTokens || request.MaxOutputTokens > run.Budget.MaxOutputTokens ||
            request.Messages.Length is < 1 or > 32 ||
            request.Messages.Any(message => message.Role is not ("system" or "user" or "assistant") || string.IsNullOrWhiteSpace(message.Content)) ||
            reserve.SchemaVersion != 1 || reserve.OperationId != operationId || reserve.RunId != runId ||
            reserve.ReservationId != model.ReservationId || reserve.Type != "model_call" || reserve.MaxUnits != 1)
            throw Denied();
        DeskAuthority.ValidateBudget(run.Budget);
    }

    public static void RequireDispatchable(InvestigationModelContext captured)
    {
        if (captured.Run.State is JobState.Cancelled or JobState.Cancelling)
            throw new DeskContractException("CANCELLED", "The saved investigation is cancelled.");
        if (captured.Run.State != JobState.Running) throw Denied();
        if (captured.Model.Reservation.State != OperationState.Reserved || captured.Model.Reservation.SettledAt is not null)
            throw new DeskContractException("OUTCOME_UNKNOWN", "This model operation has already been dispatched. It cannot be sent again.");
    }

    private static DeskContractException Denied() => new("PERMISSION_DENIED", "The model request does not match its saved investigation authority.");
}
