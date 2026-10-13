using System.Text.Json.Nodes;
using Sovereign.Desktop.Client;
using Sovereign.Desktop.Client.Adapters;
using Sovereign.Desktop.Client.Host;
using Sovereign.Desktop.Client.Scenarios;
using Sovereign.Desktop.Client.Storage;
using Sovereign.Desktop.Core;
using Sovereign.Desktop.Core.Archive;
using Sovereign.Desktop.Core.Drafts;
using Sovereign.Desktop.Core.Evidence;
using Sovereign.Desktop.Core.Support;
using Sovereign.Desktop.Presentation;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class DeskContractTests
{
    private static string Fixtures => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures"));

    private static ShellViewModel PreviewShell(string scenario, string? profile = null)
    {
        profile ??= Path.Combine(Path.GetTempPath(), "sov-desk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        var client = new PreviewOperatorClient(ScenarioCatalog.Load(Fixtures, scenario));
        var drafts = new FileDraftStore(profile, new ControllableClock(DateTimeOffset.Parse("2026-09-15T12:00:00Z")));
        return new ShellViewModel(client, drafts, profile, Fixtures);
    }

    [Fact]
    public async Task Views_receive_data_without_engine_modules()
    {
        var vm = PreviewShell("admitted-blocked-execution");
        await vm.RefreshAsync();
        Assert.Equal(ApplicationMode.DesktopPreview, vm.Mode);
        Assert.Contains("PREVIEW MODE", vm.ModeBanner, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(vm.AccountHeading));
        Assert.DoesNotContain("Python", string.Join(" ", AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Desk02_native_disconnect_does_not_fall_back_to_preview()
    {
        var profile = Path.Combine(Path.GetTempPath(), "sov-desk-" + Guid.NewGuid().ToString("N"));
        var vm = new ShellViewModel(new NativeOperatorClient(), new FileDraftStore(profile), profile, Fixtures);
        await vm.RefreshAsync();
        Assert.Equal(ApplicationMode.NativeConnection, vm.Mode);
        Assert.Contains("NATIVE CONNECTION", vm.ModeBanner, StringComparison.Ordinal);
        Assert.False(vm.Account.IsKnown);
        Assert.Equal(PresenceKind.Unknown, vm.Account.Kind);
        Assert.Empty(vm.Decisions);
        Assert.DoesNotContain("91100", vm.CashLabel, StringComparison.Ordinal);
        var op = await vm.RequestHostOperationAsync("PAUSE_NEW_RISK");
        Assert.Equal(OperationStatus.Unavailable, op.Status);
        Assert.False(op.SimulatedPreviewAcknowledgement);
    }

    [Fact]
    public async Task Desk03_account_change_clears_selection()
    {
        var vm = PreviewShell("admitted-blocked-execution");
        await vm.RefreshAsync();
        await vm.SelectDecisionAsync("dec-aaa-1");
        Assert.NotNull(vm.SelectedDecision);
        await vm.SwitchAccountContextAsync("missing-valuation");
        Assert.Null(vm.SelectedDecision);
        Assert.Contains("preview-paper-b", vm.AccountHeading, StringComparison.Ordinal);
        Assert.DoesNotContain("preview-paper-a", vm.AccountHeading, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Desk04_unknown_and_missing_are_not_zero_success()
    {
        var vm = PreviewShell("missing-valuation");
        await vm.RefreshAsync();
        Assert.Equal(PresenceKind.Missing, vm.Account.Value!.Cash.Kind);
        Assert.DoesNotContain("USD 0.00", vm.CashLabel, StringComparison.Ordinal);
        var holding = vm.Account.Value.Holdings.Single();
        Assert.Equal(PresenceKind.Known, holding.Quantity.Kind);
        Assert.Equal(40m, holding.Quantity.Value);
        Assert.Equal(PresenceKind.Missing, holding.Mark.Kind);
        await vm.SelectDecisionAsync("dec-bbb-hold");
        Assert.Equal(GateVerdict.Unknown, vm.SelectedDecision!.Admission.Verdict);
        Assert.Equal(GateVerdict.Blocked, vm.SelectedDecision.Execution.Verdict);
        Assert.Contains("valuation", vm.SelectedDecision.Execution.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Hold_and_abstain_remain_distinct()
    {
        var vm = PreviewShell("hold-and-abstain");
        await vm.RefreshAsync();
        Assert.Contains(vm.Decisions, d => d.Action == DecisionAction.Hold);
        Assert.Contains(vm.Decisions, d => d.Action == DecisionAction.Abstain);
        Assert.DoesNotContain(vm.Decisions, d => d.Action == DecisionAction.Hold && d.Id.Contains("abstain", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Desk05_draft_validation_and_inactive_save()
    {
        var invalid = new MandateDraft(MandateDraft.CurrentSchema, "m1", 0, "", DateTimeOffset.UtcNow, ActivationStatus.DraftNotActivated,
            [], "lifetime", "weekly", null, 100, 200, -1, 50_000, [], "US");
        var errors = DraftValidator.ValidateMandate(invalid);
        Assert.Contains(errors, e => e.Field == "Name");
        Assert.Contains(errors, e => e.Field == "Horizon");
        Assert.Contains(errors, e => e.Field == "Schedule");
        Assert.Contains(errors, e => e.Field == "SectorCapBps");
        Assert.Contains(errors, e => e.Field == "CashFloorMinorUnits");
        Assert.Contains(errors, e => e.Field == "TurnoverBps");
        Assert.Contains(errors, e => e.Field == "Currency");
        Assert.Contains(errors, e => e.Field == "Universe");
        Assert.Contains(errors, e => e.Field == "AllowedSources");
    }

    [Fact]
    public async Task Desk05_saved_draft_stays_inactive_across_reopen()
    {
        var profile = Path.Combine(Path.GetTempPath(), "sov-desk-" + Guid.NewGuid().ToString("N"));
        var clock = new ControllableClock(DateTimeOffset.Parse("2026-09-15T12:00:00Z"));
        var store = new FileDraftStore(profile, clock);
        var draft = new MandateDraft(MandateDraft.CurrentSchema, "mandate-a", 0, "Paper sleeve", clock.UtcNow, ActivationStatus.DraftNotActivated,
            ["AAA", "BBB"], "3-12m", "weekly", "America/Los_Angeles", 1500, 800, 25_000, 200, ["sec-edgar"], "USD");
        var saved = await store.SaveMandateAsync(draft, 0);
        Assert.Equal(1, saved.Revision);
        Assert.Equal(ActivationStatus.DraftNotActivated, saved.Activation);
        var store2 = new FileDraftStore(profile, clock);
        var loaded = await store2.LoadMandateAsync("mandate-a");
        Assert.NotNull(loaded);
        Assert.Equal(1, loaded!.Revision);
        Assert.Equal(ActivationStatus.DraftNotActivated, loaded.Activation);
        Assert.Equal(["AAA", "BBB"], loaded.Universe);
        Assert.Null(loaded.Universe!.FirstOrDefault(x => x is null));
    }

    [Fact]
    public async Task Desk06_concurrent_save_preserves_last_valid()
    {
        var profile = Path.Combine(Path.GetTempPath(), "sov-desk-" + Guid.NewGuid().ToString("N"));
        var store = new FileDraftStore(profile, new ControllableClock(DateTimeOffset.Parse("2026-09-15T12:00:00Z")));
        var draft = new MandateDraft(MandateDraft.CurrentSchema, "mandate-a", 0, "One", DateTimeOffset.UtcNow, ActivationStatus.DraftNotActivated,
            ["AAA"], "3-12m", "weekly", "UTC", 1000, 500, 0, 100, ["sec"], "USD");
        var first = await store.SaveMandateAsync(draft, 0);
        var second = await store.SaveMandateAsync(first with { Name = "Two" }, 1);
        await Assert.ThrowsAsync<ConcurrentDraftException>(() => store.SaveMandateAsync(first with { Name = "Stale" }, 1));
        var loaded = await store.LoadMandateAsync("mandate-a");
        Assert.Equal("Two", loaded!.Name);
        Assert.Equal(2, loaded.Revision);
    }

    [Fact]
    public async Task Desk06_readonly_failure_does_not_corrupt()
    {
        var profile = Path.Combine(Path.GetTempPath(), "sov-desk-" + Guid.NewGuid().ToString("N"));
        var store = new FileDraftStore(profile, new ControllableClock(DateTimeOffset.Parse("2026-09-15T12:00:00Z")));
        var draft = new MandateDraft(MandateDraft.CurrentSchema, "mandate-a", 0, "One", DateTimeOffset.UtcNow, ActivationStatus.DraftNotActivated,
            ["AAA"], "3-12m", "weekly", "UTC", 1000, 500, 0, 100, ["sec"], "USD");
        await store.SaveMandateAsync(draft, 0);
        var path = Path.Combine(profile, "drafts", "mandate-mandate-a.json");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var loaded = await store.LoadMandateAsync("mandate-a");
            await Assert.ThrowsAsync<DraftIOException>(() => store.SaveMandateAsync(loaded! with { Name = "Two" }, loaded.Revision));
            File.SetAttributes(path, FileAttributes.Normal);
            var again = await store.LoadMandateAsync("mandate-a");
            Assert.Equal("One", again!.Name);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Desk07_duplicate_and_gap_events()
    {
        var inbox = new EventInbox();
        var e1 = new ClientEvent("e1", "0001", DateTimeOffset.UtcNow, "a", "one", null, null, false);
        var e2 = new ClientEvent("e1", "0001", DateTimeOffset.UtcNow, "a", "dup", null, null, false);
        var e3 = new ClientEvent("e3", "0003", DateTimeOffset.UtcNow, "a", "gap", null, null, true);
        inbox.Accept(e1);
        inbox.Accept(e2);
        inbox.Accept(e3);
        Assert.Equal(2, inbox.Snapshot().Count);
        Assert.True(inbox.Gap);
        var resume = inbox.Resume("0000");
        Assert.Contains(resume, e => e.Kind == "resync");
    }

    [Fact]
    public async Task Desk08_timeout_stays_unknown()
    {
        var vm = PreviewShell("unknown-order-lost-response");
        await vm.RefreshAsync();
        var order = vm.Orders.Single();
        Assert.Equal(PresenceKind.Unknown, order.CurrentState.Kind);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var native = new NativeOperatorClient();
        var result = await native.RequestOperationAsync(new OperationRequest("cancel-order", "k1", null, new Dictionary<string, string>()), cts.Token);
        Assert.Equal(OperationStatus.Unknown, result.Status);
        Assert.Contains("not acknowledged as canceled", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Desk09_duplicate_idempotency_does_not_invent_second_intent()
    {
        var guard = new OperationGuard();
        Assert.True(guard.TryBegin("pause", "same", out _));
        Assert.False(guard.TryBegin("pause", "same", out var existing));
        Assert.NotNull(existing);
        Assert.Equal("same", existing!.IdempotencyKey);
        Assert.Equal(OperationStatus.Unknown, existing.Status);
    }

    [Fact]
    public async Task Desk10_mode_banner_survives_navigation()
    {
        var vm = PreviewShell("admitted-blocked-execution");
        await vm.RefreshAsync();
        vm.SelectedNav = "orders";
        Assert.Contains("PREVIEW MODE", vm.ModeBanner, StringComparison.Ordinal);
        var profile = vm.ProfileDirectory;
        var native = new ShellViewModel(new NativeOperatorClient(), new FileDraftStore(profile), profile, Fixtures);
        await native.RefreshAsync();
        Assert.Contains("NATIVE CONNECTION", native.ModeBanner, StringComparison.Ordinal);
        var export = native.ExportSupport(Path.Combine(profile, "support.zip"));
        Assert.Contains(export.IncludedRelativePaths, p => p.Contains("export-manifest", StringComparison.Ordinal));
    }

    [Fact]
    public void Desk11_archive_refusals()
    {
        var valid = ArchiveReader.OpenDirectory(Path.Combine(Fixtures, "archives", "valid-hash-consistent"));
        Assert.Equal(ArchiveTrust.HashConsistent, valid.Trust);
        var tampered = ArchiveReader.OpenDirectory(Path.Combine(Fixtures, "archives", "tampered-hash"));
        Assert.Equal(ArchiveTrust.Invalid, tampered.Trust);
        var unsupported = ArchiveReader.OpenDirectory(Path.Combine(Fixtures, "archives", "unsupported-schema"));
        Assert.Equal(ArchiveTrust.Unsupported, unsupported.Trust);
        var escape = ArchiveReader.OpenDirectory(Path.Combine(Fixtures, "archives", "path-escape"));
        Assert.Equal(ArchiveTrust.Invalid, escape.Trust);
        Assert.Contains("Traversal", escape.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(ArchiveReader.IsSafeRelative("C:\\Windows\\win.ini", out _, out _));
        Assert.False(ArchiveReader.IsSafeRelative("..\\secret.txt", out _, out _));
    }

    [Fact]
    public void Desk12_evidence_uris_and_text_are_data()
    {
        Assert.True(EvidenceSafety.IsAllowedUri("https://example.invalid/x", out _, out _));
        Assert.False(EvidenceSafety.IsAllowedUri("javascript:alert(1)", out _, out var reason));
        Assert.Contains("not allowed", reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(EvidenceSafety.IsAllowedUri("file:///C:/Windows/win.ini", out _, out _));
        Assert.DoesNotContain('\0', EvidenceSafety.AsData("abc\0def"));
    }

    [Fact]
    public void Desk13_support_export_allowlist()
    {
        var profile = Path.Combine(Path.GetTempPath(), "sov-desk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "app-version.json"), """{"app":"preview","version":"0.1.0-preview"}""");
        File.WriteAllText(Path.Combine(profile, "secrets.env"), "API_KEY=planted-secret-marker");
        Directory.CreateDirectory(Path.Combine(profile, "drafts"));
        File.WriteAllText(Path.Combine(profile, "drafts", "mandate-a.json"), """{"name":"ok"}""");
        var zip = Path.Combine(profile, "out.zip");
        var result = SupportExporter.Export(profile, zip, "0.1.0-preview", "Desktop Preview");
        Assert.Contains("app-version.json", result.IncludedRelativePaths);
        Assert.Contains(result.ExcludedReasons, r => r.Contains("secrets.env", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.ExcludedReasons, r => r.Contains("API_KEY") || r.Contains("secret", StringComparison.OrdinalIgnoreCase) || r.Contains("allowlist"));
        using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
        Assert.Null(archive.GetEntry("secrets.env"));
        var names = archive.Entries.Select(e => e.FullName).ToArray();
        Assert.DoesNotContain(names, n => n.Contains("secrets", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Desk15_disabled_reasons_are_precise()
    {
        var vm = PreviewShell("expired-grant-unavailable-provider");
        Assert.Contains("host-gated", vm.DisabledReasonFor("activate-session"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("will not accept secrets", vm.DisabledReasonFor("store-secret"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not submit orders", vm.DisabledReasonFor("submit-order"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Host_v1_example_round_trips()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "contracts", "observed", "pause-command-v1.json");
        if (!File.Exists(path)) return;
        Assert.True(File.Exists(path), "Observed host example must be snapshotted.");
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var frame = HostFraming.EncodeFrame(node);
        var decoded = HostFraming.DecodeFrame(frame);
        Assert.Equal("PAUSE_NEW_RISK", decoded["action"]!.GetValue<string>());
        node["unexpected"] = true;
        var ex = Assert.Throws<HostProtocolException>(() => HostFraming.EncodeFrame(node));
        Assert.Equal("INVALID_COMMAND_FIELDS", ex.Code);
    }

    [Fact]
    public void Money_is_exact_minor_units()
    {
        var money = Money.UsdCents(91100);
        Assert.Equal(91100, money.MinorUnits);
        Assert.Equal("USD 911.00", money.Format(System.Globalization.CultureInfo.InvariantCulture));
    }
}
