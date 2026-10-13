using Sovereign.Desktop.Client;
using Sovereign.Desktop.Client.Adapters;
using Sovereign.Desktop.Core;

namespace Sovereign.Desktop.Presentation;

public static class NativeIntegrationWalkthrough
{
    public static async Task<WalkthroughReceipt> RunAsync(ShellViewModel vm, CancellationToken cancellationToken = default)
    {
        var steps = new List<WalkthroughStep>();

        async Task Step(string id, string title, Func<Task<(bool ok, string detail)>> body)
        {
            try
            {
                var (ok, detail) = await body().ConfigureAwait(false);
                steps.Add(new WalkthroughStep(id, title, ok, detail));
            }
            catch (Exception ex)
            {
                steps.Add(new WalkthroughStep(id, title, false, ex.Message));
            }
        }

        await vm.RefreshAsync(cancellationToken).ConfigureAwait(false);

        await Step("connected-session", "Authenticated native session without preview fixtures", async () =>
        {
            var native = vm.Client as NativeOperatorClient;
            var ok = native is not null && native.IsConnected && vm.Mode == ApplicationMode.NativeConnection
                     && vm.Runtime?.BackendType == BackendType.NativeHost
                     && native.LastQueryFailure != HostQueryFailure.Disconnected;
            return (ok, "connected=" + (native?.IsConnected ?? false) + " failure=" + (native?.LastQueryFailure) + " runtime=" + vm.Runtime?.IntegrationStatus);
        }).ConfigureAwait(false);

        await Step("runtime-health-caps", "Runtime/health advertise NONE authority and UNAVAILABLE paper/providers", async () =>
        {
            var caps = vm.Capabilities;
            var advertised = caps.Any(c => c.Id.Equals("offline_demo.start", StringComparison.OrdinalIgnoreCase) &&
                                           c.Status.Equals("AVAILABLE", StringComparison.OrdinalIgnoreCase));
            var paper = vm.Health?.UnresolvedIssues.Any(i => i.Id == "paper") == true;
            var providers = vm.Health?.UnresolvedIssues.Any(i => i.Id == "providers") == true;
            var ok = advertised && paper && providers
                     && vm.Health?.Authorization.Value == AuthorizationState.None
                     && vm.Runtime?.IntegrationStatus.Contains("trading_authority=NONE", StringComparison.Ordinal) == true;
            return (ok, "caps=" + caps.Count + " advertised=" + advertised + " auth=" + vm.Health?.Authorization.Value);
        }).ConfigureAwait(false);

        string? firstOperationId = null;
        string? firstRunId = null;
        var firstOrderCount = 0;

        await Step("start-three-cycle", "Explicit offline_demo.start then poll to terminal", async () =>
        {
            var started = await vm.StartOfflineEngineRunAsync(cancellationToken).ConfigureAwait(false);
            var native = (NativeOperatorClient)vm.Client;
            firstOperationId = native.ActiveOperationId;
            firstRunId = native.ActiveRunId;
            firstOrderCount = vm.Orders.Count;
            var ok = started.Status == OperationStatus.Completed
                     && firstRunId is not null
                     && firstOrderCount == 3
                     && !started.SimulatedPreviewAcknowledgement;
            return (ok, started.Status + " " + started.Detail + " orders=" + firstOrderCount);
        }).ConfigureAwait(false);

        await Step("oracle-typed-account", "Engine-backed cash/shares/equity/reservations/authority", async () =>
        {
            var account = vm.Account;
            var equity = account.IsKnown ? account.Value!.EndingEquity : Presence<Money>.Unknown("account not known");
            var cash = account.IsKnown ? account.Value!.Cash : Presence<Money>.Unknown("account not known");
            var aaa = account.IsKnown ? account.Value!.Holdings.FirstOrDefault(h => h.Instrument == "AAA") : null;
            var reserved = account.IsKnown ? account.Value!.Reservations : Presence<Money>.Unknown("account not known");
            var ok = cash.IsKnown && cash.Value!.MinorUnits == 91100
                     && equity.IsKnown && equity.Value!.MinorUnits == 101000
                     && aaa is not null && aaa.Quantity.IsKnown && aaa.Quantity.Value == 11m
                     && reserved.IsKnown && reserved.Value!.MinorUnits == 0
                     && account.Value!.TradingAuthority == "NONE"
                     && vm.Orders.Count == 3;
            return (ok,
                "cash=" + (cash.IsKnown ? cash.Value!.MinorUnits.ToString() : cash.Describe()) +
                " equity=" + (equity.IsKnown ? equity.Value!.MinorUnits.ToString() : equity.Describe()) +
                " aaa=" + (aaa?.Quantity.Describe() ?? "missing") +
                " reserved=" + (reserved.IsKnown ? reserved.Value!.MinorUnits.ToString() : reserved.Describe()) +
                " authority=" + vm.TradingAuthorityLabel +
                " orders=" + vm.Orders.Count +
                " decisions=" + vm.Decisions.Count);
        }).ConfigureAwait(false);

        await Step("duplicate-click-guard", "Same-session Start is OperationGuard-protected", async () =>
        {
            var before = vm.Orders.Count;
            var native = (NativeOperatorClient)vm.Client;
            var beforeOp = native.ActiveOperationId;
            var duplicate = await vm.StartOfflineEngineRunAsync(cancellationToken).ConfigureAwait(false);
            var ok = duplicate.Detail.Contains(beforeOp ?? "missing", StringComparison.Ordinal)
                     && vm.Orders.Count == before
                     && native.ActiveOperationId == beforeOp;
            return (ok, "duplicate=" + duplicate.Detail + " orders=" + vm.Orders.Count + " op=" + native.ActiveOperationId);
        }).ConfigureAwait(false);

        await Step("reopen-recovers-without-start", "Disconnect/reopen recovers the existing run without Start", async () =>
        {
            await vm.ReopenNativeSessionAsync(cancellationToken).ConfigureAwait(false);
            var native = (NativeOperatorClient)vm.Client;
            var recovered = native.IsConnected
                            && native.ActiveRunId == firstRunId
                            && vm.Account.IsKnown
                            && vm.Account.Value!.EndingEquity.IsKnown
                            && vm.Account.Value.EndingEquity.Value!.MinorUnits == 101000
                            && vm.Orders.Count == firstOrderCount;
            return (recovered, "run=" + native.ActiveRunId + " op=" + native.ActiveOperationId + " orders=" + vm.Orders.Count + " connected=" + native.IsConnected);
        }).ConfigureAwait(false);

        await Step("explicit-idempotent-retry", "Separate explicit retry: same key, fresh nonce, same operation/run, no new orders", async () =>
        {
            var before = vm.Orders.Count;
            var native = (NativeOperatorClient)vm.Client;
            var retry = await vm.StartOfflineEngineRunAsync(cancellationToken).ConfigureAwait(false);
            var ok = retry.Status == OperationStatus.Completed
                     && native.ActiveOperationId == firstOperationId
                     && native.ActiveRunId == firstRunId
                     && vm.Orders.Count == before
                     && vm.Orders.Count == 3;
            return (ok, retry.Detail + " orders=" + vm.Orders.Count);
        }).ConfigureAwait(false);

        await Step("evidence-selection-journey", "User-facing evidence selection from a decision", async () =>
        {
            if (vm.Decisions.Count == 0)
            {
                return (false, "No host decisions to select.");
            }

            var decision = vm.Decisions[0];
            await vm.SelectDecisionAsync(decision.Id, cancellationToken).ConfigureAwait(false);
            if (decision.EvidenceIds.Count == 0)
            {
                var ok = vm.SelectedDecision?.Id == decision.Id && vm.SelectedEvidence is null;
                return (ok, "selection-journey: host decision has no evidence_ids; evidence.get was not invoked from selection. decision=" + decision.Id);
            }

            return (vm.SelectedEvidence is not null,
                "selection-journey evidence=" + (vm.SelectedEvidence?.Id ?? "none") + " media=" + (vm.SelectedEvidence?.MediaType ?? ""));
        }).ConfigureAwait(false);

        await Step("api-negative-evidence-get", "API-NEGATIVE evidence.get UNAVAILABLE is not a selection journey", async () =>
        {
            await vm.ProbeEvidenceGetApiNegativeAsync("probe-unavailable", cancellationToken).ConfigureAwait(false);
            var native = (NativeOperatorClient)vm.Client;
            var doc = vm.ApiNegativeEvidence;
            var ok = native.IsConnected
                     && native.LastQueryFailure == HostQueryFailure.Unavailable
                     && doc is not null
                     && doc.Title.StartsWith("API-NEGATIVE", StringComparison.Ordinal)
                     && doc.MediaType.Contains("api-negative-evidence-get", StringComparison.Ordinal);
            return (ok, "failure=" + native.LastQueryFailure + " title=" + (doc?.Title ?? "null") + " media=" + (doc?.MediaType ?? "") + " connected=" + native.IsConnected);
        }).ConfigureAwait(false);

        await Step("support-export-no-authenticators", "Support export omits session authenticators and secrets", () =>
        {
            File.WriteAllText(Path.Combine(vm.ProfileDirectory, "endpoint-v2.json"), """{"pipe_name":"x","authenticator":{"key_b64":"planted"}}""");
            var zip = Path.Combine(vm.ProfileDirectory, "native-support.zip");
            var exported = vm.ExportSupport(zip);
            using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
            var names = archive.Entries.Select(e => e.FullName).ToArray();
            var leaked = names.Any(n => n.Contains("endpoint-v2", StringComparison.OrdinalIgnoreCase))
                         || exported.ContainsPlantedSecretMarker;
            return Task.FromResult((!leaked, "included=" + string.Join(",", exported.IncludedRelativePaths) + " excluded=" + string.Join(";", exported.ExcludedReasons)));
        }).ConfigureAwait(false);

        var passed = steps.All(s => s.Passed);
        return new WalkthroughReceipt(
            ShellViewModel.AppVersion,
            steps,
            passed,
            passed
                ? "Host-local native integration walkthrough passed. This is not N3/release qualification."
                : "One or more native integration walkthrough steps failed. See steps.");
    }
}
