namespace Sovereign.Desktop.Core.Desk;

public sealed record CollectedInvestigationSource(Guid EvidenceRevisionId, Guid? OriginalArtifactId,
    Guid? NormalizedArtifactId, string Locator, string ReadState, string[] QualityFlags);
public sealed record InvestigationSourceCollection(CollectedInvestigationSource[] Sources, Guid[] OperationIds);
