namespace Sovereign.Desktop.Core;

public sealed record RuntimeIdentity(
    string BuildId,
    string ProtocolVersion,
    BackendType BackendType,
    IReadOnlyList<string> Capabilities,
    DateTimeOffset ObservationTimestamp,
    string IntegrationStatus);

public sealed record LabeledState<T>(T Value, string Label, string? Reason)
    where T : notnull;

public sealed record SystemHealth(
    LabeledState<EngineState> Engine,
    LabeledState<ConnectivityState> Provider,
    LabeledState<ConnectivityState> Broker,
    LabeledState<ReconciliationState> Reconciliation,
    LabeledState<AuthorizationState> Authorization,
    Presence<DateTimeOffset> NextCycle,
    IReadOnlyList<AttentionItem> UnresolvedIssues,
    DateTimeOffset ObservationTimestamp);

public sealed record AttentionItem(
    string Id,
    string Severity,
    string Summary,
    string? NextStep,
    string? RelatedDecisionId);

public sealed record AccountView(
    string AccountId,
    string Environment,
    DateTimeOffset ObservedAt,
    string SourceLabel);

public sealed record GateResult(
    string Name,
    GateVerdict Verdict,
    string Reason);

public sealed record DecisionRecord(
    string Id,
    DecisionAction Action,
    string Instrument,
    Presence<decimal> Quantity,
    IReadOnlyList<string> EvidenceIds,
    string AccountRevision,
    IReadOnlyList<string> Reasons,
    GateResult Admission,
    GateResult Risk,
    GateResult Execution,
    DateTimeOffset ObservedAt);

public sealed record ClientEvent(
    string Id,
    string Cursor,
    DateTimeOffset At,
    string Kind,
    string Summary,
    string? DecisionId,
    bool GapBefore);

public sealed record InquiryJob(
    string Id,
    string Title,
    string Status,
    string Horizon,
    Presence<string> Thesis,
    Presence<string> CounterThesis,
    Presence<string> Uncertainty,
    IReadOnlyList<string> EvidenceRefs,
    IReadOnlyList<string> ReviewTriggers,
    string? UnavailableReason,
    bool PreviewScenario);

public sealed record CapabilityItem(
    string Id,
    string Version,
    string Status,
    string Provider,
    string PermissionScope,
    IReadOnlyList<string> RequiredCapabilities,
    string AvailabilityReason,
    string CostStatus,
    string? LastOutcome,
    string CatalogStatus);

public sealed record EvaluationSummary(
    string CampaignId,
    Presence<int> SampleCount,
    Presence<string> Coverage,
    string PerformanceBasis,
    string BenchmarkAssumptions,
    string CostAssumptions,
    int MissedCount,
    int LateCount,
    int AbstainCount,
    int BlockedCount,
    string SoftwareResult,
    string OperationalReadiness,
    string InvestmentVerdict,
    string? PlotAbsenceReason);

public sealed record EvidenceDocument(
    string Id,
    string Title,
    string MediaType,
    string Text,
    IReadOnlyList<string> AllowedUriSchemes,
    IReadOnlyList<string> SourceUris);

public sealed record HomeSnapshot(
    RuntimeIdentity Runtime,
    SystemHealth Health,
    Presence<AccountView> Account,
    Presence<DecisionRecord> LatestDecision,
    IReadOnlyList<InquiryJob> Jobs,
    IReadOnlyList<AttentionItem> Attention);

public sealed record OperationRequest(
    string Operation,
    string IdempotencyKey,
    string? ExpectedRevision,
    IReadOnlyDictionary<string, string> Arguments);

public sealed record OperationResult(
    string Operation,
    string IdempotencyKey,
    OperationStatus Status,
    string? HostRequestId,
    string Detail,
    bool SimulatedPreviewAcknowledgement);
