using Sovereign.Desktop.Core;
using Sovereign.Desktop.Core.Drafts;

namespace Sovereign.Desktop.Client;

public interface IOperatorClient
{
    ApplicationMode Mode { get; }
    string ModeBanner { get; }
    string? ScenarioId { get; }

    Task<RuntimeIdentity> GetRuntimeAsync(CancellationToken cancellationToken = default);
    Task<SystemHealth> GetHealthAsync(CancellationToken cancellationToken = default);
    Task<Presence<AccountView>> GetAccountAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DecisionRecord>> ListDecisionsAsync(string? query = null, DecisionAction? action = null, CancellationToken cancellationToken = default);
    Task<DecisionRecord?> GetDecisionAsync(string id, CancellationToken cancellationToken = default);
    Task<EvidenceDocument?> GetEvidenceAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ClientEvent>> ListEventsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InquiryJob>> ListJobsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CapabilityItem>> ListCapabilitiesAsync(CancellationToken cancellationToken = default);
    Task<EvaluationSummary> GetEvaluationAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<ClientEvent> SubscribeAsync(string? resumeCursor, CancellationToken cancellationToken = default);
    Task<OperationResult> RequestOperationAsync(OperationRequest request, CancellationToken cancellationToken = default);
}

public interface IDraftStore
{
    Task<MandateDraft> SaveMandateAsync(MandateDraft draft, int? expectedRevision, CancellationToken cancellationToken = default);
    Task<MandateDraft?> LoadMandateAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MandateDraft>> ListMandatesAsync(CancellationToken cancellationToken = default);
    Task<InquiryDraft> SaveInquiryAsync(InquiryDraft draft, int? expectedRevision, CancellationToken cancellationToken = default);
    Task<InquiryDraft?> LoadInquiryAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InquiryDraft>> ListInquiryAsync(CancellationToken cancellationToken = default);
    Task<SessionDraft> SaveSessionAsync(SessionDraft draft, int? expectedRevision, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SessionDraft>> ListSessionsAsync(CancellationToken cancellationToken = default);
}

public sealed class ConcurrentDraftException : InvalidOperationException
{
    public ConcurrentDraftException(string message) : base(message) { }
}

public sealed class DraftIOException : IOException
{
    public DraftIOException(string message, Exception? inner = null) : base(message, inner) { }
}
