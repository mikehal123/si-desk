using System.Reflection;
using System.Text.Json;
using Sovereign.Desktop.Client.Adapters;
using Sovereign.Desktop.Client.Host;
using Sovereign.Desktop.Core;

namespace Sovereign.Desktop.G3Harness;

public sealed record G3InstalledReceipt(
    bool Passed,
    string Client,
    string ClientAssemblySha256,
    string ClientInformationalVersion,
    string HostKind,
    string InterruptKind,
    string AfterDispatchStatus,
    string AfterDispatchDetail,
    bool StartPersistedBeforeCancel,
    string? PersistedRunId,
    string? PersistPath,
    string? RunIdAfterInterrupt,
    string? OperationIdAfterInterrupt,
    string ReconcileMethod,
    string AfterReconcileStatus,
    string? RunIdAfterReconcile,
    string? OperationIdAfterReconcile,
    int RunDirsAfterRetry,
    int OrdersAfterRetry,
    string? RunIdAfterRetry,
    string Summary);

public static class G3InstalledHostProbe
{
    public static async Task<G3InstalledReceipt> RunAsync(
        string installDir,
        string desktopProfile,
        CancellationToken cancellationToken = default)
    {
        installDir = Path.GetFullPath(installDir);
        desktopProfile = Path.GetFullPath(desktopProfile);
        var hostProfile = Path.GetFullPath(Path.Combine(desktopProfile, "host"));
        var clientHash = ClientBuildHash();
        var clientVersion = typeof(NativeOperatorClient).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";

        var engine = InstalledEngineResolver.TryResolve(installDir, out var engineWhy);
        if (engine is null)
        {
            return Fail("ENGINE_MISSING: " + engineWhy, clientHash, clientVersion);
        }

        Directory.CreateDirectory(hostProfile);
        var profileId = InstalledHostSupervisor.ProfileIdFor(hostProfile);
        InstalledHostSupervisor.EnsureRunning(engine, hostProfile, profileId);

        PersistShot? persist = null;
        using var interrupt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var seam = new HostTransportSeam
        {
            AfterSendBeforeReply = async (ctx, ct) =>
            {
                if (ctx.Method != "offline_demo.start")
                {
                    return;
                }

                persist = await WaitForPersistAsync(hostProfile, TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
                if (persist.Persisted)
                {
                    await interrupt.CancelAsync().ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                }
            }
        };

        await using var client = new NativeOperatorClient(new NativeClientOptions(
            hostProfile,
            "Sovereign.Desktop.G3Harness",
            clientVersion,
            desktopProfile,
            seam));
        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        if (!client.IsConnected)
        {
            return Fail("CLIENT_NOT_CONNECTED " + client.ConnectionReason, clientHash, clientVersion);
        }

        var interrupted = await client.RequestOperationAsync(
            new OperationRequest("offline_demo.start", NativeOperatorClient.OfflineDemoIdempotencyKey, null, new Dictionary<string, string>()),
            interrupt.Token).ConfigureAwait(false);

        if (persist is null || !persist.Persisted)
        {
            return Fail("NOT_PERSISTED_BEFORE_CANCEL", clientHash, clientVersion, persist, interrupted);
        }

        if (interrupted.Status != OperationStatus.Unknown || interrupted.SimulatedPreviewAcknowledgement)
        {
            return Fail(
                "AFTER_DISPATCH_NOT_UNKNOWN: " + interrupted.Status + " " + interrupted.Detail,
                clientHash,
                clientVersion,
                persist,
                interrupted);
        }

        var runAfterInterrupt = client.ActiveRunId;
        var opAfterInterrupt = client.ActiveOperationId;
        await client.ResyncAuthoritativeStateAsync(cancellationToken).ConfigureAwait(false);
        var discoveredOp = client.ActiveOperationId;
        if (string.IsNullOrWhiteSpace(discoveredOp))
        {
            return Fail(
                "NO_ISSUED_OPERATION: runs.list did not recover the persisted start. A never-issued lookup is not G3.",
                clientHash,
                clientVersion,
                persist,
                interrupted,
                runAfterInterrupt,
                opAfterInterrupt);
        }

        var get = await client.RequestOperationAsync(
            new OperationRequest(
                "operations.get",
                "g3-operations-get",
                null,
                new Dictionary<string, string> { ["operation_id"] = discoveredOp }),
            cancellationToken).ConfigureAwait(false);

        await using var retryClient = new NativeOperatorClient(new NativeClientOptions(
            hostProfile,
            "Sovereign.Desktop.G3Harness",
            clientVersion,
            desktopProfile));
        await retryClient.ConnectAsync(cancellationToken).ConfigureAwait(false);
        var retry = await retryClient.RequestOperationAsync(
            new OperationRequest("offline_demo.start", NativeOperatorClient.OfflineDemoIdempotencyKey, null, new Dictionary<string, string>()),
            cancellationToken).ConfigureAwait(false);
        var orders = await retryClient.ListOrdersAsync(cancellationToken).ConfigureAwait(false);
        var after = Snapshot(hostProfile);
        var passed = persist.Persisted
                     && interrupted.Status == OperationStatus.Unknown
                     && runAfterInterrupt is null
                     && get.Status == OperationStatus.Completed
                     && retry.Status == OperationStatus.Completed
                     && after.RunCount == 1
                     && orders.Count == 3
                     && string.Equals(retryClient.ActiveRunId, persist.RunId, StringComparison.Ordinal)
                     && string.Equals(client.ActiveRunId, persist.RunId, StringComparison.Ordinal);

        return new G3InstalledReceipt(
            passed,
            "Sovereign.Desktop.Client.NativeOperatorClient.RequestOperationAsync",
            clientHash,
            clientVersion,
            "installed-engine-2.0.2",
            "transport-seam-withhold-reply-after-host-persist",
            interrupted.Status.ToString(),
            interrupted.Detail,
            persist.Persisted,
            persist.RunId,
            persist.Hint,
            runAfterInterrupt,
            opAfterInterrupt,
            "NativeOperatorClient.ResyncAuthoritativeStateAsync then RequestOperationAsync(operations.get)",
            get.Status.ToString(),
            client.ActiveRunId,
            client.ActiveOperationId,
            after.RunCount,
            orders.Count,
            retryClient.ActiveRunId,
            passed
                ? "Installed host persisted start before the seam canceled the wait; RequestOperationAsync returned Unknown; operations.get recovered the issued run; same-key retry left one run and three orders."
                : "G3 installed probe failed. persist=" + persist.Persisted + " interrupt=" + interrupted.Status + " get=" + get.Status + " retry=" + retry.Status + " runs=" + after.RunCount + " orders=" + orders.Count);
    }

    public static async Task<int> RunAndWriteAsync(string installDir, string desktopProfile, string? receiptPath, CancellationToken cancellationToken = default)
    {
        var receipt = await RunAsync(installDir, desktopProfile, cancellationToken).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true });
        if (!string.IsNullOrWhiteSpace(receiptPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(receiptPath))!);
            File.WriteAllText(receiptPath, json);
        }

        Console.WriteLine(json);
        return receipt.Passed ? 0 : 2;
    }

    public static string ClientBuildHash()
    {
        var path = typeof(NativeOperatorClient).Assembly.Location;
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    }

    public static PersistShot Snapshot(string hostProfile)
    {
        var runs = Path.Combine(hostProfile, "runs");
        var dirs = Directory.Exists(runs) ? Directory.GetDirectories(runs) : [];
        string? runId = null;
        string? hint = null;
        foreach (var dir in dirs)
        {
            runId = Path.GetFileName(dir);
            var summary = Path.Combine(dir, "summary.json");
            if (File.Exists(summary))
            {
                hint = summary;
            }
            else
            {
                hint ??= dir;
            }
        }

        return new PersistShot(dirs.Length > 0 && runId is not null, runId, hint, dirs.Length);
    }

    private static async Task<PersistShot> WaitForPersistAsync(string hostProfile, TimeSpan budget, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + budget;
        PersistShot shot;
        do
        {
            shot = Snapshot(hostProfile);
            if (shot.Persisted)
            {
                return shot;
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        } while (DateTime.UtcNow < deadline);

        return shot;
    }

    private static G3InstalledReceipt Fail(
        string summary,
        string clientHash,
        string clientVersion,
        PersistShot? persist = null,
        OperationResult? interrupted = null,
        string? runAfterInterrupt = null,
        string? opAfterInterrupt = null) =>
        new(
            false,
            "Sovereign.Desktop.Client.NativeOperatorClient.RequestOperationAsync",
            clientHash,
            clientVersion,
            "installed-engine-2.0.2",
            "transport-seam-withhold-reply-after-host-persist",
            interrupted?.Status.ToString() ?? "",
            interrupted?.Detail ?? "",
            persist?.Persisted ?? false,
            persist?.RunId,
            persist?.Hint,
            runAfterInterrupt,
            opAfterInterrupt,
            "none",
            "",
            null,
            null,
            persist?.RunCount ?? 0,
            0,
            null,
            summary);

    public sealed record PersistShot(bool Persisted, string? RunId, string? Hint, int RunCount);
}
