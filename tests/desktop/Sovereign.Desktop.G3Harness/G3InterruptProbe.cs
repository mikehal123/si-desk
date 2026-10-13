using System.Text.Json;
using Sovereign.Desktop.Client.Adapters;
using Sovereign.Desktop.Client.Host;
using Sovereign.Desktop.Core;

namespace Sovereign.Desktop.G3Harness;

public sealed record G3ProbeReceipt(
    bool Passed,
    string Client,
    string InterruptKind,
    string AfterDispatchStatus,
    string AfterDispatchDetail,
    int StartCallsAtInterrupt,
    string? RunIdAfterInterrupt,
    string? OperationIdAfterInterrupt,
    string ReconcileMethod,
    string AfterReconcileStatus,
    string? RunIdAfterReconcile,
    string? OperationIdAfterReconcile,
    int StartCallsAfterRetry,
    string? RunIdAfterRetry,
    string? OperationIdAfterRetry,
    int OperationsGetCalls,
    string Summary);

public static class G3InterruptProbe
{
    public static async Task<G3ProbeReceipt> RunAsync(CancellationToken cancellationToken = default)
    {
        var profile = Path.Combine(Path.GetTempPath(), "sov-g3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        var id = InstalledHostSupervisor.ProfileIdFor(profile);
        var pipe = InstalledHostSupervisor.ExpectedPipeName(id);
        var secret = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        await using var server = new G3HoldServer(pipe, id, "inst-g3", secret);
        server.Start();
        File.WriteAllText(
            Path.Combine(profile, "endpoint-v2.json"),
            "{\"pipe_name\":" + JsonSerializer.Serialize(pipe) +
            ",\"host_instance_id\":\"inst-g3\",\"profile_id\":" + JsonSerializer.Serialize(id) +
            ",\"protocol\":\"sovereign.host/v2\",\"bundle_version\":\"2.0.2\"}");

        await using var client = new NativeOperatorClient(new NativeClientOptions(profile, "Sovereign.Desktop.G3Harness", "g3-external", profile));
        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        if (!client.IsConnected)
        {
            return Fail("CLIENT_NOT_CONNECTED " + client.ConnectionReason, server, "Unknown", "", 0, null, null, "none", "Unknown", null, null, 0, null, null);
        }

        using var interrupt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var start = client.RequestOperationAsync(
            new OperationRequest("offline_demo.start", NativeOperatorClient.OfflineDemoIdempotencyKey, null, new Dictionary<string, string>()),
            interrupt.Token);
        using var waitDispatch = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await server.Dispatched.WaitAsync(waitDispatch.Token).ConfigureAwait(false);
        var startsAtInterrupt = server.StartCalls;
        if (startsAtInterrupt < 1)
        {
            return Fail("NOT_DISPATCHED: host never recorded offline_demo.start.", server, "Unknown", "", startsAtInterrupt, client.ActiveRunId, client.ActiveOperationId, "none", "Unknown", null, null, server.StartCalls, null, null);
        }

        await interrupt.CancelAsync().ConfigureAwait(false);
        var interrupted = await start.ConfigureAwait(false);
        var runAfterInterrupt = client.ActiveRunId;
        var opAfterInterrupt = client.ActiveOperationId;
        server.ReleaseHeldReply();

        if (interrupted.Status != OperationStatus.Unknown)
        {
            return Fail(
                "AFTER_DISPATCH_NOT_UNKNOWN: " + interrupted.Status + " " + interrupted.Detail,
                server,
                interrupted.Status.ToString(),
                interrupted.Detail,
                startsAtInterrupt,
                runAfterInterrupt,
                opAfterInterrupt,
                "none",
                interrupted.Status.ToString(),
                null,
                null,
                server.StartCalls,
                null,
                null);
        }

        if (interrupted.SimulatedPreviewAcknowledgement)
        {
            return Fail("PREVIEW_FALLBACK", server, interrupted.Status.ToString(), interrupted.Detail, startsAtInterrupt, runAfterInterrupt, opAfterInterrupt, "none", "Unknown", null, null, server.StartCalls, null, null);
        }

        await client.ResyncAuthoritativeStateAsync(cancellationToken).ConfigureAwait(false);
        var discoveredOp = client.ActiveOperationId;
        if (string.IsNullOrWhiteSpace(discoveredOp) || discoveredOp == "op-never-issued")
        {
            return Fail(
                "NO_ISSUED_OPERATION: runs.list did not recover the dispatched operation. A never-issued lookup is not G3.",
                server,
                interrupted.Status.ToString(),
                interrupted.Detail,
                startsAtInterrupt,
                runAfterInterrupt,
                opAfterInterrupt,
                "runs.list",
                "Unknown",
                client.ActiveRunId,
                discoveredOp,
                server.StartCalls,
                null,
                null);
        }

        var get = await client.RequestOperationAsync(
            new OperationRequest(
                "operations.get",
                "g3-operations-get",
                null,
                new Dictionary<string, string> { ["operation_id"] = discoveredOp }),
            cancellationToken).ConfigureAwait(false);

        var retry = await client.RequestOperationAsync(
            new OperationRequest("offline_demo.start", NativeOperatorClient.OfflineDemoIdempotencyKey, null, new Dictionary<string, string>()),
            cancellationToken).ConfigureAwait(false);

        var sameRun = string.Equals(client.ActiveRunId, "run-g3-1", StringComparison.Ordinal)
                      && string.Equals(client.ActiveOperationId, "op-g3-1", StringComparison.Ordinal)
                      && string.Equals(discoveredOp, "op-g3-1", StringComparison.Ordinal);
        var noSecondDispatch = server.StartCalls == startsAtInterrupt;
        var passed = startsAtInterrupt == 1
                     && runAfterInterrupt is null
                     && get.Status == OperationStatus.Completed
                     && sameRun
                     && noSecondDispatch
                     && server.OperationsGetCalls >= 1;

        return new G3ProbeReceipt(
            passed,
            "Sovereign.Desktop.Client.NativeOperatorClient",
            "cancel-after-dispatch-before-reply",
            interrupted.Status.ToString(),
            interrupted.Detail,
            startsAtInterrupt,
            runAfterInterrupt,
            opAfterInterrupt,
            "runs.list then operations.get on the issued operation_id",
            get.Status.ToString(),
            client.ActiveRunId,
            client.ActiveOperationId,
            server.StartCalls,
            client.ActiveRunId,
            client.ActiveOperationId,
            server.OperationsGetCalls,
            passed
                ? "Dispatched offline_demo.start, local status stayed UNKNOWN, operations.get recovered op-g3-1/run-g3-1, same-key retry did not dispatch a second start."
                : "G3 probe failed. startAtInterrupt=" + startsAtInterrupt + " get=" + get.Status + " retry=" + retry.Status + " starts=" + server.StartCalls + " ops.get=" + server.OperationsGetCalls);
    }

    public static async Task<int> RunAndWriteAsync(string? receiptPath, CancellationToken cancellationToken = default)
    {
        var receipt = await RunAsync(cancellationToken).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true });
        if (!string.IsNullOrWhiteSpace(receiptPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(receiptPath))!);
            File.WriteAllText(receiptPath, json);
        }

        Console.WriteLine(json);
        return receipt.Passed ? 0 : 2;
    }

    private static G3ProbeReceipt Fail(
        string summary,
        G3HoldServer server,
        string afterStatus,
        string afterDetail,
        int startsAtInterrupt,
        string? runAfterInterrupt,
        string? opAfterInterrupt,
        string reconcile,
        string afterReconcile,
        string? runAfterReconcile,
        string? opAfterReconcile,
        int startsAfter,
        string? retryRun,
        string? retryOp) =>
        new(
            false,
            "Sovereign.Desktop.Client.NativeOperatorClient",
            "cancel-after-dispatch-before-reply",
            afterStatus,
            afterDetail,
            startsAtInterrupt,
            runAfterInterrupt,
            opAfterInterrupt,
            reconcile,
            afterReconcile,
            runAfterReconcile,
            opAfterReconcile,
            startsAfter,
            retryRun,
            retryOp,
            server.OperationsGetCalls,
            summary);
}
