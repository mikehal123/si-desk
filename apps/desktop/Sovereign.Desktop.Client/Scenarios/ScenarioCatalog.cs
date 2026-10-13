using System.Text.Json;
using Sovereign.Desktop.Core;

namespace Sovereign.Desktop.Client.Scenarios;

public sealed class ScenarioDocument
{
    public required string ScenarioId { get; init; }
    public required string Title { get; init; }
    public required string UiInterpretation { get; init; }
    public required RuntimeIdentity Runtime { get; init; }
    public required SystemHealth Health { get; init; }
    public Presence<AccountView> Account { get; init; } = Presence<AccountView>.Missing("no account in this scenario");
    public IReadOnlyList<DecisionRecord> Decisions { get; init; } = [];
    public IReadOnlyList<OrderRecord> Orders { get; init; } = [];
    public IReadOnlyList<ClientEvent> Events { get; init; } = [];
    public IReadOnlyList<CapabilityItem> Capabilities { get; init; } = [];
    public IReadOnlyList<EvidenceDocument> Evidence { get; init; } = [];
    public required EvaluationSummary Evaluation { get; init; }
    public IReadOnlyDictionary<string, OperationResult> Operations { get; init; } =
        new Dictionary<string, OperationResult>();
}

public static class ScenarioCatalog
{
    public static IReadOnlyList<string> RequiredIds { get; } =
    [
        "disconnected-drafts",
        "admitted-blocked-execution",
        "missing-valuation",
        "stale-evidence-blocked",
        "hold-and-abstain",
        "unknown-order-lost-response",
        "partial-fill-cancel-pending",
        "recon-discrepancy-paused",
        "expired-grant-unavailable-provider",
        "long-running-job-cancel-timeout",
        "missed-late-insufficient-eval"
    ];

    public static ScenarioDocument Load(string fixturesRoot, string scenarioId)
    {
        var path = Path.Combine(fixturesRoot, "scenarios", scenarioId + ".json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Scenario fixture missing.", path);
        }

        var json = File.ReadAllText(path);
        return Parse(json);
    }

    public static ScenarioDocument Parse(string json)
    {
        var dto = JsonSerializer.Deserialize<ScenarioDto>(json, Options)
                  ?? throw new InvalidOperationException("Scenario JSON was null.");
        if (string.IsNullOrWhiteSpace(dto.ScenarioId))
        {
            throw new InvalidOperationException("scenario_id is required.");
        }

        return new ScenarioDocument
        {
            ScenarioId = dto.ScenarioId,
            Title = dto.Title ?? dto.ScenarioId,
            UiInterpretation = dto.UiInterpretation ?? "",
            Runtime = MapRuntime(dto.Runtime),
            Health = MapHealth(dto.Health),
            Account = MapAccount(dto.Account),
            Decisions = (dto.Decisions ?? []).Select(MapDecision).ToArray(),
            Orders = (dto.Orders ?? []).Select(MapOrder).ToArray(),
            Events = (dto.Events ?? []).Select(MapEvent).ToArray(),
            Capabilities = (dto.Capabilities ?? []).Select(MapCap).ToArray(),
            Evidence = (dto.Evidence ?? []).Select(MapEvidence).ToArray(),
            Evaluation = MapEval(dto.Evaluation),
            Operations = (dto.Operations ?? new Dictionary<string, OperationDto>()).ToDictionary(
                kv => kv.Key,
                kv => MapOp(kv.Key, kv.Value),
                StringComparer.OrdinalIgnoreCase)
        };
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    private static RuntimeIdentity MapRuntime(RuntimeDto? dto) => new(
        dto?.BuildId ?? "unknown-build",
        dto?.ProtocolVersion ?? "unspecified",
        ParseEnum(dto?.BackendType, BackendType.PreviewFixture),
        dto?.Capabilities ?? [],
        ParseTime(dto?.ObservationTimestamp),
        dto?.IntegrationStatus ?? "native engine not connected");

    private static SystemHealth MapHealth(HealthDto? dto) => new(
        MapState(dto?.Engine, EngineState.Unknown, "engine"),
        MapState(dto?.Provider, ConnectivityState.Unknown, "provider"),
        MapState(dto?.Broker, ConnectivityState.Unknown, "broker"),
        MapState(dto?.Reconciliation, ReconciliationState.Unknown, "reconciliation"),
        MapState(dto?.Authorization, AuthorizationState.Unknown, "authorization"),
        MapTimePresence(dto?.NextCycle),
        (dto?.UnresolvedIssues ?? []).Select(i => new AttentionItem(i.Id ?? "issue", i.Severity ?? "info", i.Summary ?? "", i.NextStep, i.RelatedDecisionId, i.RelatedOrderId)).ToArray(),
        ParseTime(dto?.ObservationTimestamp));

    private static LabeledState<T> MapState<T>(StateDto? dto, T fallback, string name) where T : struct, Enum
    {
        if (dto is null || string.IsNullOrWhiteSpace(dto.Value))
        {
            return new LabeledState<T>(fallback, $"{name}: unknown", "state omitted from scenario");
        }

        var value = ParseEnum(dto.Value, fallback);
        return new LabeledState<T>(value, dto.Label ?? $"{name}: {value}", dto.Reason);
    }

    private static Presence<AccountView> MapAccount(AccountDto? dto)
    {
        if (dto is null)
        {
            return Presence<AccountView>.Missing("no account object in scenario");
        }

        if (dto.Presence is "unknown")
        {
            return Presence<AccountView>.Unknown(dto.Reason ?? "account unknown");
        }

        if (dto.Presence is "missing")
        {
            return Presence<AccountView>.Missing(dto.Reason ?? "account missing");
        }

        var holdings = (dto.Holdings ?? []).Select(h => new Holding(
            h.Instrument ?? "UNKNOWN",
            MapDecimal(h.Quantity),
            MapMoney(h.Mark),
            h.QuantitySource ?? "unspecified",
            h.IncompleteValuationReason)).ToArray();

        var view = new AccountView(
            dto.AccountId ?? "unspecified",
            dto.Environment ?? "unspecified",
            dto.BalanceBasis ?? "unspecified",
            ParseTime(dto.ObservedAt),
            MapMoney(dto.Cash),
            holdings,
            MapMoney(dto.Reservations),
            dto.OpenOrderIds ?? [],
            dto.IncompleteValuationReasons ?? [],
            dto.SourceLabel ?? "scenario",
            Presence<Money>.Unknown("ending_equity_cents is not a preview accounting field"),
            "preview");
        return Presence<AccountView>.Known(view);
    }

    private static DecisionRecord MapDecision(DecisionDto d) => new(
        d.Id ?? throw new InvalidOperationException("decision id required"),
        ParseEnum(d.Action, DecisionAction.Unknown),
        d.Instrument ?? "UNKNOWN",
        MapDecimal(d.Quantity),
        MapMoney(d.Price),
        d.EvidenceIds ?? [],
        d.AccountRevision ?? "unknown-revision",
        d.Reasons ?? [],
        MapGate("admission", d.Admission),
        MapGate("risk", d.Risk),
        MapGate("execution", d.Execution),
        ParseTime(d.ObservedAt),
        d.RelatedOrderId);

    private static GateResult MapGate(string name, GateDto? dto) =>
        new(name, ParseEnum(dto?.Verdict, GateVerdict.Unknown), dto?.Reason ?? "gate value not supplied");

    private static OrderRecord MapOrder(OrderDto o) => new(
        o.Id ?? throw new InvalidOperationException("order id required"),
        o.ProposalId,
        o.DecisionId,
        o.Instrument ?? "UNKNOWN",
        ParseEnum(o.RequestedState, OrderLifecycle.Unknown),
        MapEnumPresence<OrderLifecycle>(o.AcknowledgedState),
        MapEnumPresence<OrderLifecycle>(o.CurrentState),
        (o.Fills ?? []).Select(f => new FillRecord(f.Id ?? "fill", MapDecimal(f.Quantity), MapMoney(f.Price), ParseTime(f.At))).ToArray(),
        MapDecimal(o.RemainingQuantity),
        ParseTime(o.RequestedAt),
        o.AmbiguityReasons ?? []);

    private static ClientEvent MapEvent(EventDto e) => new(
        e.Id ?? throw new InvalidOperationException("event id required"),
        e.Cursor ?? e.Id,
        ParseTime(e.At),
        e.Kind ?? "note",
        e.Summary ?? "",
        e.DecisionId,
        e.OrderId,
        e.GapBefore);

    private static CapabilityItem MapCap(CapDto c) => new(
        c.Id ?? "unknown.tool",
        c.Version ?? "unspecified",
        c.Status ?? "planned",
        c.Provider ?? "unspecified",
        c.PermissionScope ?? "none",
        c.RequiredCapabilities ?? [],
        c.AvailabilityReason ?? "catalog entry is not evidence of installation",
        c.CostStatus ?? "unknown",
        c.LastOutcome,
        c.CatalogStatus ?? "preview");

    private static EvidenceDocument MapEvidence(EvidenceDto e) => new(
        e.Id ?? throw new InvalidOperationException("evidence id required"),
        e.Title ?? e.Id,
        e.MediaType ?? "text/plain",
        e.Text ?? "",
        e.AllowedUriSchemes ?? ["https", "http"],
        e.SourceUris ?? []);

    private static EvaluationSummary MapEval(EvalDto? dto) => new(
        dto?.CampaignId ?? "none",
        MapInt(dto?.SampleCount),
        MapString(dto?.Coverage),
        dto?.PerformanceBasis ?? "unspecified",
        dto?.BenchmarkAssumptions ?? "unspecified",
        dto?.CostAssumptions ?? "unspecified",
        dto?.MissedCount ?? 0,
        dto?.LateCount ?? 0,
        dto?.AbstainCount ?? 0,
        dto?.BlockedCount ?? 0,
        dto?.SoftwareResult ?? "desktop-preview-component",
        dto?.OperationalReadiness ?? "native-host-not-connected",
        dto?.InvestmentVerdict ?? "INSUFFICIENT_EVIDENCE",
        dto?.PlotAbsenceReason);

    private static OperationResult MapOp(string key, OperationDto dto) => new(
        dto.Operation ?? key,
        dto.IdempotencyKey ?? "none",
        ParseEnum(dto.Status, OperationStatus.Unknown),
        dto.HostRequestId,
        dto.Detail ?? "",
        dto.SimulatedPreviewAcknowledgement);

    private static Presence<Money> MapMoney(ValueDto? dto) => dto?.Presence switch
    {
        "known" when dto.MinorUnits is long minor && !string.IsNullOrWhiteSpace(dto.Currency) =>
            Presence<Money>.Known(new Money(dto.Currency, minor)),
        "unknown" => Presence<Money>.Unknown(dto.Reason ?? "amount unknown"),
        "missing" => Presence<Money>.Missing(dto.Reason ?? "amount missing"),
        _ => Presence<Money>.Unknown("amount not supplied")
    };

    private static Presence<decimal> MapDecimal(ValueDto? dto) => dto?.Presence switch
    {
        "known" when dto.Decimal is string s && decimal.TryParse(s, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var n) =>
            Presence<decimal>.Known(n),
        "unknown" => Presence<decimal>.Unknown(dto.Reason ?? "quantity unknown"),
        "missing" => Presence<decimal>.Missing(dto.Reason ?? "quantity missing"),
        _ => Presence<decimal>.Unknown("quantity not supplied")
    };

    private static Presence<int> MapInt(ValueDto? dto) => dto?.Presence switch
    {
        "known" when dto.Int is int n => Presence<int>.Known(n),
        "unknown" => Presence<int>.Unknown(dto.Reason ?? "count unknown"),
        "missing" => Presence<int>.Missing(dto.Reason ?? "count missing"),
        _ => Presence<int>.Unknown("count not supplied")
    };

    private static Presence<string> MapString(ValueDto? dto) => dto?.Presence switch
    {
        "known" when dto.Text is not null => Presence<string>.Known(dto.Text),
        "unknown" => Presence<string>.Unknown(dto.Reason ?? "text unknown"),
        "missing" => Presence<string>.Missing(dto.Reason ?? "text missing"),
        _ => Presence<string>.Missing("text not supplied")
    };

    private static Presence<DateTimeOffset> MapTimePresence(ValueDto? dto) => dto?.Presence switch
    {
        "known" when dto.Text is not null => Presence<DateTimeOffset>.Known(ParseTime(dto.Text)),
        "unknown" => Presence<DateTimeOffset>.Unknown(dto.Reason ?? "time unknown"),
        "missing" => Presence<DateTimeOffset>.Missing(dto.Reason ?? "time missing"),
        _ => Presence<DateTimeOffset>.Unknown("next cycle not supplied")
    };

    private static Presence<T> MapEnumPresence<T>(ValueDto? dto) where T : struct, Enum => dto?.Presence switch
    {
        "known" when dto.Text is not null => Presence<T>.Known(ParseEnum(dto.Text, default(T))),
        "unknown" => Presence<T>.Unknown(dto.Reason ?? "state unknown"),
        "missing" => Presence<T>.Missing(dto.Reason ?? "state missing"),
        _ => Presence<T>.Unknown("state not supplied")
    };

    private static T ParseEnum<T>(string? text, T fallback) where T : struct, Enum =>
        Enum.TryParse(text, ignoreCase: true, out T value) ? value : fallback;

    private static DateTimeOffset ParseTime(string? text) =>
        DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var value)
            ? value
            : DateTimeOffset.UnixEpoch;

    private sealed class ScenarioDto
    {
        public string? ScenarioId { get; set; }
        public string? Title { get; set; }
        public string? UiInterpretation { get; set; }
        public RuntimeDto? Runtime { get; set; }
        public HealthDto? Health { get; set; }
        public AccountDto? Account { get; set; }
        public List<DecisionDto>? Decisions { get; set; }
        public List<OrderDto>? Orders { get; set; }
        public List<EventDto>? Events { get; set; }
        public List<JobDto>? Jobs { get; set; }
        public List<CapDto>? Capabilities { get; set; }
        public List<EvidenceDto>? Evidence { get; set; }
        public EvalDto? Evaluation { get; set; }
        public Dictionary<string, OperationDto>? Operations { get; set; }
    }

    private sealed class RuntimeDto
    {
        public string? BuildId { get; set; }
        public string? ProtocolVersion { get; set; }
        public string? BackendType { get; set; }
        public List<string>? Capabilities { get; set; }
        public string? ObservationTimestamp { get; set; }
        public string? IntegrationStatus { get; set; }
    }

    private sealed class HealthDto
    {
        public StateDto? Engine { get; set; }
        public StateDto? Provider { get; set; }
        public StateDto? Broker { get; set; }
        public StateDto? Reconciliation { get; set; }
        public StateDto? Authorization { get; set; }
        public ValueDto? NextCycle { get; set; }
        public List<IssueDto>? UnresolvedIssues { get; set; }
        public string? ObservationTimestamp { get; set; }
    }

    private sealed class StateDto
    {
        public string? Value { get; set; }
        public string? Label { get; set; }
        public string? Reason { get; set; }
    }

    private sealed class IssueDto
    {
        public string? Id { get; set; }
        public string? Severity { get; set; }
        public string? Summary { get; set; }
        public string? NextStep { get; set; }
        public string? RelatedDecisionId { get; set; }
        public string? RelatedOrderId { get; set; }
    }

    private sealed class AccountDto
    {
        public string? Presence { get; set; }
        public string? Reason { get; set; }
        public string? AccountId { get; set; }
        public string? Environment { get; set; }
        public string? BalanceBasis { get; set; }
        public string? ObservedAt { get; set; }
        public ValueDto? Cash { get; set; }
        public List<HoldingDto>? Holdings { get; set; }
        public ValueDto? Reservations { get; set; }
        public List<string>? OpenOrderIds { get; set; }
        public List<string>? IncompleteValuationReasons { get; set; }
        public string? SourceLabel { get; set; }
    }

    private sealed class HoldingDto
    {
        public string? Instrument { get; set; }
        public ValueDto? Quantity { get; set; }
        public ValueDto? Mark { get; set; }
        public string? QuantitySource { get; set; }
        public string? IncompleteValuationReason { get; set; }
    }

    private sealed class DecisionDto
    {
        public string? Id { get; set; }
        public string? Action { get; set; }
        public string? Instrument { get; set; }
        public ValueDto? Quantity { get; set; }
        public ValueDto? Price { get; set; }
        public List<string>? EvidenceIds { get; set; }
        public string? AccountRevision { get; set; }
        public List<string>? Reasons { get; set; }
        public GateDto? Admission { get; set; }
        public GateDto? Risk { get; set; }
        public GateDto? Execution { get; set; }
        public string? ObservedAt { get; set; }
        public string? RelatedOrderId { get; set; }
    }

    private sealed class GateDto
    {
        public string? Verdict { get; set; }
        public string? Reason { get; set; }
    }

    private sealed class OrderDto
    {
        public string? Id { get; set; }
        public string? ProposalId { get; set; }
        public string? DecisionId { get; set; }
        public string? Instrument { get; set; }
        public string? RequestedState { get; set; }
        public ValueDto? AcknowledgedState { get; set; }
        public ValueDto? CurrentState { get; set; }
        public List<FillDto>? Fills { get; set; }
        public ValueDto? RemainingQuantity { get; set; }
        public string? RequestedAt { get; set; }
        public List<string>? AmbiguityReasons { get; set; }
    }

    private sealed class FillDto
    {
        public string? Id { get; set; }
        public ValueDto? Quantity { get; set; }
        public ValueDto? Price { get; set; }
        public string? At { get; set; }
    }

    private sealed class EventDto
    {
        public string? Id { get; set; }
        public string? Cursor { get; set; }
        public string? At { get; set; }
        public string? Kind { get; set; }
        public string? Summary { get; set; }
        public string? DecisionId { get; set; }
        public string? OrderId { get; set; }
        public bool GapBefore { get; set; }
    }

    private sealed class JobDto
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Status { get; set; }
        public string? Horizon { get; set; }
        public ValueDto? Thesis { get; set; }
        public ValueDto? CounterThesis { get; set; }
        public ValueDto? Uncertainty { get; set; }
        public List<string>? EvidenceRefs { get; set; }
        public List<string>? ReviewTriggers { get; set; }
        public string? UnavailableReason { get; set; }
        public bool PreviewScenario { get; set; }
    }

    private sealed class CapDto
    {
        public string? Id { get; set; }
        public string? Version { get; set; }
        public string? Status { get; set; }
        public string? Provider { get; set; }
        public string? PermissionScope { get; set; }
        public List<string>? RequiredCapabilities { get; set; }
        public string? AvailabilityReason { get; set; }
        public string? CostStatus { get; set; }
        public string? LastOutcome { get; set; }
        public string? CatalogStatus { get; set; }
    }

    private sealed class EvidenceDto
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? MediaType { get; set; }
        public string? Text { get; set; }
        public List<string>? AllowedUriSchemes { get; set; }
        public List<string>? SourceUris { get; set; }
    }

    private sealed class EvalDto
    {
        public string? CampaignId { get; set; }
        public ValueDto? SampleCount { get; set; }
        public ValueDto? Coverage { get; set; }
        public string? PerformanceBasis { get; set; }
        public string? BenchmarkAssumptions { get; set; }
        public string? CostAssumptions { get; set; }
        public int MissedCount { get; set; }
        public int LateCount { get; set; }
        public int AbstainCount { get; set; }
        public int BlockedCount { get; set; }
        public string? SoftwareResult { get; set; }
        public string? OperationalReadiness { get; set; }
        public string? InvestmentVerdict { get; set; }
        public string? PlotAbsenceReason { get; set; }
    }

    private sealed class OperationDto
    {
        public string? Operation { get; set; }
        public string? IdempotencyKey { get; set; }
        public string? Status { get; set; }
        public string? HostRequestId { get; set; }
        public string? Detail { get; set; }
        public bool SimulatedPreviewAcknowledgement { get; set; }
    }

    private sealed class ValueDto
    {
        public string? Presence { get; set; }
        public string? Reason { get; set; }
        public string? Currency { get; set; }
        public long? MinorUnits { get; set; }
        public string? Decimal { get; set; }
        public int? Int { get; set; }
        public string? Text { get; set; }
    }
}
