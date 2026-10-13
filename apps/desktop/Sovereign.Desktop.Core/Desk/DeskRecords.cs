namespace Sovereign.Desktop.Core.Desk;

public sealed record CaseRecord(int SchemaVersion, Guid CaseId, string Title, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, bool Archived, Guid CurrentRevisionId);
public sealed record CaseRevision(int SchemaVersion, Guid RevisionId, Guid CaseId, Guid? ParentRevisionId,
    Guid? OriginatingRunId, string Question, Guid? ContextRevisionId, Guid? MandateRevisionId,
    Guid[] QuestionIds, Guid[] ClaimIds, Guid[] EvidenceIds, Guid[] CalculationIds,
    InquiryState InquiryState, DecisionState ProposedDisposition, Guid ContentArtifactId,
    DateTimeOffset CreatedAt, DateTimeOffset? ReviewedAt);
public sealed record ProviderRoute(string ProviderId, string ModelId, string ConnectionRef);
public sealed record BudgetPolicy(int MaxModelCalls, int MaxSourceCalls, int MaxOutputTokens,
    int MaxWallSeconds, string? MonetaryLimit, string? Currency, string PricingBasis, bool UnknownCostAuthorized);
public sealed record UsageSummary(int ModelCalls, int SourceCalls, long InputTokens, long OutputTokens,
    string? ActualCost, string? Currency, bool CostUnknown);
public sealed record RunRecord(int SchemaVersion, Guid RunId, Guid CaseId, Guid InputRevisionId,
    string WorkflowId, string WorkflowVersion, string PlanSha256, Guid ApprovalId, ProviderRoute ProviderRoute,
    Dictionary<string, string> ToolVersions, string ComponentBuildId, BudgetPolicy Budget, JobState State,
    string? TerminalReason, long LastSequence, Guid? CheckpointArtifactId, Guid? OutputRevisionId,
    Guid? LinkedRunId, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, UsageSummary Usage);
public sealed record ArtifactRecord(int SchemaVersion, Guid ArtifactId, string Sha256, long Bytes,
    string MediaType, string RelativePath, string Producer, string Retention, string Provenance,
    DateTimeOffset CreatedAt);
public sealed record SourceRecord(int SchemaVersion, Guid SourceId, string? CanonicalUrl,
    string? DocumentId, string? Publisher, string? Author, string SourceType, string AccessPolicy, string RetentionPolicy);
public sealed record EvidenceRevision(int SchemaVersion, Guid EvidenceRevisionId, Guid SourceId,
    Guid? PreviousRevisionId, string Sha256, Guid? OriginalArtifactId, Guid? NormalizedArtifactId,
    string ExtractionVersion, DateTimeOffset RetrievedAt, DateTimeOffset? PublishedAt, DateTimeOffset? AsOf,
    bool PublicationUnknown, bool FreshnessUnknown, Guid? ProviderReceiptId, DataState ReadState,
    string[] QualityFlags, string[] ReplayLimits);
public sealed record EvidenceLocator(int? Page, string? Section, int? StartCharacter, int? EndCharacter);
public sealed record Citation(int SchemaVersion, Guid CitationId, Guid ClaimId, Guid EvidenceRevisionId,
    string ArtifactSha256, EvidenceLocator Locator, string Excerpt, string Relation);
public sealed record Claim(int SchemaVersion, Guid ClaimId, string Text, string Type, Guid[] CitationIds,
    string ConfidenceExplanation, Guid[] CounterClaimIds, string Status);
public sealed record Observation(int SchemaVersion, Guid ObservationId, string? DecimalValue, string? TextValue,
    string Units, string? Currency, string? Period, DateTimeOffset? AsOf, string AdjustmentBasis,
    Guid EvidenceRevisionId, EvidenceLocator Locator, string ExtractionMethod, string VerificationStatus);
public sealed record InquiryQuestion(int SchemaVersion, Guid QuestionId, string Text, string DecisionRelevance,
    Guid[] DependencyIds, string Status, string AcceptableEvidence, string StoppingCondition, string CostClass);
public sealed record ContextRevision(int SchemaVersion, Guid RevisionId, Guid? ParentRevisionId, string Owner,
    string Scope, string Horizon, string[] Constraints, Guid[] EvidenceIds, DateTimeOffset AsOf,
    DateTimeOffset? ExpiresAt, Guid? ApprovalId);
public sealed record CalculationReceipt(int SchemaVersion, Guid CalculationId, Guid RunId, string ToolId,
    string ToolVersion, Guid InputArtifactId, string InputSha256, Guid OutputArtifactId, string OutputSha256,
    string ComponentBuildId, string CodeSha256, DateTimeOffset StartedAt, DateTimeOffset FinishedAt,
    string Status, string Units, string Currency, Guid[] EvidenceIds, Guid[] AssumptionIds,
    string[] Warnings, string ReplaySha256);
public sealed record ApprovalReceipt(int SchemaVersion, Guid ApprovalId, string ActorId, string ActorKind,
    string ScopeSha256, DateTimeOffset ApprovedAt, DateTimeOffset? ExpiresAt);
public sealed record DecisionRevision(int SchemaVersion, Guid DecisionId, Guid RevisionId,
    Guid CaseRevisionId, DecisionState Disposition, string Rationale, Guid[] EvidenceIds,
    Guid[] CalculationIds, string ActorId, DateTimeOffset CreatedAt, DateTimeOffset? ReviewAt,
    DateTimeOffset? ExpiresAt, Guid ApprovalId);
public sealed record FollowupRevision(int SchemaVersion, Guid FollowupId, Guid RevisionId,
    Guid CaseRevisionId, string Trigger, string[] ApprovedSources, string[] ApprovedTools,
    string Frequency, string Timezone, DateTimeOffset NextDue, DateTimeOffset? LastChecked,
    DateTimeOffset? ExpiresAt, BudgetPolicy Budget, Guid ApprovalId, string State);
public sealed record BudgetReservation(int SchemaVersion, Guid ReservationId, Guid RunId, Guid OperationId,
    string Type, long MaxUnits, string? MaxCost, string? Currency, string? PricingVersion,
    OperationState State, DateTimeOffset CreatedAt, DateTimeOffset? SettledAt);
public sealed record OperationReceipt(int SchemaVersion, Guid OperationId, Guid RunId, Guid ReservationId,
    string ScopeSha256, OperationState State, DateTimeOffset CreatedAt, DateTimeOffset? DispatchedAt,
    DateTimeOffset? SettledAt, Guid? ResultArtifactId, string? ErrorCode, UsageSummary? Usage);
