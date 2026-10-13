using System.Buffers.Binary;
using System.IO.Pipes;
using Sovereign.Desktop.Client;
using Sovereign.Desktop.Client.Adapters;
using Sovereign.Desktop.Client.Host;
using Sovereign.Desktop.Core;
using Sovereign.Desktop.Client.Storage;
using Sovereign.Desktop.Presentation;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class NativePipeTests
{
    [Fact]
    public void Discovery_refuses_native_repo_and_does_not_guess_a_pipe()
    {
        Assert.False(HostDiscovery.IsAuthorizedProfile(@"C:\Windows\System32\drivers", out var reason));
        Assert.Contains("outside", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Null(HostDiscovery.TryRead(Path.GetTempPath().TrimEnd('\\'), out var missing));
        Assert.Contains("endpoint-v2.json", missing, StringComparison.Ordinal);
        Assert.False(HostDiscovery.IsAuthorizedProfile(@"C:\Windows\System32\config", out var grok));
        Assert.Contains("outside", grok, StringComparison.OrdinalIgnoreCase);
        Assert.False(HostDiscovery.IsAuthorizedProfile(@"C:\Windows\System32\spool", out var stage));
        Assert.Contains("outside", stage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Discovery_reads_host_instance_id()
    {
        var profile = Path.Combine(Path.GetTempPath(), "sov-disc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "endpoint-v2.json"),
            """{"pipe_name":"\\\\.\\pipe\\SovereignAI.Offline.x.v2","host_instance_id":"host-1","profile_id":"grok-offline-integration","bundle_version":"2.0.0","pid":1}""");
        var endpoint = HostDiscovery.TryRead(profile, out var reason);
        Assert.Equal("ok", reason);
        Assert.NotNull(endpoint);
        Assert.Equal("host-1", endpoint!.InstanceId);
        Assert.Equal("2.0.0", endpoint.BundleId);
    }

    [Fact]
    public async Task Transport_seam_runs_after_send_and_leaves_reply_validation_intact()
    {
        var (profile, server) = StartMock();
        await using var _ = server;
        var seen = new List<string>();
        var seam = new HostTransportSeam
        {
            AfterSendBeforeReply = (ctx, _) =>
            {
                seen.Add(ctx.Method);
                Assert.True(ctx.Sent);
                return Task.CompletedTask;
            }
        };
        await using var client = new NativeOperatorClient(new NativeClientOptions(profile, "Sovereign.Desktop", ShellViewModel.AppVersion, profile, seam));
        await client.ConnectAsync();
        Assert.True(client.IsConnected, client.ConnectionReason);
        Assert.DoesNotContain("session_open", seen);
        var started = await client.RequestOperationAsync(new OperationRequest("offline_demo.start", NativeOperatorClient.OfflineDemoIdempotencyKey, null, new Dictionary<string, string>()));
        Assert.Equal(OperationStatus.Completed, started.Status);
        Assert.Contains("offline_demo.start", seen);
        Assert.Contains("run_id=run-three-cycle-1", started.Detail, StringComparison.Ordinal);
        Assert.False(started.SimulatedPreviewAcknowledgement);
    }

    [Fact]
    public async Task Mock_server_session_and_runtime_are_not_preview_fixtures()
    {
        var (profile, server) = StartMock();
        await using var _ = server;
        await using var client = new NativeOperatorClient(new NativeClientOptions(profile, "Sovereign.Desktop", "0.1.2-integration", profile));
        await client.ConnectAsync();
        Assert.True(client.IsConnected, client.ConnectionReason);
        Assert.Equal(HostQueryFailure.None, client.LastQueryFailure);
        var runtime = await client.GetRuntimeAsync();
        Assert.Equal(BackendType.NativeHost, runtime.BackendType);
        Assert.Equal(HostV2.Protocol, runtime.ProtocolVersion);
        Assert.Contains("trading_authority=NONE", runtime.IntegrationStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("preview-paper", runtime.IntegrationStatus, StringComparison.OrdinalIgnoreCase);
        var caps = await client.ListCapabilitiesAsync();
        Assert.Contains(caps, c => c.Id == "offline_demo.start" && c.Status == "AVAILABLE");
        var first = await client.RequestOperationAsync(new OperationRequest("offline_demo.start", NativeOperatorClient.OfflineDemoIdempotencyKey, null, new Dictionary<string, string>()));
        var second = await client.RequestOperationAsync(new OperationRequest("offline_demo.start", NativeOperatorClient.OfflineDemoIdempotencyKey, null, new Dictionary<string, string>()));
        Assert.Equal(first.Detail, second.Detail);
        Assert.Equal(1, server.StartCalls);
        Assert.Contains("run_id=run-three-cycle-1", first.Detail, StringComparison.Ordinal);
        Assert.False(first.SimulatedPreviewAcknowledgement);
        var pause = await client.RequestOperationAsync(new OperationRequest("PAUSE_NEW_RISK", "pause-1", null, new Dictionary<string, string>()));
        Assert.Equal(OperationStatus.Unavailable, pause.Status);
        Assert.Equal(HostQueryFailure.Unavailable, client.LastQueryFailure);
        Assert.True(client.IsConnected);
        var account = await client.GetAccountAsync();
        Assert.True(account.IsKnown);
        Assert.Equal(91100, account.Value!.Cash.Value!.MinorUnits);
        Assert.Equal(101000, account.Value.EndingEquity.Value!.MinorUnits);
        Assert.Equal(11m, account.Value.Holdings.Single(h => h.Instrument == "AAA").Quantity.Value);
        Assert.Equal(0, account.Value.Reservations.Value!.MinorUnits);
        Assert.Equal("NONE", account.Value.TradingAuthority);
    }

    [Fact]
    public async Task Reopen_recovers_run_without_start_and_retry_is_separate()
    {
        var (profile, server) = StartMock();
        await using var _ = server;
        await using (var first = new NativeOperatorClient(new NativeClientOptions(profile, "Sovereign.Desktop", "0.1.2-integration", profile)))
        {
            await first.ConnectAsync();
            Assert.Null(first.ActiveRunId);
            var started = await first.RequestOperationAsync(new OperationRequest("offline_demo.start", NativeOperatorClient.OfflineDemoIdempotencyKey, null, new Dictionary<string, string>()));
            Assert.Contains("run-three-cycle-1", started.Detail, StringComparison.Ordinal);
            Assert.Equal(1, server.StartCalls);
        }

        await using var reopened = new NativeOperatorClient(new NativeClientOptions(profile, "Sovereign.Desktop", "0.1.2-integration", profile));
        await reopened.ConnectAsync();
        Assert.True(reopened.IsConnected);
        Assert.Equal("run-three-cycle-1", reopened.ActiveRunId);
        Assert.Equal(1, server.StartCalls);
        var recovered = await reopened.GetAccountAsync();
        Assert.True(recovered.IsKnown);
        Assert.Equal(101000, recovered.Value!.EndingEquity.Value!.MinorUnits);
        var orders = await reopened.ListOrdersAsync();
        Assert.Equal(3, orders.Count);

        var retry = await reopened.RequestOperationAsync(new OperationRequest("offline_demo.start", NativeOperatorClient.OfflineDemoIdempotencyKey, null, new Dictionary<string, string>()));
        Assert.Equal(2, server.StartCalls);
        Assert.Contains("op-demo-1", retry.Detail, StringComparison.Ordinal);
        Assert.Equal("run-three-cycle-1", reopened.ActiveRunId);
        Assert.Equal(3, (await reopened.ListOrdersAsync()).Count);
    }

    [Fact]
    public async Task Evidence_unavailable_stays_connected_and_is_api_negative()
    {
        var (profile, server) = StartMock();
        await using var _ = server;
        await using var client = new NativeOperatorClient(new NativeClientOptions(profile, "Sovereign.Desktop", "0.1.2-integration", profile));
        await client.ConnectAsync();
        await client.RequestOperationAsync(new OperationRequest("offline_demo.start", NativeOperatorClient.OfflineDemoIdempotencyKey, null, new Dictionary<string, string>()));
        var doc = await client.GetEvidenceAsync("probe-unavailable");
        Assert.True(client.IsConnected);
        Assert.Equal(HostQueryFailure.Unavailable, client.LastQueryFailure);
        Assert.NotNull(doc);
        Assert.StartsWith("API-NEGATIVE", doc!.Title, StringComparison.Ordinal);
        Assert.Contains("api-negative-evidence-get", doc.MediaType, StringComparison.Ordinal);
        Assert.Equal(BackendType.NativeHost, (await client.GetRuntimeAsync()).BackendType);
    }

    [Fact]
    public async Task Event_gap_resets_inbox_and_requires_full_resync()
    {
        var (profile, server) = StartMock();
        server.GapOnZero = true;
        await using var _ = server;
        await using var client = new NativeOperatorClient(new NativeClientOptions(profile, "Sovereign.Desktop", "0.1.2-integration", profile));
        await client.ConnectAsync();
        await client.RequestOperationAsync(new OperationRequest("offline_demo.start", NativeOperatorClient.OfflineDemoIdempotencyKey, null, new Dictionary<string, string>()));
        var events = await client.ListEventsAsync();
        Assert.True(client.ResyncRequired);
        Assert.Contains(events, e => e.Kind == "resync");
        Assert.True(client.ConsumeResyncRequired());
        server.GapOnZero = false;
        var again = await client.ListEventsAsync();
        Assert.False(client.ResyncRequired);
        Assert.DoesNotContain(again, e => e.Kind == "resync" && e.GapBefore);
    }

    [Fact]
    public async Task Event_gap_on_incremental_cursor_requeries_from_zero()
    {
        var (profile, server) = StartMock();
        await using var _ = server;
        await using var client = new NativeOperatorClient(new NativeClientOptions(profile, "Sovereign.Desktop", "0.1.2-integration", profile));
        await client.ConnectAsync();
        await client.RequestOperationAsync(new OperationRequest("offline_demo.start", NativeOperatorClient.OfflineDemoIdempotencyKey, null, new Dictionary<string, string>()));
        var first = await client.ListEventsAsync();
        Assert.False(client.ResyncRequired);
        Assert.DoesNotContain(first, e => e.Kind == "resync");
        Assert.Contains(0L, server.EventsAfterSequences);

        server.GapAfterPositive = true;
        var gapped = await client.ListEventsAsync();
        Assert.True(client.ResyncRequired);
        Assert.Contains(gapped, e => e.Kind == "resync");
        Assert.Contains(server.EventsAfterSequences, seq => seq > 0);
        var afterGap = server.EventsAfterSequences.ToArray();
        var positive = Array.FindIndex(afterGap, seq => seq > 0);
        Assert.True(positive >= 0);
        Assert.Contains(afterGap.Skip(positive), seq => seq == 0);
    }

    [Fact]
    public async Task Start_offline_run_refreshes_account_orders_and_does_not_say_disconnected_or_live()
    {
        var (hostProfile, server) = StartMock();
        await using var _ = server;
        var desktopProfile = Path.Combine(Path.GetTempPath(), "sov-refresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(desktopProfile);
        var fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures");
        var vm = new ShellViewModel(
            new NativeOperatorClient(new NativeClientOptions(hostProfile, "Sovereign.Desktop", ShellViewModel.AppVersion, desktopProfile)),
            new FileDraftStore(desktopProfile),
            desktopProfile,
            fixtures,
            hostProfile);
        await vm.RefreshAsync();
        Assert.False(vm.Account.IsKnown);
        var first = await vm.StartOfflineEngineRunAsync();
        Assert.Equal(OperationStatus.Completed, first.Status);
        Assert.False(first.SimulatedPreviewAcknowledgement);
        Assert.True(vm.Account.IsKnown);
        Assert.Equal(91100, vm.Account.Value!.Cash.Value!.MinorUnits);
        Assert.Equal(101000, vm.Account.Value.EndingEquity.Value!.MinorUnits);
        Assert.Equal(3, vm.Orders.Count);
        Assert.Equal(3, vm.Decisions.Count);
        Assert.NotNull(vm.Evaluation);
        Assert.NotEqual("none", vm.Evaluation!.CampaignId);
        Assert.DoesNotContain("host disconnected", vm.Evaluation.SampleCount.Describe(), StringComparison.OrdinalIgnoreCase);
        Assert.True(vm.IsNativeAuthenticated);
        Assert.DoesNotContain("disconnected", vm.ModeBanner, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Disconnected native mode", vm.WorkspaceGuidance, StringComparison.Ordinal);
        Assert.DoesNotContain("disconnected", vm.WorkspaceGuidance, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Offline synthetic", vm.WorkspaceGuidance, StringComparison.Ordinal);
        Assert.Contains("Not a live market", vm.WorkspaceGuidance, StringComparison.Ordinal);
        var shown = ShellViewModel.FormatHostOperation(first);
        Assert.DoesNotContain("simulated=", shown, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Offline synthetic", shown, StringComparison.Ordinal);
        Assert.Contains("not a live market", shown, StringComparison.OrdinalIgnoreCase);
        var starts = server.StartCalls;
        var second = await vm.StartOfflineEngineRunAsync();
        Assert.Equal(first.Detail, second.Detail);
        Assert.Equal(starts, server.StartCalls);
        Assert.Equal(3, vm.Orders.Count);
        Directory.Delete(desktopProfile, true);
    }

    [Fact]
    public async Task Event_gap_resync_requeries_run_operation_account_orders_decisions_without_start()
    {
        var (hostProfile, server) = StartMock();
        await using var _ = server;
        var desktopProfile = Path.Combine(Path.GetTempPath(), "sov-resync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(desktopProfile);
        var fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures");
        var vm = new ShellViewModel(
            new NativeOperatorClient(new NativeClientOptions(hostProfile, "Sovereign.Desktop", ShellViewModel.AppVersion, desktopProfile)),
            new FileDraftStore(desktopProfile),
            desktopProfile,
            fixtures,
            hostProfile);
        await vm.RefreshAsync();
        var started = await vm.StartOfflineEngineRunAsync();
        Assert.False(started.SimulatedPreviewAcknowledgement);
        await vm.RefreshAsync();
        var starts = server.StartCalls;
        var runsBefore = server.Methods.Count(m => m == "runs.list");
        var opsBefore = server.Methods.Count(m => m == "operations.get");
        var accountBefore = server.Methods.Count(m => m == "account.get");
        var ordersBefore = server.Methods.Count(m => m == "orders.list");
        var decisionsBefore = server.Methods.Count(m => m == "decisions.list");

        server.GapAfterPositive = true;
        await vm.RefreshAsync();

        Assert.Equal(starts, server.StartCalls);
        Assert.True(server.Methods.Count(m => m == "runs.list") > runsBefore);
        Assert.True(server.Methods.Count(m => m == "operations.get") > opsBefore);
        Assert.True(server.Methods.Count(m => m == "account.get") > accountBefore);
        Assert.True(server.Methods.Count(m => m == "orders.list") > ordersBefore);
        Assert.True(server.Methods.Count(m => m == "decisions.list") > decisionsBefore);
        Assert.Equal(101000, vm.Account.Value!.EndingEquity.Value!.MinorUnits);
        Assert.Equal(3, vm.Orders.Count);
        Assert.Equal(3, vm.Decisions.Count);
        var native = (NativeOperatorClient)vm.Client;
        Assert.Equal("run-three-cycle-1", native.ActiveRunId);
        Assert.Equal("op-demo-1", native.ActiveOperationId);
    }

    [Fact]
    public async Task Native_walkthrough_on_mock_covers_reopen_and_api_negative()
    {
        var (hostProfile, server) = StartMock();
        await using var _ = server;
        var desktopProfile = Path.Combine(Path.GetTempPath(), "sov-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(desktopProfile);
        var fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures");
        var vm = new ShellViewModel(
            new NativeOperatorClient(new NativeClientOptions(hostProfile, "Sovereign.Desktop", ShellViewModel.AppVersion, desktopProfile)),
            new FileDraftStore(desktopProfile),
            desktopProfile,
            fixtures,
            hostProfile);
        var receipt = await NativeIntegrationWalkthrough.RunAsync(vm);
        Assert.True(receipt.Passed, string.Join(Environment.NewLine, receipt.Steps.Select(s => $"{s.Id}: {s.Passed} {s.Detail}")));
        Assert.Contains(receipt.Steps, s => s.Id == "reopen-recovers-without-start" && s.Passed);
        Assert.Contains(receipt.Steps, s => s.Id == "explicit-idempotent-retry" && s.Passed);
        Assert.Contains(receipt.Steps, s => s.Id == "api-negative-evidence-get" && s.Passed);
        Assert.Contains(receipt.Steps, s => s.Id == "evidence-selection-journey" && s.Passed);
        Assert.Equal(2, server.StartCalls);
    }

    private static (string Profile, MockV2Server Server) StartMock()
    {
        var profile = Path.Combine(Path.GetTempPath(), "sov-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        var id = InstalledHostSupervisor.ProfileIdFor(profile);
        var pipe = InstalledHostSupervisor.ExpectedPipeName(id);
        var secret = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var server = new MockV2Server(pipe, id, "inst-test", secret);
        server.Start();
        File.WriteAllText(Path.Combine(profile, "endpoint-v2.json"),
            $$"""{"pipe_name":{{System.Text.Json.JsonSerializer.Serialize(pipe)}},"host_instance_id":"inst-test","profile_id":{{System.Text.Json.JsonSerializer.Serialize(id)}},"protocol":"sovereign.host/v2","bundle_version":"2.0.0"}""");
        return (profile, server);
    }
}

internal sealed class MockV2Server : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly string _pipe;
    private readonly string _profile;
    private readonly string _instance;
    private readonly byte[] _secret;
    private Task? _task;
    private string? _startedRun;

    public MockV2Server(string pipeName, string profileId, string instanceId, byte[] secret)
    {
        _pipe = NamedPipeTransport.NormalizePipeName(pipeName);
        _profile = profileId;
        _instance = instanceId;
        _secret = secret;
    }

    public int StartCalls { get; private set; }
    public bool GapOnZero { get; set; }
    public bool GapAfterPositive { get; set; }
    public List<long> EventsAfterSequences { get; } = [];
    public List<string> Methods { get; } = [];

    public void Start() => _task = Task.Run(() => RunAsync(_cts.Token));

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(_pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try
            {
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                await HandleAsync(server, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
            }
            finally
            {
                await server.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        var header = await ReadExact(server, 4, ct).ConfigureAwait(false);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(header);
        var body = await ReadExact(server, (int)size, ct).ConfigureAwait(false);
        var message = HostV2.ParseJsonObject(body);
        HostJson reply;
        if (message["kind"].AsString() == "session_open")
        {
            reply = HostJson.Obj(
                ("protocol", HostJson.Str(HostV2.Protocol)),
                ("kind", HostJson.Str("session_opened")),
                ("request_id", message["request_id"]),
                ("status", HostJson.Str("COMPLETED")),
                ("endpoint", HostJson.Obj(
                    ("pipe_name", HostJson.Str(@"\\.\pipe\" + _pipe)),
                    ("instance_id", HostJson.Str(_instance)),
                    ("profile_id", HostJson.Str(_profile)))),
                ("session", HostJson.Obj(
                    ("session_id", HostJson.Str("session-test")),
                    ("operator_sid", HostJson.Str("S-1-test")),
                    ("issued_at", HostJson.Str("2026-09-15T00:00:00Z")),
                    ("expires_at", HostJson.Str("2026-09-15T00:30:00Z")),
                    ("authenticator", HostJson.Obj(
                        ("algorithm", HostJson.Str("HMAC-SHA-256")),
                        ("key_b64", HostJson.Str(HostV2.SecretToB64(_secret))))))));
        }
        else
        {
            HostV2.VerifyMac(message, _secret);
            reply = HandleRequest(message);
        }

        var frame = HostV2.EncodeFrame(reply);
        await server.WriteAsync(frame, ct).ConfigureAwait(false);
        await server.FlushAsync(ct).ConfigureAwait(false);
    }

    private HostJson HandleRequest(HostJson request)
    {
        var method = request["method"].AsString();
        Methods.Add(method ?? "");
        HostJson result;
        var status = "COMPLETED";
        HostJson error = HostJson.Null;
        switch (method)
        {
            case "runtime.get":
                result = HostJson.Obj(
                    ("type", HostJson.Str("runtime")),
                    ("protocol", HostJson.Str(HostV2.Protocol)),
                    ("bundle_version", HostJson.Str("2.0.0")),
                    ("engine_version", HostJson.Str("n2-offline-2.0.0")),
                    ("python_version", HostJson.Str("3.12.10")),
                    ("profile_id", HostJson.Str(_profile)),
                    ("mode", HostJson.Str("OFFLINE")),
                    ("data_class", HostJson.Str("SYNTHETIC")),
                    ("trading_authority", HostJson.Str("NONE")));
                break;
            case "health.get":
                result = HostJson.Obj(
                    ("type", HostJson.Str("health")),
                    ("status", HostJson.Str("READY")),
                    ("host_instance_id", HostJson.Str(_instance)),
                    ("profile_id", HostJson.Str(_profile)),
                    ("snapshot_revision", HostJson.IntValue(1)),
                    ("paper", HostJson.Str("UNAVAILABLE")),
                    ("providers", HostJson.Str("UNAVAILABLE")),
                    ("checked_at", HostJson.Str("2026-09-15T00:00:00Z")));
                break;
            case "capabilities.list":
                result = HostJson.Obj(
                    ("type", HostJson.Str("capability_list")),
                    ("items", HostJson.Arr([
                        HostJson.Obj(("id", HostJson.Str("offline_demo.start")), ("status", HostJson.Str("AVAILABLE")), ("reason", HostJson.Null)),
                        HostJson.Obj(("id", HostJson.Str("evidence.get")), ("status", HostJson.Str("UNAVAILABLE")), ("reason", HostJson.Str("EVIDENCE_NOT_EXPOSED_IN_OFFLINE_N2")))
                    ])));
                break;
            case "runs.list":
                result = HostJson.Obj(
                    ("type", HostJson.Str("run_list")),
                    ("items", _startedRun is null ? HostJson.Arr([]) : HostJson.Arr([
                        HostJson.Obj(
                            ("run_id", HostJson.Str(_startedRun)),
                            ("operation_id", HostJson.Str("op-demo-1")),
                            ("state", HostJson.Str("COMPLETED")),
                            ("mode", HostJson.Str("OFFLINE")),
                            ("data_class", HostJson.Str("SYNTHETIC")),
                            ("trading_authority", HostJson.Str("NONE")))
                    ])));
                break;
            case "runs.get":
                result = HostJson.Obj(
                    ("type", HostJson.Str("run")),
                    ("run_id", HostJson.Str(_startedRun ?? "run-three-cycle-1")),
                    ("operation_id", HostJson.Str("op-demo-1")),
                    ("state", HostJson.Str("COMPLETED")),
                    ("mode", HostJson.Str("OFFLINE")),
                    ("data_class", HostJson.Str("SYNTHETIC")),
                    ("trading_authority", HostJson.Str("NONE")));
                break;
            case "offline_demo.start":
                StartCalls++;
                _startedRun ??= "run-three-cycle-1";
                result = HostJson.Obj(
                    ("type", HostJson.Str("operation")),
                    ("operation_id", HostJson.Str("op-demo-1")),
                    ("idempotency_key", HostJson.Str(request["idempotency_key"].AsString() ?? NativeOperatorClient.OfflineDemoIdempotencyKey)),
                    ("run_id", HostJson.Str(_startedRun)),
                    ("state", HostJson.Str("COMPLETED")),
                    ("created_at", HostJson.Str("2026-09-15T00:00:00Z")),
                    ("updated_at", HostJson.Str("2026-09-15T00:00:01Z")),
                    ("error", HostJson.Null));
                break;
            case "operations.get":
                result = HostJson.Obj(
                    ("type", HostJson.Str("operation")),
                    ("operation_id", HostJson.Str("op-demo-1")),
                    ("idempotency_key", HostJson.Str(NativeOperatorClient.OfflineDemoIdempotencyKey)),
                    ("run_id", HostJson.Str(_startedRun ?? "run-three-cycle-1")),
                    ("state", HostJson.Str("COMPLETED")),
                    ("created_at", HostJson.Str("2026-09-15T00:00:00Z")),
                    ("updated_at", HostJson.Str("2026-09-15T00:00:01Z")),
                    ("error", HostJson.Null));
                break;
            case "account.get":
                result = AccountResult();
                break;
            case "orders.list":
                result = HostJson.Obj(
                    ("type", HostJson.Str("order_list")),
                    ("run_id", HostJson.Str(_startedRun ?? "run-three-cycle-1")),
                    ("items", HostJson.Arr([
                        Order("cid-1"), Order("cid-2"), Order("cid-3")
                    ])));
                break;
            case "decisions.list":
                result = HostJson.Obj(
                    ("type", HostJson.Str("decision_list")),
                    ("run_id", HostJson.Str(_startedRun ?? "run-three-cycle-1")),
                    ("items", HostJson.Arr([
                        HostJson.Obj(("cycle_id", HostJson.Str("c1")), ("coverage", HostJson.Str("ON_TIME_DECISION"))),
                        HostJson.Obj(("cycle_id", HostJson.Str("c2")), ("coverage", HostJson.Str("ON_TIME_DECISION"))),
                        HostJson.Obj(("cycle_id", HostJson.Str("c3")), ("coverage", HostJson.Str("ON_TIME_DECISION")))
                    ])));
                break;
            case "evaluation.get":
                result = HostJson.Obj(
                    ("type", HostJson.Str("evaluation")),
                    ("run_id", HostJson.Str(_startedRun ?? "run-three-cycle-1")),
                    ("value", HostJson.Obj(
                        ("ratio", HostJson.Str("0.1")),
                        ("actual_account", HostJson.Obj(
                            ("cash", HostJson.IntValue(91100)),
                            ("positions", HostJson.Obj(("AAA", HostJson.IntValue(11)))))))));
                break;
            case "events.list":
                var after = request["params"]["after_sequence"].AsInt() ?? 0;
                EventsAfterSequences.Add(after);
                var gap = (GapOnZero && after == 0) || (GapAfterPositive && after > 0);
                result = HostJson.Obj(
                    ("type", HostJson.Str("event_batch")),
                    ("first_available", HostJson.IntValue(gap ? 2 : 1)),
                    ("last_available", HostJson.IntValue(2)),
                    ("next_cursor", HostJson.IntValue(2)),
                    ("gap", gap ? HostJson.True : HostJson.False),
                    ("events", HostJson.Arr([
                        HostJson.Obj(
                            ("sequence", HostJson.IntValue(2)),
                            ("revision", HostJson.IntValue(1)),
                            ("run_id", HostJson.Str(_startedRun ?? "run-three-cycle-1")),
                            ("kind", HostJson.Str("run")),
                            ("at_utc", HostJson.Str("2026-09-15T00:00:00Z")),
                            ("body", HostJson.Obj(("state", HostJson.Str("COMPLETED")))))
                    ])));
                break;
            case "evidence.get":
            case "evidence.list":
                status = "UNAVAILABLE";
                result = HostJson.Obj(
                    ("type", HostJson.Str("availability")),
                    ("status", HostJson.Str("UNAVAILABLE")),
                    ("reason", HostJson.Str("EVIDENCE_NOT_EXPOSED_IN_OFFLINE_N2")));
                error = HostJson.Obj(
                    ("code", HostJson.Str("EVIDENCE_NOT_EXPOSED_IN_OFFLINE_N2")),
                    ("message", HostJson.Str("EVIDENCE_NOT_EXPOSED_IN_OFFLINE_N2")),
                    ("retryable", HostJson.False));
                break;
            default:
                result = HostJson.Obj(("type", HostJson.Str("availability")), ("status", HostJson.Str("UNAVAILABLE")), ("reason", HostJson.Str("UNKNOWN_METHOD")));
                status = "REJECTED";
                error = HostJson.Obj(("code", HostJson.Str("UNKNOWN_METHOD")), ("message", HostJson.Str("UNKNOWN_METHOD")), ("retryable", HostJson.False));
                break;
        }

        var unsigned = HostJson.Obj(
            ("protocol", HostJson.Str(HostV2.Protocol)),
            ("kind", HostJson.Str("reply")),
            ("request_id", request["request_id"]),
            ("session_id", request["session_id"]),
            ("server_nonce", HostJson.Str(HostV2.NewNonce())),
            ("status", HostJson.Str(status)),
            ("snapshot_revision", HostJson.IntValue(1)),
            ("result", result),
            ("error", error),
            ("mac", HostJson.Str(new string('0', 64))));
        return HostV2.Sign(unsigned, _secret);
    }

    private static HostJson AccountResult() => HostJson.Obj(
        ("type", HostJson.Str("account")),
        ("run_id", HostJson.Str("run-three-cycle-1")),
        ("cash_cents", HostJson.IntValue(91100)),
        ("positions", HostJson.Obj(("AAA", HostJson.IntValue(11)))),
        ("reserved_cash_cents", HostJson.IntValue(0)),
        ("ending_equity_cents", HostJson.IntValue(101000)),
        ("trading_authority", HostJson.Str("NONE")));

    private static HostJson Order(string cid) => HostJson.Obj(
        ("client_order_id", HostJson.Str(cid)),
        ("id", HostJson.Str("broker-" + cid)),
        ("symbol", HostJson.Str("AAA")),
        ("qty", HostJson.Str("1")),
        ("side", HostJson.Str("buy")),
        ("status", HostJson.Str("filled")));

    private static async Task<byte[]> ReadExact(Stream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct).ConfigureAwait(false);
            if (n == 0)
            {
                throw new EndOfStreamException();
            }

            offset += n;
        }

        return buffer;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_task is not null)
        {
            try
            {
                await _task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts.Dispose();
    }
}
