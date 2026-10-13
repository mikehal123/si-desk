using Sovereign.Desktop.Client;
using Sovereign.Desktop.Client.Adapters;
using ScenarioCatalog = Sovereign.Desktop.Client.Scenarios.ScenarioCatalog;
using Sovereign.Desktop.Client.Storage;
using Sovereign.Desktop.Core;
using Sovereign.Desktop.Core.Drafts;
using Sovereign.Desktop.Core.Support;

namespace Sovereign.Desktop.Presentation;

public sealed class ShellViewModel : ObservableObject
{
    public const string AppDisplayName = "xAI Desk";
    public static string AppVersion { get; } = System.Reflection.CustomAttributeExtensions
        .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(ShellViewModel).Assembly)!
        .InformationalVersion.Split('+')[0];

    private IOperatorClient _client;
    private readonly IDraftStore _drafts;
    private readonly string _profileDirectory;
    private readonly string _fixturesRoot;
    private readonly string? _hostProfileDirectory;
    private string _statusLine = "Loading";
    private string _selectedNav = "home";
    private string _query = "";
    private DecisionRecord? _selectedDecision;
    private OrderRecord? _selectedOrder;
    private EvidenceDocument? _selectedEvidence;
    private MandateDraft _mandate;
    private string _draftErrors = "";
    private string _draftStatus = "DRAFT — NOT ACTIVATED";
    private string _theme = "Dark";
    private string _lastError = "";
    private string _disabledReason = "";

    public ShellViewModel(IOperatorClient client, IDraftStore drafts, string profileDirectory, string fixturesRoot, string? hostProfileDirectory = null)
    {
        _client = client;
        _drafts = drafts;
        _profileDirectory = profileDirectory;
        _fixturesRoot = fixturesRoot;
        _hostProfileDirectory = hostProfileDirectory;
        _mandate = NewMandate();
    }

    public IOperatorClient Client => _client;
    public string AppTitle => $"{AppDisplayName} {AppVersion}";
    public string ModeBanner => _client.ModeBanner;
    public ApplicationMode Mode => _client.Mode;
    public string ProfileDirectory => _profileDirectory;
    public string StatusLine { get => _statusLine; private set => SetField(ref _statusLine, value); }
    public string SelectedNav { get => _selectedNav; set => SetField(ref _selectedNav, value); }
    public string Query { get => _query; set => SetField(ref _query, value); }
    public string Theme { get => _theme; set => SetField(ref _theme, value); }
    public string LastError { get => _lastError; private set => SetField(ref _lastError, value); }
    public string DisabledReason { get => _disabledReason; private set => SetField(ref _disabledReason, value); }
    public string DraftErrors { get => _draftErrors; private set => SetField(ref _draftErrors, value); }
    public string DraftStatus { get => _draftStatus; private set => SetField(ref _draftStatus, value); }
    public MandateDraft Mandate { get => _mandate; private set => SetField(ref _mandate, value); }

    public RuntimeIdentity? Runtime { get; private set; }
    public SystemHealth? Health { get; private set; }
    public Presence<AccountView> Account { get; private set; } = Presence<AccountView>.Unknown("not loaded");
    public IReadOnlyList<DecisionRecord> Decisions { get; private set; } = [];
    public IReadOnlyList<OrderRecord> Orders { get; private set; } = [];
    public IReadOnlyList<ClientEvent> Events { get; private set; } = [];
    public IReadOnlyList<CapabilityItem> Capabilities { get; private set; } = [];
    public EvaluationSummary? Evaluation { get; private set; }
    public DecisionRecord? SelectedDecision { get => _selectedDecision; private set => SetField(ref _selectedDecision, value); }
    public OrderRecord? SelectedOrder { get => _selectedOrder; private set => SetField(ref _selectedOrder, value); }
    public EvidenceDocument? SelectedEvidence { get => _selectedEvidence; private set => SetField(ref _selectedEvidence, value); }
    public EvidenceDocument? ApiNegativeEvidence { get; private set; }
    public IReadOnlyList<MandateDraft> SavedMandates { get; private set; } = [];

    public string EngineLabel => Health?.Engine.Label ?? "Engine: unknown";
    public string ProviderLabel => Health?.Provider.Label ?? "Provider: unknown";
    public string BrokerLabel => Health?.Broker.Label ?? "Broker: unknown";
    public string ReconciliationLabel => Health?.Reconciliation.Label ?? "Reconciliation: unknown";
    public string AuthorizationLabel => Health?.Authorization.Label ?? "Authorization: unknown";
    public string AccountHeading => Account.IsKnown
        ? $"Account {Account.Value!.AccountId} ({Account.Value.Environment}) — {ModeLabels.Short(Mode)}"
        : $"Account unknown — {ModeLabels.Short(Mode)} — {Account.Describe()}";

    public string CashLabel => Account.IsKnown ? Account.Value!.Cash.Describe(m => m.Format()) : Account.Describe();
    public string EquityLabel => Account.IsKnown ? Account.Value!.EndingEquity.Describe(m => m.Format()) : Account.Describe();
    public string TradingAuthorityLabel => Account.IsKnown ? Account.Value!.TradingAuthority : "unknown";

    public bool IsNativeAuthenticated => _client is NativeOperatorClient native && native.IsConnected;

    public string WorkspaceGuidance => Mode switch
    {
        ApplicationMode.NativeConnection when IsNativeAuthenticated =>
            "Authenticated native connection. Offline synthetic execution only. Not a live market. Not preview fixtures.",
        ApplicationMode.NativeConnection =>
            "Native mode is not authenticated. Local drafts only. Account panel is not filled with preview balances.",
        ApplicationMode.EvidenceArchive =>
            "Historical packet. Observation window is in the banner. No execution authority.",
        _ => "Preview scenario. Values are deterministic fixtures, not a live account or accounting."
    };

    public static string FormatHostOperation(OperationResult result)
    {
        var authority = result.SimulatedPreviewAcknowledgement
            ? "Preview simulated acknowledgement — not host authority and not a live market."
            : "Offline synthetic host execution — not a live market.";
        return result.Status + ": " + result.Detail + ". " + authority;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        Runtime = await _client.GetRuntimeAsync(cancellationToken).ConfigureAwait(false);
        Health = await _client.GetHealthAsync(cancellationToken).ConfigureAwait(false);
        Account = await _client.GetAccountAsync(cancellationToken).ConfigureAwait(false);
        Decisions = await _client.ListDecisionsAsync(Query, null, cancellationToken).ConfigureAwait(false);
        Orders = await _client.ListOrdersAsync(cancellationToken).ConfigureAwait(false);
        Events = await _client.ListEventsAsync(cancellationToken).ConfigureAwait(false);
        if (_client is NativeOperatorClient native && native.ConsumeResyncRequired())
        {
            await native.ResyncAuthoritativeStateAsync(cancellationToken).ConfigureAwait(false);
            Account = await _client.GetAccountAsync(cancellationToken).ConfigureAwait(false);
            Decisions = await _client.ListDecisionsAsync(Query, null, cancellationToken).ConfigureAwait(false);
            Orders = await _client.ListOrdersAsync(cancellationToken).ConfigureAwait(false);
            Evaluation = await _client.GetEvaluationAsync(cancellationToken).ConfigureAwait(false);
            Events = await _client.ListEventsAsync(cancellationToken).ConfigureAwait(false);
        }

        Capabilities = await _client.ListCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        Evaluation = await _client.GetEvaluationAsync(cancellationToken).ConfigureAwait(false);
        SavedMandates = await _drafts.ListMandatesAsync(cancellationToken).ConfigureAwait(false);
        StatusLine = $"{ModeLabels.Short(Mode)} | {Runtime.BackendType} | {Runtime.IntegrationStatus}";
        Raise(nameof(Runtime));
        Raise(nameof(Health));
        Raise(nameof(Account));
        Raise(nameof(Decisions));
        Raise(nameof(Orders));
        Raise(nameof(Events));
        Raise(nameof(Capabilities));
        Raise(nameof(Evaluation));
        Raise(nameof(SavedMandates));
        Raise(nameof(EngineLabel));
        Raise(nameof(ProviderLabel));
        Raise(nameof(BrokerLabel));
        Raise(nameof(ReconciliationLabel));
        Raise(nameof(AuthorizationLabel));
        Raise(nameof(AccountHeading));
        Raise(nameof(CashLabel));
        Raise(nameof(EquityLabel));
        Raise(nameof(TradingAuthorityLabel));
        Raise(nameof(ModeBanner));
        Raise(nameof(IsNativeAuthenticated));
        Raise(nameof(WorkspaceGuidance));
    }

    public async Task SwitchModeAsync(ApplicationMode mode, string? scenarioId, string? archiveDirectory, CancellationToken cancellationToken = default)
    {
        ClearSelection("mode-change");
        _client = mode switch
        {
            ApplicationMode.DesktopPreview => new PreviewOperatorClient(
                ScenarioCatalog.Load(_fixturesRoot, scenarioId ?? "admitted-blocked-execution")),
            ApplicationMode.EvidenceArchive when archiveDirectory is not null => CreateArchiveClient(archiveDirectory),
            ApplicationMode.NativeConnection => new NativeOperatorClient(NativeOptions()),
            ApplicationMode.EvidenceArchive => throw new InvalidOperationException("Archive mode requires a packet directory."),
            _ => new NativeOperatorClient(NativeOptions())
        };
        Raise(nameof(Client));
        Raise(nameof(Mode));
        Raise(nameof(ModeBanner));
        await RefreshAsync(cancellationToken);
    }

    public async Task SwitchScenarioAsync(string scenarioId, CancellationToken cancellationToken = default)
    {
        if (Mode != ApplicationMode.DesktopPreview)
        {
            LastError = "Scenario switching is a preview-mode action.";
            return;
        }

        ClearSelection("scenario-change");
        _client = new PreviewOperatorClient(ScenarioCatalog.Load(_fixturesRoot, scenarioId));
        await RefreshAsync(cancellationToken);
    }

    public async Task SwitchAccountContextAsync(string scenarioId, CancellationToken cancellationToken = default)
    {
        ClearSelection("account-change");
        if (Mode == ApplicationMode.DesktopPreview)
        {
            _client = new PreviewOperatorClient(ScenarioCatalog.Load(_fixturesRoot, scenarioId));
        }

        await RefreshAsync(cancellationToken);
    }

    public void ClearSelection(string reason)
    {
        SelectedDecision = null;
        SelectedOrder = null;
        SelectedEvidence = null;
        StatusLine = "Selections cleared (" + reason + ")";
    }

    public async Task SelectDecisionAsync(string id, CancellationToken cancellationToken = default)
    {
        SelectedDecision = await _client.GetDecisionAsync(id, cancellationToken);
        if (SelectedDecision?.RelatedOrderId is string orderId)
        {
            SelectedOrder = await _client.GetOrderAsync(orderId, cancellationToken);
        }

        if (SelectedDecision?.EvidenceIds.FirstOrDefault() is string evidenceId)
        {
            SelectedEvidence = await _client.GetEvidenceAsync(evidenceId, cancellationToken);
        }

        SelectedNav = "decisions";
    }

    public async Task FilterDecisionsAsync(CancellationToken cancellationToken = default)
    {
        Decisions = await _client.ListDecisionsAsync(Query, null, cancellationToken);
        Raise(nameof(Decisions));
    }

    public IReadOnlyList<FieldError> ValidateMandate() => DraftValidator.ValidateMandate(Mandate);

    public async Task SaveMandateAsync(CancellationToken cancellationToken = default)
    {
        var errors = ValidateMandate();
        if (errors.Count > 0)
        {
            DraftErrors = string.Join(Environment.NewLine, errors.Select(e => e.Field + ": " + e.Message));
            DraftStatus = "DRAFT — NOT ACTIVATED (invalid)";
            return;
        }

        try
        {
            Mandate = await _drafts.SaveMandateAsync(Mandate, Mandate.Revision == 0 ? 0 : Mandate.Revision, cancellationToken);
            DraftErrors = "";
            DraftStatus = $"DRAFT — NOT ACTIVATED · id {Mandate.Id} · revision {Mandate.Revision}";
            SavedMandates = await _drafts.ListMandatesAsync(cancellationToken);
            Raise(nameof(SavedMandates));
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            DraftStatus = "DRAFT — NOT ACTIVATED (save failed; last valid file retained when present)";
        }
    }

    public async Task LoadMandateAsync(string id, CancellationToken cancellationToken = default)
    {
        var loaded = await _drafts.LoadMandateAsync(id, cancellationToken);
        if (loaded is null)
        {
            LastError = "Draft not found: " + id;
            return;
        }

        Mandate = loaded;
        DraftStatus = $"DRAFT — NOT ACTIVATED · id {Mandate.Id} · revision {Mandate.Revision}";
        DraftErrors = "";
    }

    public void DuplicateMandate()
    {
        Mandate = Mandate with
        {
            Id = Mandate.Id + "-copy",
            Name = Mandate.Name + " copy",
            Revision = 0
        };
        DraftStatus = "DRAFT — NOT ACTIVATED (duplicate, unsaved)";
    }

    public async Task ReopenNativeSessionAsync(CancellationToken cancellationToken = default)
    {
        if (_client is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }

        _client = new NativeOperatorClient(NativeOptions());
        Raise(nameof(Client));
        Raise(nameof(Mode));
        Raise(nameof(ModeBanner));
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ProbeEvidenceGetApiNegativeAsync(string evidenceId, CancellationToken cancellationToken = default)
    {
        ApiNegativeEvidence = await _client.GetEvidenceAsync(evidenceId, cancellationToken).ConfigureAwait(false);
        Raise(nameof(ApiNegativeEvidence));
    }

    public Task<OperationResult> RequestHostOperationAsync(string operation, CancellationToken cancellationToken = default) =>
        RequestHostOperationAsync(operation, operation + ":" + Guid.NewGuid().ToString("N"), cancellationToken);

    public async Task<OperationResult> RequestHostOperationAsync(string operation, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var request = new OperationRequest(operation, idempotencyKey, Health?.ToString(), new Dictionary<string, string>());
        var result = await _client.RequestOperationAsync(request, cancellationToken);
        DisabledReason = result.Status is OperationStatus.Unavailable or OperationStatus.Rejected
            ? result.Detail
            : "";
        LastError = result.Status == OperationStatus.Unavailable ? result.Detail : LastError;
        return result;
    }

    public async Task<OperationResult> StartOfflineEngineRunAsync(CancellationToken cancellationToken = default)
    {
        var advertised = Capabilities.Any(c =>
            c.Id.Equals("offline_demo.start", StringComparison.OrdinalIgnoreCase)
            || c.Id.Contains("offline_demo", StringComparison.OrdinalIgnoreCase));
        if (Mode == ApplicationMode.NativeConnection && _client is NativeOperatorClient native && native.IsConnected && !advertised)
        {
            var refused = new OperationResult(
                "offline_demo.start",
                NativeOperatorClient.OfflineDemoIdempotencyKey,
                OperationStatus.Unavailable,
                null,
                "Host has not advertised offline_demo.start. The desktop will not start a run from envelope reachability alone.",
                false);
            DisabledReason = refused.Detail;
            return refused;
        }

        var result = await RequestHostOperationAsync("offline_demo.start", NativeOperatorClient.OfflineDemoIdempotencyKey, cancellationToken).ConfigureAwait(false);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private NativeClientOptions NativeOptions()
    {
        var host = _hostProfileDirectory;
        if (string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(_profileDirectory))
        {
            host = Path.Combine(_profileDirectory, "host");
        }

        return string.IsNullOrWhiteSpace(host)
            ? NativeClientOptions.Disconnected
            : new NativeClientOptions(host, "Sovereign.Desktop", AppVersion, _profileDirectory);
    }

    public SupportExportResult ExportSupport(string destinationZip, bool includePrivateDrafts = false)
    {
        File.WriteAllText(Path.Combine(_profileDirectory, "app-version.json"),
            $$"""{"app":"{{AppDisplayName}}","version":"{{AppVersion}}","mode":"{{Mode}}"}""");
        File.WriteAllText(Path.Combine(_profileDirectory, "connection-state.json"),
            $$"""{"mode":"{{Mode}}","backend":"{{Runtime?.BackendType}}","connected":{{(_client is NativeOperatorClient n && n.IsConnected ? "true" : "false")}}}""");
        File.WriteAllText(Path.Combine(_profileDirectory, "last-client-errors.json"),
            $$"""{"last_error":{{System.Text.Json.JsonSerializer.Serialize(LastError)}}}""");
        File.WriteAllText(Path.Combine(_profileDirectory, "preferences.json"),
            $$"""{"theme":"{{Theme}}"}""");
        return SupportExporter.Export(_profileDirectory, destinationZip, AppVersion, ModeLabels.Short(Mode), includePrivateDrafts);
    }

    public string DisabledReasonFor(string operation) => operation switch
    {
        "activate-session" => "Activation is host-gated. No authenticated session grant is present.",
        "pause-session" => "Pause requires host capability PAUSE_NEW_RISK and an authenticated session.",
        "submit-order" => "The desktop does not submit orders. Paper transport is a host responsibility.",
        "store-secret" => "Credential storage is a future host-backed function. Preview will not accept secrets.",
        _ => "Unsupported action: " + operation
    };

    public void SetMandateFields(
        string name,
        string? universeCsv,
        string? horizon,
        string? schedule,
        string? timezone,
        int? concentration,
        int? sector,
        long? cashFloor,
        int? turnover,
        string? sourcesCsv)
    {
        Mandate = Mandate with
        {
            Name = name,
            Universe = Split(universeCsv),
            Horizon = EmptyToNull(horizon),
            Schedule = EmptyToNull(schedule),
            Timezone = EmptyToNull(timezone),
            ConcentrationCapBps = concentration,
            SectorCapBps = sector,
            CashFloorMinorUnits = cashFloor,
            TurnoverBps = turnover,
            AllowedSources = Split(sourcesCsv)
        };
    }

    private IOperatorClient CreateArchiveClient(string directory)
    {
        var packet = Sovereign.Desktop.Core.Archive.ArchiveReader.OpenDirectory(directory);
        if (packet.Trust is ArchiveTrust.Invalid or ArchiveTrust.Unsupported)
        {
            LastError = packet.Reason;
            throw new InvalidOperationException(packet.Reason);
        }

        var snapshotPath = Path.Combine(directory, "snapshot.json");
        var scenario = File.Exists(snapshotPath)
            ? ScenarioCatalog.Parse(File.ReadAllText(snapshotPath))
            : ScenarioCatalog.Load(_fixturesRoot, "admitted-blocked-execution");
        return new ArchiveOperatorClient(scenario, packet);
    }

    private static MandateDraft NewMandate() => new(
        MandateDraft.CurrentSchema,
        "mandate-default",
        0,
        "Untitled mandate",
        DateTimeOffset.UnixEpoch,
        ActivationStatus.DraftNotActivated,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        "USD");

    private static IReadOnlyList<string>? Split(string? csv)
    {
        if (csv is null)
        {
            return null;
        }

        if (csv.Trim().Length == 0)
        {
            return Array.Empty<string>();
        }

        return csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}


