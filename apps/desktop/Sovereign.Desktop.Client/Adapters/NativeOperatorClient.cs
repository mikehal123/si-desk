using System.Globalization;
using System.Text.Json;
using Sovereign.Desktop.Client.Host;
using Sovereign.Desktop.Core;

namespace Sovereign.Desktop.Client.Adapters;

public sealed record NativeClientOptions(
    string? HostProfileDirectory,
    string ClientName,
    string ClientVersion,
    string? DesktopProfileDirectory = null,
    HostTransportSeam? TransportSeam = null)
{
    public static NativeClientOptions Disconnected { get; } = new(null, "Sovereign.Desktop", "0.1.7-shell-refresh");
}

public sealed class NativeOperatorClient : IOperatorClient, IAsyncDisposable
{
    public const string OfflineDemoIdempotencyKey = "offline-demo-three-cycle-v1";

    private readonly NativeClientOptions _options;
    private readonly List<string> _errors = [];
    private readonly OperationGuard _guard = new();
    private readonly EventInbox _inbox = new();
    private HostSession? _session;
    private string? _runId;
    private string? _operationId;
    private long? _snapshotRevision;
    private bool _resyncRequired;

    public NativeOperatorClient(NativeClientOptions? options = null)
    {
        _options = options ?? NativeClientOptions.Disconnected;
        Mode = ApplicationMode.NativeConnection;
        ConnectionReason = "No authenticated compatible host session. Named-pipe connect was not established. Preview fixtures are not substituted.";
        LastQueryFailure = HostQueryFailure.Disconnected;
    }

    public ApplicationMode Mode { get; }
    public string ModeBanner => IsConnected
        ? "NATIVE CONNECTION — authenticated session. Offline synthetic execution only. Not a live market. " + ConnectionReason
        : ModeLabels.Banner(Mode) + " " + ConnectionReason;
    public string? ScenarioId => null;
    public bool IsConnected => _session is not null;
    public string ConnectionReason { get; private set; }
    public IReadOnlyList<string> ClientErrors => _errors;
    public string? ActiveRunId => _runId;
    public string? ActiveOperationId => _operationId;
    public long? SnapshotRevision => _snapshotRevision;
    public HostQueryFailure LastQueryFailure { get; private set; }
    public bool ResyncRequired => _resyncRequired;

    public bool ConsumeResyncRequired()
    {
        var value = _resyncRequired;
        _resyncRequired = false;
        return value;
    }

    /// <summary>
    /// Re-read run and operation identity from the host. Never calls offline_demo.start.
    /// Truncated event history is not treated as the account/order/decision snapshot.
    /// </summary>
    public async Task ResyncAuthoritativeStateAsync(CancellationToken cancellationToken = default)
    {
        if (_session is null)
        {
            return;
        }

        await RecoverExistingRunAsync(cancellationToken).ConfigureAwait(false);
        if (_operationId is null)
        {
            return;
        }

        var reply = await CallAsync(
            "operations.get",
            HostJson.Obj(("operation_id", HostJson.Str(_operationId))),
            null,
            cancellationToken).ConfigureAwait(false);
        if (reply is null || reply["status"].AsString() != "COMPLETED")
        {
            return;
        }

        var result = ResultOf(reply);
        _runId = result["run_id"].AsString() ?? _runId;
        _operationId = result["operation_id"].AsString() ?? _operationId;
    }

    private string? ResolveHostProfileDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_options.HostProfileDirectory))
        {
            return Path.GetFullPath(_options.HostProfileDirectory);
        }

        if (!string.IsNullOrWhiteSpace(_options.DesktopProfileDirectory))
        {
            return Path.GetFullPath(Path.Combine(_options.DesktopProfileDirectory, "host"));
        }

        return null;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_session is not null)
        {
            return;
        }

        var hostProfile = ResolveHostProfileDirectory();
        if (string.IsNullOrWhiteSpace(hostProfile))
        {
            ConnectionReason = "No host profile directory. Native mode requires a current-user writable profile outside the install directory. Preview fixtures are not substituted.";
            LastQueryFailure = HostQueryFailure.Disconnected;
            return;
        }

        if (!HostDiscovery.IsAuthorizedProfile(hostProfile, out var profileReason))
        {
            ConnectionReason = profileReason + " Preview fixtures are not substituted. A development engine is not used.";
            LastQueryFailure = HostQueryFailure.Disconnected;
            return;
        }

        var profileId = InstalledHostSupervisor.ProfileIdFor(hostProfile);
        HostEndpoint? endpoint;
        var engine = InstalledEngineResolver.TryResolve(AppContext.BaseDirectory, out var engineReason);
        if (engine is not null)
        {
            try
            {
                endpoint = InstalledHostSupervisor.EnsureRunning(engine, hostProfile, profileId);
            }
            catch (Exception ex)
            {
                ConnectionReason = ex.Message + " Preview fixtures are not substituted. A development engine is not used.";
                LastQueryFailure = HostQueryFailure.Unavailable;
                _errors.Add(ConnectionReason);
                return;
            }
        }
        else
        {
            endpoint = HostDiscovery.TryRead(hostProfile, out var reason, profileId);
            if (endpoint is null)
            {
                ConnectionReason = engineReason + " " + reason + " Preview fixtures are not substituted. A development engine is not used.";
                LastQueryFailure = HostQueryFailure.Disconnected;
                return;
            }
        }

        try
        {
            _session = await HostSession.OpenAsync(
                endpoint,
                _options.ClientName,
                _options.ClientVersion,
                TimeSpan.FromSeconds(15),
                cancellationToken,
                _options.TransportSeam).ConfigureAwait(false);
            ConnectionReason = "Authenticated sovereign.host/v2 session on discovered endpoint " + endpoint.PipeName + " instance " + endpoint.InstanceId + ".";
            LastQueryFailure = HostQueryFailure.None;
            await RecoverExistingRunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _session = null;
            ConnectionReason = "Session handshake failed: " + ex.Message + " Preview fixtures are not substituted.";
            LastQueryFailure = HostQueryFailure.Transport;
            _errors.Add(ConnectionReason);
        }
    }

    public Task<RuntimeIdentity> GetRuntimeAsync(CancellationToken cancellationToken = default) =>
        QueryAsync(
            "runtime.get",
            HostJson.Obj(),
            result => HostProjection.MapRuntime(result, ConnectionReason),
            DisconnectedRuntime(),
            result => HostProjection.MapRuntime(result, "SCHEMA: runtime result was not a completed runtime object."),
            reply => new RuntimeIdentity(
                "native-host",
                HostV2.Protocol,
                BackendType.NativeHost,
                [],
                DateTimeOffset.UtcNow,
                "UNAVAILABLE: " + HostProjection.FailureText(reply) + " Session remains authenticated."),
            "runtime",
            cancellationToken);

    public Task<SystemHealth> GetHealthAsync(CancellationToken cancellationToken = default) =>
        QueryAsync(
            "health.get",
            HostJson.Obj(),
            HostProjection.MapHealth,
            NativeOperatorClient.DisconnectedHealth(),
            _ => HostProjection.SchemaHealth("health.get result type was not health"),
            reply => HostProjection.UnavailableHealth(HostProjection.FailureText(reply)),
            "health",
            cancellationToken);

    public Task<Presence<AccountView>> GetAccountAsync(CancellationToken cancellationToken = default)
    {
        if (_runId is null)
        {
            LastQueryFailure = IsConnected ? HostQueryFailure.None : HostQueryFailure.Disconnected;
            return Task.FromResult(IsConnected
                ? Presence<AccountView>.Unknown("No run_id yet. Recover an existing host run or start the advertised offline demo before reading account.")
                : Presence<AccountView>.Unknown("Native host disconnected; account is not preview data."));
        }

        return QueryAsync(
            "account.get",
            HostJson.Obj(("run_id", HostJson.Str(_runId))),
            HostProjection.MapAccount,
            Presence<AccountView>.Unknown("account.get transport failed; value is not preview data."),
            result => Presence<AccountView>.Unknown(HostProjection.TryType(result, "account", out var reason) ? "account mapping failed" : reason),
            reply => Presence<AccountView>.Unknown("UNAVAILABLE: " + HostProjection.FailureText(reply)),
            "account",
            cancellationToken);
    }

    public async Task<IReadOnlyList<DecisionRecord>> ListDecisionsAsync(string? query = null, DecisionAction? action = null, CancellationToken cancellationToken = default)
    {
        if (_runId is null)
        {
            return [];
        }

        var items = await QueryAsync(
            "decisions.list",
            HostJson.Obj(("run_id", HostJson.Str(_runId))),
            HostProjection.MapDecisions,
            (IReadOnlyList<DecisionRecord>)[],
            _ => [],
            _ => [],
            "decision_list",
            cancellationToken).ConfigureAwait(false);
        IEnumerable<DecisionRecord> filtered = items;
        if (!string.IsNullOrWhiteSpace(query))
        {
            filtered = filtered.Where(d => d.Id.Contains(query, StringComparison.OrdinalIgnoreCase) || d.Instrument.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        if (action is DecisionAction a)
        {
            filtered = filtered.Where(d => d.Action == a);
        }

        return filtered.ToArray();
    }

    public async Task<DecisionRecord?> GetDecisionAsync(string id, CancellationToken cancellationToken = default) =>
        (await ListDecisionsAsync(null, null, cancellationToken).ConfigureAwait(false)).FirstOrDefault(d => d.Id == id);

    public Task<EvidenceDocument?> GetEvidenceAsync(string id, CancellationToken cancellationToken = default)
    {
        if (_runId is null)
        {
            return Task.FromResult<EvidenceDocument?>(null);
        }

        return QueryAsync(
            "evidence.get",
            HostJson.Obj(("run_id", HostJson.Str(_runId)), ("evidence_id", HostJson.Str(id))),
            HostProjection.MapEvidence,
            null,
            _ => null,
            reply => HostProjection.UnavailableEvidence(id, reply),
            null,
            cancellationToken);
    }

    public Task<IReadOnlyList<OrderRecord>> ListOrdersAsync(CancellationToken cancellationToken = default)
    {
        if (_runId is null)
        {
            return Task.FromResult<IReadOnlyList<OrderRecord>>([]);
        }

        return QueryAsync(
            "orders.list",
            HostJson.Obj(("run_id", HostJson.Str(_runId))),
            HostProjection.MapOrders,
            (IReadOnlyList<OrderRecord>)[],
            _ => [],
            _ => [],
            "order_list",
            cancellationToken);
    }

    public async Task<OrderRecord?> GetOrderAsync(string id, CancellationToken cancellationToken = default) =>
        (await ListOrdersAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(o => o.Id == id);

    public async Task<IReadOnlyList<ClientEvent>> ListEventsAsync(CancellationToken cancellationToken = default)
    {
        if (_runId is null || _session is null)
        {
            return [];
        }

        var after = 0L;
        if (long.TryParse(_inbox.LastCursor, out var cursor))
        {
            after = cursor;
        }

        var reply = await CallAsync("events.list", HostJson.Obj(
            ("run_id", HostJson.Str(_runId)),
            ("after_sequence", HostJson.IntValue(after)),
            ("limit", HostJson.IntValue(200))), null, cancellationToken).ConfigureAwait(false);
        if (reply is null)
        {
            return _inbox.Snapshot();
        }

        if (reply["status"].AsString() == "UNAVAILABLE")
        {
            LastQueryFailure = HostQueryFailure.Unavailable;
            _errors.Add("events.list UNAVAILABLE " + HostProjection.FailureText(reply));
            return _inbox.Snapshot();
        }

        if (reply["status"].AsString() != "COMPLETED")
        {
            LastQueryFailure = reply["status"].AsString() == "REJECTED" ? HostQueryFailure.Rejected : HostQueryFailure.Protocol;
            return _inbox.Snapshot();
        }

        var result = ResultOf(reply);
        if (!HostProjection.TryType(result, "event_batch", out var schema))
        {
            LastQueryFailure = HostQueryFailure.Schema;
            _errors.Add(schema);
            return _inbox.Snapshot();
        }

        var mapped = HostProjection.MapEvents(result, out var gap, out _, out var firstAvailable);
        if (gap)
        {
            _inbox.ResetForResync();
            _resyncRequired = true;
            await ResyncAuthoritativeStateAsync(cancellationToken).ConfigureAwait(false);
            if (after != 0)
            {
                var full = await ListEventsAsync(cancellationToken).ConfigureAwait(false);
                if (full.Any(e => e.Kind == "resync" && e.GapBefore))
                {
                    return full;
                }

                return new[] { ResyncRequiredEvent(firstAvailable) }.Concat(full).ToArray();
            }

            foreach (var item in mapped)
            {
                _inbox.Accept(item);
            }

            if (mapped.Count == 0 && firstAvailable > 0)
            {
                _inbox.RememberCursor((firstAvailable - 1).ToString(CultureInfo.InvariantCulture));
            }

            BindRevision(reply);
            LastQueryFailure = HostQueryFailure.None;
            return new[] { ResyncRequiredEvent(firstAvailable) }.Concat(_inbox.Snapshot()).ToArray();
        }

        foreach (var item in mapped)
        {
            _inbox.Accept(item);
        }

        BindRevision(reply);
        LastQueryFailure = HostQueryFailure.None;
        return _inbox.Snapshot();
    }

    public Task<IReadOnlyList<CapabilityItem>> ListCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        QueryAsync(
            "capabilities.list",
            HostJson.Obj(),
            HostProjection.MapCapabilities,
            (IReadOnlyList<CapabilityItem>)[],
            _ => [],
            _ => [],
            "capability_list",
            cancellationToken);

    public Task<EvaluationSummary> GetEvaluationAsync(CancellationToken cancellationToken = default)
    {
        if (_runId is null)
        {
            return Task.FromResult(DisconnectedEvaluation());
        }

        return QueryAsync(
            "evaluation.get",
            HostJson.Obj(("run_id", HostJson.Str(_runId))),
            HostProjection.MapEvaluation,
            DisconnectedEvaluation(),
            result => HostProjection.MapEvaluation(result),
            reply => DisconnectedEvaluation() with
            {
                PlotAbsenceReason = "UNAVAILABLE: " + HostProjection.FailureText(reply),
                SoftwareResult = "unavailable",
                OperationalReadiness = "unavailable"
            },
            "evaluation",
            cancellationToken);
    }

    public async IAsyncEnumerable<ClientEvent> SubscribeAsync(string? resumeCursor, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            _errors.Add("Subscribe requested while disconnected; cursor=" + (resumeCursor ?? "none"));
            yield break;
        }

        var snapshot = await ListEventsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var item in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (resumeCursor is not null && string.CompareOrdinal(item.Cursor, resumeCursor) <= 0)
            {
                continue;
            }

            yield return item;
        }
    }

    public async Task<OperationResult> RequestOperationAsync(OperationRequest request, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new OperationResult(request.Operation, request.IdempotencyKey, OperationStatus.Unknown, null, "Local wait canceled. Backend action is not acknowledged as canceled.", false);
        }

        if (!IsConnected)
        {
            LastQueryFailure = HostQueryFailure.Disconnected;
            return new OperationResult(
                request.Operation,
                request.IdempotencyKey,
                OperationStatus.Unavailable,
                null,
                "HOST_UNAVAILABLE: " + ConnectionReason + " Sensitive operations require an authenticated host capability. The UI cannot grant either.",
                false);
        }

        var started = _guard.TryBegin(request.Operation, request.IdempotencyKey, out var existing);
        if (!started && existing is not null)
        {
            return existing;
        }

        try
        {
            var mapped = MapOperation(request);
            if (mapped is null)
            {
                var unavailable = new OperationResult(request.Operation, request.IdempotencyKey, OperationStatus.Unavailable, null, "Method is not in sovereign.host/v2. Paper/provider commands are not exposed because the envelope can encode them.", false);
                _guard.Complete(request.IdempotencyKey, unavailable);
                LastQueryFailure = HostQueryFailure.Unavailable;
                return unavailable;
            }

            var reply = await CallAsync(mapped.Value.Method, mapped.Value.Params, mapped.Value.Idempotency, cancellationToken).ConfigureAwait(false);
            if (reply is null)
            {
                var unknown = new OperationResult(request.Operation, request.IdempotencyKey, OperationStatus.Unknown, null, "Send completed or interrupted without a verified reply. Status remains UNKNOWN until operations.get.", false);
                _guard.Complete(request.IdempotencyKey, unknown);
                return unknown;
            }

            var result = ResultOf(reply);
            if (mapped.Value.Method == "offline_demo.start")
            {
                _runId = result["run_id"].AsString() ?? _runId;
                _operationId = result["operation_id"].AsString() ?? _operationId;
                if (reply["status"].AsString() == "ACCEPTED" && _operationId is not null)
                {
                    var polled = await PollOperationAsync(_operationId, cancellationToken).ConfigureAwait(false);
                    if (polled is not null)
                    {
                        reply = polled;
                        result = ResultOf(reply);
                        _runId = result["run_id"].AsString() ?? _runId;
                    }
                }

                PersistRecovery(request.IdempotencyKey);
            }

            BindRevision(reply);
            var status = HostProjection.MapStatus(reply["status"].AsString());
            var detail = reply["error"].IsNull
                ? (result["run_id"].AsString() is string run ? "run_id=" + run + " operation_id=" + (result["operation_id"].AsString() ?? "") : "host status " + reply["status"].AsString())
                : reply["error"]["code"].AsString() + ": " + reply["error"]["message"].AsString();
            var op = new OperationResult(request.Operation, request.IdempotencyKey, status, reply["request_id"].AsString(), detail ?? "", false);
            _guard.Complete(request.IdempotencyKey, op);
            return op;
        }
        catch (HostCallCanceledException)
        {
            var unknown = new OperationResult(request.Operation, request.IdempotencyKey, OperationStatus.Unknown, null, "Local wait canceled. Backend action is not acknowledged as canceled.", false);
            _guard.Complete(request.IdempotencyKey, unknown);
            return unknown;
        }
        finally
        {
            _guard.End(request.IdempotencyKey);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync().ConfigureAwait(false);
            _session = null;
        }
    }

    public static SystemHealth DisconnectedHealth() => new(
        new LabeledState<EngineState>(EngineState.Unknown, "Engine: unknown", "host disconnected"),
        new LabeledState<ConnectivityState>(ConnectivityState.Disconnected, "Provider: disconnected", "host disconnected"),
        new LabeledState<ConnectivityState>(ConnectivityState.Disconnected, "Broker: disconnected", "host disconnected"),
        new LabeledState<ReconciliationState>(ReconciliationState.Unknown, "Reconciliation: unknown", "host disconnected"),
        new LabeledState<AuthorizationState>(AuthorizationState.Unknown, "Authorization: unknown", "host disconnected"),
        Presence<DateTimeOffset>.Unknown("next cycle unknown while disconnected"),
        [
            new AttentionItem("host-disconnected", "warning", "Native host is not connected.", "Keep working on local drafts, or wait for a published engine bundle and endpoint-v2.json in an isolated profile.", null, null)
        ],
        DateTimeOffset.UtcNow);

    private static RuntimeIdentity DisconnectedRuntime() => new(
        "desktop-0.1.7-shell-refresh",
        HostV2.Protocol,
        BackendType.Disconnected,
        [],
        DateTimeOffset.UtcNow,
        "Native host disconnected. Desktop remains in Native Connection mode without fixture fallback.");

    private static EvaluationSummary DisconnectedEvaluation() => new(
        "none",
        Presence<int>.Unknown("host disconnected"),
        Presence<string>.Unknown("host disconnected"),
        "not available",
        "not available",
        "not available",
        0,
        0,
        0,
        0,
        "desktop-client-disconnected",
        "native-host-not-connected",
        "INSUFFICIENT_EVIDENCE",
        "No evaluation plot: host did not supply campaign data.");

    private async Task RecoverExistingRunAsync(CancellationToken cancellationToken)
    {
        var reply = await CallAsync("runs.list", HostJson.Obj(), null, cancellationToken).ConfigureAwait(false);
        if (reply is null || reply["status"].AsString() != "COMPLETED")
        {
            return;
        }

        var result = ResultOf(reply);
        if (!HostProjection.TryType(result, "run_list", out var schema))
        {
            LastQueryFailure = HostQueryFailure.Schema;
            _errors.Add("runs.list " + schema);
            return;
        }

        HostJson? chosen = null;
        foreach (var item in result["items"].AsArray())
        {
            if (item["state"].AsString() == "COMPLETED")
            {
                chosen = item;
            }
        }

        if (chosen is null)
        {
            foreach (var item in result["items"].AsArray())
            {
                if (item["state"].AsString() is "RUNNING" or "PENDING")
                {
                    chosen = item;
                }
            }
        }

        if (chosen is null)
        {
            return;
        }

        _runId = chosen["run_id"].AsString() ?? _runId;
        _operationId = chosen["operation_id"].AsString() ?? _operationId;
        if (chosen["state"].AsString() is "RUNNING" or "PENDING" && _operationId is not null)
        {
            var polled = await PollOperationAsync(_operationId, cancellationToken).ConfigureAwait(false);
            if (polled is not null)
            {
                var body = ResultOf(polled);
                _runId = body["run_id"].AsString() ?? _runId;
            }
        }
    }

    private async Task<HostJson?> PollOperationAsync(string operationId, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(180);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reply = await CallAsync(
                "operations.get",
                HostJson.Obj(("operation_id", HostJson.Str(operationId))),
                null,
                cancellationToken).ConfigureAwait(false);
            if (reply is null)
            {
                return null;
            }

            var state = ResultOf(reply)["state"].AsString();
            if (state is not ("PENDING" or "RUNNING"))
            {
                return reply;
            }

            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        }

        _errors.Add("operations.get poll exceeded 180s for " + operationId);
        LastQueryFailure = HostQueryFailure.Protocol;
        return null;
    }

    private void PersistRecovery(string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(_options.DesktopProfileDirectory) || _runId is null)
        {
            return;
        }

        Directory.CreateDirectory(_options.DesktopProfileDirectory);
        var path = Path.Combine(_options.DesktopProfileDirectory, "native-recovery.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            run_id = _runId,
            operation_id = _operationId,
            idempotency_key = idempotencyKey
        }));
    }

    private async Task<T> QueryAsync<T>(
        string method,
        HostJson parameters,
        Func<HostJson, T> mapCompleted,
        T onDisconnected,
        Func<HostJson, T> mapSchema,
        Func<HostJson, T> mapUnavailable,
        string? expectedType,
        CancellationToken cancellationToken)
    {
        await ConnectAsync(cancellationToken).ConfigureAwait(false);
        if (_session is null)
        {
            LastQueryFailure = HostQueryFailure.Disconnected;
            return onDisconnected;
        }

        var reply = await CallAsync(method, parameters, null, cancellationToken).ConfigureAwait(false);
        if (reply is null)
        {
            return LastQueryFailure is HostQueryFailure.Transport or HostQueryFailure.Protocol
                ? onDisconnected
                : onDisconnected;
        }

        var status = reply["status"].AsString();
        if (status == "UNAVAILABLE")
        {
            LastQueryFailure = HostQueryFailure.Unavailable;
            _errors.Add(method + " UNAVAILABLE " + HostProjection.FailureText(reply));
            return mapUnavailable(reply);
        }

        if (status == "REJECTED")
        {
            LastQueryFailure = HostQueryFailure.Rejected;
            _errors.Add(method + " REJECTED " + HostProjection.FailureText(reply));
            return mapSchema(ResultOf(reply));
        }

        if (status != "COMPLETED")
        {
            LastQueryFailure = HostQueryFailure.Protocol;
            _errors.Add(method + " status " + status);
            return mapSchema(ResultOf(reply));
        }

        var result = ResultOf(reply);
        if (expectedType is not null && !HostProjection.TryType(result, expectedType, out var schema))
        {
            LastQueryFailure = HostQueryFailure.Schema;
            _errors.Add(method + " " + schema);
            return mapSchema(result);
        }

        BindRevision(reply);
        LastQueryFailure = HostQueryFailure.None;
        return mapCompleted(result);
    }

    private async Task<HostJson?> CallAsync(string method, HostJson parameters, string? idempotency, CancellationToken cancellationToken)
    {
        await ConnectAsync(cancellationToken).ConfigureAwait(false);
        if (_session is null)
        {
            LastQueryFailure = HostQueryFailure.Disconnected;
            return null;
        }

        try
        {
            return await _session.CallAsync(method, parameters, idempotency, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        }
        catch (HostCallCanceledException)
        {
            throw;
        }
        catch (HostCallUnknownException ex)
        {
            LastQueryFailure = HostQueryFailure.Transport;
            _errors.Add("TRANSPORT " + ex.Code);
            await DropSessionAsync().ConfigureAwait(false);
            return null;
        }
        catch (HostProtocolException ex)
        {
            LastQueryFailure = HostQueryFailure.Protocol;
            _errors.Add("PROTOCOL " + ex.Code);
            ConnectionReason = "Host protocol error: " + ex.Code + " Session is not treated as a healthy runtime. Preview fixtures are not substituted.";
            if (ex.Code is "INVALID_MAC" or "REPLY_MISMATCH" or "SESSION_REJECTED" or "ENDPOINT_MISMATCH")
            {
                await DropSessionAsync().ConfigureAwait(false);
            }

            return null;
        }
        catch (Exception ex)
        {
            LastQueryFailure = HostQueryFailure.Transport;
            _errors.Add(ex.Message);
            ConnectionReason = "Host transport failed: " + ex.Message + " Preview fixtures are not substituted.";
            await DropSessionAsync().ConfigureAwait(false);
            return null;
        }
    }

    private async Task DropSessionAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync().ConfigureAwait(false);
            _session = null;
        }
    }

    private void BindRevision(HostJson reply)
    {
        var revision = reply["snapshot_revision"].AsInt();
        if (revision is long r)
        {
            if (_snapshotRevision is long previous && r < previous)
            {
                _resyncRequired = true;
            }

            _snapshotRevision = r;
        }

        var run = ResultOf(reply)["run_id"].AsString();
        if (run is not null)
        {
            _runId = run;
        }
    }

    private static ClientEvent ResyncRequiredEvent(long firstAvailable) => new(
        "resync-required",
        firstAvailable.ToString(CultureInfo.InvariantCulture),
        DateTimeOffset.UtcNow,
        "resync",
        "Event cursor gap. Mixed projections discarded. Full query resynchronization required.",
        null,
        null,
        true);

    private static HostJson ResultOf(HostJson reply) => reply["result"].IsObject ? reply["result"] : HostJson.Obj();

    private static (string Method, HostJson Params, string? Idempotency)? MapOperation(OperationRequest request)
    {
        if (request.Operation is "offline_demo.start" or "START_OFFLINE_ENGINE_RUN")
        {
            return ("offline_demo.start", HostJson.Obj(("scenario", HostJson.Str("three-cycle-v1"))), request.IdempotencyKey);
        }

        if (request.Operation is "operations.get" && request.Arguments.TryGetValue("operation_id", out var getId))
        {
            return ("operations.get", HostJson.Obj(("operation_id", HostJson.Str(getId))), request.IdempotencyKey);
        }

        if (request.Operation is "operations.cancel" or "cancel-order" && request.Arguments.TryGetValue("operation_id", out var cancelId))
        {
            return ("operations.cancel", HostJson.Obj(("operation_id", HostJson.Str(cancelId))), request.IdempotencyKey);
        }

        if (request.Operation == "session.revoke")
        {
            return ("session.revoke", HostJson.Obj(), request.IdempotencyKey);
        }

        return null;
    }
}
