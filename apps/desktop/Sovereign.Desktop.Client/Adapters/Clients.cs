using Sovereign.Desktop.Core;

namespace Sovereign.Desktop.Client.Adapters;

public sealed class PreviewOperatorClient : IOperatorClient
{
    private readonly Scenarios.ScenarioDocument _scenario;
    private readonly EventInbox _inbox;
    private readonly OperationGuard _guard = new();

    public PreviewOperatorClient(Scenarios.ScenarioDocument scenario)
    {
        _scenario = scenario;
        Mode = ApplicationMode.DesktopPreview;
        ScenarioId = scenario.ScenarioId;
        _inbox = EventInbox.From(_scenario.Events);
    }

    public ApplicationMode Mode { get; }
    public string ModeBanner => ModeLabels.Banner(Mode);
    public string? ScenarioId { get; }

    public Task<RuntimeIdentity> GetRuntimeAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_scenario.Runtime with { BackendType = BackendType.PreviewFixture });

    public Task<SystemHealth> GetHealthAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_scenario.Health);

    public Task<Presence<AccountView>> GetAccountAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_scenario.Account);

    public Task<IReadOnlyList<DecisionRecord>> ListDecisionsAsync(string? query = null, DecisionAction? action = null, CancellationToken cancellationToken = default)
    {
        IEnumerable<DecisionRecord> items = _scenario.Decisions;
        if (!string.IsNullOrWhiteSpace(query))
        {
            items = items.Where(d =>
                d.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                || d.Instrument.Contains(query, StringComparison.OrdinalIgnoreCase)
                || d.Reasons.Any(r => r.Contains(query, StringComparison.OrdinalIgnoreCase)));
        }

        if (action is DecisionAction a)
        {
            items = items.Where(d => d.Action == a);
        }

        return Task.FromResult<IReadOnlyList<DecisionRecord>>(items.ToArray());
    }

    public Task<DecisionRecord?> GetDecisionAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_scenario.Decisions.FirstOrDefault(d => d.Id == id));

    public Task<EvidenceDocument?> GetEvidenceAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_scenario.Evidence.FirstOrDefault(e => e.Id == id));

    public Task<IReadOnlyList<OrderRecord>> ListOrdersAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_scenario.Orders);

    public Task<OrderRecord?> GetOrderAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_scenario.Orders.FirstOrDefault(o => o.Id == id));

    public Task<IReadOnlyList<ClientEvent>> ListEventsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_inbox.Snapshot());

    public Task<IReadOnlyList<CapabilityItem>> ListCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_scenario.Capabilities);

    public Task<EvaluationSummary> GetEvaluationAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_scenario.Evaluation);

    public async IAsyncEnumerable<ClientEvent> SubscribeAsync(string? resumeCursor, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var item in _inbox.Resume(resumeCursor))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
            await Task.Yield();
        }
    }

    public Task<OperationResult> RequestOperationAsync(OperationRequest request, CancellationToken cancellationToken = default)
    {
        var started = _guard.TryBegin(request.Operation, request.IdempotencyKey, out var existing);
        if (!started && existing is not null)
        {
            return Task.FromResult(existing);
        }

        try
        {
            if (_scenario.Operations.TryGetValue(request.Operation, out var preset))
            {
                var result = preset with { IdempotencyKey = request.IdempotencyKey };
                _guard.Complete(request.IdempotencyKey, result);
                return Task.FromResult(result);
            }

            var simulated = new OperationResult(
                request.Operation,
                request.IdempotencyKey,
                OperationStatus.Unavailable,
                null,
                "Preview client does not execute host operations. This is a simulated acknowledgement surface only; it is not actual authority.",
                SimulatedPreviewAcknowledgement: true);
            _guard.Complete(request.IdempotencyKey, simulated);
            return Task.FromResult(simulated);
        }
        finally
        {
            _guard.End(request.IdempotencyKey);
        }
    }
}

public sealed class ArchiveOperatorClient : IOperatorClient
{
    private readonly Scenarios.ScenarioDocument _scenario;
    private readonly Sovereign.Desktop.Core.Archive.ArchiveLoadResult _packet;

    public ArchiveOperatorClient(Scenarios.ScenarioDocument scenario, Sovereign.Desktop.Core.Archive.ArchiveLoadResult packet)
    {
        _scenario = scenario;
        _packet = packet;
        Mode = ApplicationMode.EvidenceArchive;
        ScenarioId = scenario.ScenarioId;
        Trust = packet.Trust;
        PacketReason = packet.Reason;
    }

    public ApplicationMode Mode { get; }
    public string ModeBanner => ModeLabels.Banner(Mode) + " Trust: " + Trust + ". " + PacketReason;
    public string? ScenarioId { get; }
    public ArchiveTrust Trust { get; }
    public string PacketReason { get; }

    public Task<RuntimeIdentity> GetRuntimeAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_scenario.Runtime with
        {
            BackendType = BackendType.HistoricalArchive,
            IntegrationStatus = "Historical archive. No execution authority."
        });

    public Task<SystemHealth> GetHealthAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_scenario.Health);

    public Task<Presence<AccountView>> GetAccountAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_scenario.Account);

    public Task<IReadOnlyList<DecisionRecord>> ListDecisionsAsync(string? query = null, DecisionAction? action = null, CancellationToken cancellationToken = default)
    {
        IEnumerable<DecisionRecord> items = _scenario.Decisions;
        if (!string.IsNullOrWhiteSpace(query))
        {
            items = items.Where(d => d.Instrument.Contains(query, StringComparison.OrdinalIgnoreCase) || d.Id.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        if (action is DecisionAction a)
        {
            items = items.Where(d => d.Action == a);
        }

        return Task.FromResult<IReadOnlyList<DecisionRecord>>(items.ToArray());
    }

    public Task<DecisionRecord?> GetDecisionAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_scenario.Decisions.FirstOrDefault(d => d.Id == id));

    public Task<EvidenceDocument?> GetEvidenceAsync(string id, CancellationToken cancellationToken = default)
    {
        var fromScenario = _scenario.Evidence.FirstOrDefault(e => e.Id == id);
        if (fromScenario is not null)
        {
            return Task.FromResult<EvidenceDocument?>(fromScenario);
        }

        if (_packet.Texts.TryGetValue(id, out var text))
        {
            return Task.FromResult<EvidenceDocument?>(new EvidenceDocument(id, id, "text/plain", Core.Evidence.EvidenceSafety.AsData(text), ["https", "http"], []));
        }

        return Task.FromResult<EvidenceDocument?>(null);
    }

    public Task<IReadOnlyList<OrderRecord>> ListOrdersAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_scenario.Orders);

    public Task<OrderRecord?> GetOrderAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_scenario.Orders.FirstOrDefault(o => o.Id == id));

    public Task<IReadOnlyList<ClientEvent>> ListEventsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_scenario.Events);

    public Task<IReadOnlyList<CapabilityItem>> ListCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_scenario.Capabilities);

    public Task<EvaluationSummary> GetEvaluationAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_scenario.Evaluation);

    public async IAsyncEnumerable<ClientEvent> SubscribeAsync(string? resumeCursor, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var e in _scenario.Events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (resumeCursor is not null && string.CompareOrdinal(e.Cursor, resumeCursor) <= 0)
            {
                continue;
            }

            yield return e;
            await Task.Yield();
        }
    }

    public Task<OperationResult> RequestOperationAsync(OperationRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new OperationResult(
            request.Operation,
            request.IdempotencyKey,
            OperationStatus.Rejected,
            null,
            "Archives cannot acquire execution authority. Operation refused.",
            false));
}
