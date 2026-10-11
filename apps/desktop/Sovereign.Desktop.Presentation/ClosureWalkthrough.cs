using Sovereign.Desktop.Client;
using Sovereign.Desktop.Client.Adapters;
using Sovereign.Desktop.Client.Storage;
using Sovereign.Desktop.Core;
using Sovereign.Desktop.Core.Drafts;

namespace Sovereign.Desktop.Presentation;

public sealed record WalkthroughStep(string Id, string Title, bool Passed, string Detail);

public sealed record WalkthroughReceipt(
    string Version,
    IReadOnlyList<WalkthroughStep> Steps,
    bool Passed,
    string Summary);

public static class ClosureWalkthrough
{
    public static async Task<WalkthroughReceipt> RunAsync(ShellViewModel vm, string fixturesRoot, CancellationToken cancellationToken = default)
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

        await Step("archive-decision-evidence", "Open archive, decision, and evidence", async () =>
        {
            var archive = Path.Combine(fixturesRoot, "archives", "valid-hash-consistent");
            await vm.SwitchModeAsync(ApplicationMode.EvidenceArchive, null, archive, cancellationToken).ConfigureAwait(false);
            if (vm.Decisions.Count == 0)
            {
                return (false, "Archive produced no decisions.");
            }

            var decision = vm.Decisions[0];
            await vm.SelectDecisionAsync(decision.Id, cancellationToken).ConfigureAwait(false);
            var evidenceOk = vm.SelectedEvidence is not null;
            return (evidenceOk,
                $"mode={ModeLabels.Short(vm.Mode)} decision={decision.Id} action={decision.Action} evidence={(vm.SelectedEvidence?.Id ?? "none")} events={vm.Events.Count}");
        }).ConfigureAwait(false);

        await Step("invalid-draft-correct-save-reopen", "Invalid draft, correct, save, reopen inactive", async () =>
        {
            await vm.SwitchModeAsync(ApplicationMode.DesktopPreview, "admitted-blocked-execution", null, cancellationToken).ConfigureAwait(false);
            vm.SetMandateFields("", "", "lifetime", "whenever", null, 100, 200, -1, 50_000, "", -5);
            await vm.SaveMandateAsync(cancellationToken).ConfigureAwait(false);
            var invalid = vm.DraftErrors.Contains("Name", StringComparison.Ordinal) && vm.DraftStatus.Contains("NOT ACTIVATED", StringComparison.Ordinal);
            vm.SetMandateFields("Closure sleeve", "AAA,BBB", "3-12m", "weekly", "America/Los_Angeles", 1500, 800, 25_000, 200, "sec-edgar", 1000);
            await vm.SaveMandateAsync(cancellationToken).ConfigureAwait(false);
            var id = vm.Mandate.Id;
            var revision = vm.Mandate.Revision;
            var profile = vm.ProfileDirectory;
            var reopened = new ShellViewModel(
                new PreviewOperatorClient(Sovereign.Desktop.Client.Scenarios.ScenarioCatalog.Load(fixturesRoot, "admitted-blocked-execution")),
                new FileDraftStore(profile),
                profile,
                fixturesRoot);
            await reopened.LoadMandateAsync(id, cancellationToken).ConfigureAwait(false);
            var ok = invalid
                     && reopened.Mandate.Revision == revision
                     && reopened.Mandate.Activation == ActivationStatus.DraftNotActivated
                     && reopened.Mandate.Name == "Closure sleeve"
                     && reopened.DraftStatus.Contains("NOT ACTIVATED", StringComparison.Ordinal);
            return (ok, reopened.DraftStatus + " errors-before=" + vm.DraftErrors.Replace(Environment.NewLine, " | "));
        }).ConfigureAwait(false);

        await Step("support-export-no-secrets", "Support export allowlist omits planted secrets", () =>
        {
            File.WriteAllText(Path.Combine(vm.ProfileDirectory, "secrets.env"), "API_KEY=planted-secret-marker");
            var zip = Path.Combine(vm.ProfileDirectory, "walkthrough-support.zip");
            var exported = vm.ExportSupport(zip);
            using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
            var names = archive.Entries.Select(e => e.FullName).ToArray();
            var ok = !exported.ContainsPlantedSecretMarker
                     && names.All(n => !n.Contains("secrets", StringComparison.OrdinalIgnoreCase))
                     && exported.ExcludedReasons.Any(r => r.Contains("secrets.env", StringComparison.OrdinalIgnoreCase));
            return Task.FromResult((ok, "included=" + string.Join(",", exported.IncludedRelativePaths) + " excluded=" + string.Join(";", exported.ExcludedReasons)));
        }).ConfigureAwait(false);

        await Step("mode-switch-no-leak", "Mode switch does not leak previous account", async () =>
        {
            await vm.SwitchModeAsync(ApplicationMode.DesktopPreview, "admitted-blocked-execution", null, cancellationToken).ConfigureAwait(false);
            await vm.SelectDecisionAsync(vm.Decisions[0].Id, cancellationToken).ConfigureAwait(false);
            var previous = vm.AccountHeading;
            await vm.SwitchModeAsync(ApplicationMode.NativeConnection, null, null, cancellationToken).ConfigureAwait(false);
            var leaked = vm.Account.IsKnown || previous == vm.AccountHeading;
            var ok = !leaked && vm.SelectedDecision is null && vm.Mode == ApplicationMode.NativeConnection && vm.Decisions.Count == 0;
            return (ok, "before=" + previous + " after=" + vm.AccountHeading + " banner=" + vm.ModeBanner);
        }).ConfigureAwait(false);

        var passed = steps.All(s => s.Passed);
        return new WalkthroughReceipt(
            ShellViewModel.AppVersion,
            steps,
            passed,
            passed ? "All walkthrough steps passed on the view-model surface." : "One or more walkthrough steps failed. See steps.");
    }
}
