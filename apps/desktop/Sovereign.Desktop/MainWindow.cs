using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Sovereign.Desktop.Client;
using Sovereign.Desktop.Client.Scenarios;
using Sovereign.Desktop.Core;
using Sovereign.Desktop.Presentation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.System;
using WinRT.Interop;

namespace Sovereign.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly ShellViewModel _vm;
    private readonly TextBlock _title = new() { FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeTitle, Foreground = DeskTheme.Text, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _banner = new() { FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeCaption, Foreground = DeskTheme.Muted, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _account = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _engine = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _provider = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _broker = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _recon = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _auth = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _detail = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly TextBlock _evidence = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly TextBlock _draftStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _draftError = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _host = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _export = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListView _nav = new() { Width = 220, Padding = new Thickness(DeskTheme.Space8, DeskTheme.Space4, DeskTheme.Space8, DeskTheme.Space4) };
    private readonly ListView _main = new();
    private readonly ComboBox _scenarios = new() { Width = 240 };
    private readonly ComboBox _modes = new() { Width = 200 };
    private readonly TextBox _search = new() { Width = 220, PlaceholderText = "Filter" };
    private readonly TextBox _name = new() { Header = "Name" };
    private readonly TextBox _universe = new() { Header = "Universe" };
    private readonly TextBox _horizon = new() { Header = "Horizon" };
    private readonly TextBox _schedule = new() { Header = "Schedule" };
    private readonly TextBox _timezone = new() { Header = "Timezone" };
    private readonly TextBox _conc = new() { Header = "Concentration cap bps" };
    private readonly TextBox _sector = new() { Header = "Sector cap bps" };
    private readonly TextBox _cash = new() { Header = "Cash floor minor units" };
    private readonly TextBox _turnover = new() { Header = "Turnover bps" };
    private readonly TextBox _sources = new() { Header = "Allowed sources" };
    private Grid? _scaleRoot;
    private ContentDialog? _exportDialog;
    private int _tab;
    private bool _bindQueued;

    public MainWindow(ShellViewModel vm, int testDpiPercent = 100)
    {
        App.CrashLog("MainWindow ctor");
        _vm = vm;
        Title = vm.AppTitle;
        AutomationName(_nav, "Workspace navigation");
        AutomationName(_main, "Main records");
        AutomationName(_search, "Filter list");
        AutomationName(_scenarios, "Preview scenario");
        AutomationName(_modes, "Application mode");
        AutomationName(_name, "Mandate name");
        AutomationName(_universe, "Universe");
        AutomationName(_horizon, "Horizon");
        AutomationName(_schedule, "Schedule");
        AutomationName(_timezone, "Timezone");
        AutomationName(_conc, "Concentration cap bps");
        AutomationName(_sector, "Sector cap bps");
        AutomationName(_cash, "Cash floor minor units");
        AutomationName(_turnover, "Turnover bps");
        AutomationName(_sources, "Allowed sources");
        AutomationId(_title, "app-title");
        AutomationId(_banner, "mode-banner");
        AutomationId(_account, "account-heading");
        AutomationId(_hint, "workspace-guidance");
        AutomationId(_host, "host-operation-result");
        AutomationId(_status, "status-footer");

        foreach (var id in ScenarioCatalog.RequiredIds)
        {
            _scenarios.Items.Add(new ComboBoxItem { Content = id, Tag = id });
        }

        _modes.Items.Add(new ComboBoxItem { Content = "Desktop Preview", Tag = "preview" });
        _modes.Items.Add(new ComboBoxItem { Content = "Evidence Archive", Tag = "archive" });
        _modes.Items.Add(new ComboBoxItem { Content = "Native Connection", Tag = "native" });
        _modes.SelectedIndex = vm.Mode == ApplicationMode.NativeConnection ? 2 : vm.Mode == ApplicationMode.EvidenceArchive ? 1 : 0;
        _scenarios.PlaceholderText = "Select a fixture scenario";
        foreach (var tag in XaiDeskIdentity.Navigation)
        {
            var item = new ListViewItem { Content = Label(tag), Tag = tag };
            AutomationName(item, Label(tag));
            _nav.Items.Add(item);
        }

        _nav.ItemClick += (_, e) =>
        {
            var item = _nav.Items.Cast<ListViewItem>().FirstOrDefault(x => ReferenceEquals(x, e.ClickedItem) || ReferenceEquals(x.Content, e.ClickedItem));
            if (item is not null) Navigate((string)item.Tag);
        };
        _nav.IsItemClickEnabled = true;
        _main.SelectionChanged += MainChanged;
        _scenarios.SelectionChanged += ScenarioChanged;
        _modes.SelectionChanged += ModeChanged;

        var filter = DeskButton("Apply filter");
        AutomationName(filter, "Apply filter");
        filter.Click += async (_, _) =>
        {
            _vm.Query = _search.Text ?? "";
            await _vm.FilterDecisionsAsync();
            BindList();
            BindHeader();
        };
        var save = Button("Validate and save draft", async () =>
        {
            _vm.SetMandateFields(_name.Text ?? "", Empty(_universe.Text), Empty(_horizon.Text), Empty(_schedule.Text), Empty(_timezone.Text),
                Int(_conc.Text), Int(_sector.Text), Long(_cash.Text), Int(_turnover.Text), Empty(_sources.Text));
            await _vm.SaveMandateAsync();
            BindHeader();
            BindList();
            BindDraft();
        });
        save.KeyboardAccelerators.Add(new KeyboardAccelerator { Key = VirtualKey.S, Modifiers = VirtualKeyModifiers.Control });
        var dup = Button("Duplicate draft", () => { _vm.DuplicateMandate(); BindDraft(); BindHeader(); });
        var pause = Button("Request pause (capability-gated)", async () =>
        {
            var result = await _vm.RequestHostOperationAsync("PAUSE_NEW_RISK");
            _host.Text = ShellViewModel.FormatHostOperation(result);
        });
        var activate = Button("Activate session", () => _host.Text = _vm.DisabledReasonFor("activate-session"));
        var openUri = Button("Open allowed source URI", OpenUri);
        var export = Button("Export redacted support zip", ShowExportDialog);
        var offline = Button("Start offline engine run", async () =>
        {
            if (_runButton is not null) _runButton.IsEnabled = false;
            _host.Text = "Running the offline demonstration… Waiting for the engine result.";
            try
            {
                var result = await _vm.StartOfflineEngineRunAsync();
                _host.Text = ShellViewModel.FormatHostOperation(result);
                BindHeader();
                BindList();
            }
            catch (Exception ex) { _host.Text = "Could not confirm the run result. Refresh to reconcile before retrying. " + ex.Message; }
            finally { if (_runButton is not null) _runButton.IsEnabled = true; }
        });
        offline.KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;
        offline.KeyboardAccelerators.Add(new KeyboardAccelerator { Key = VirtualKey.R, Modifiers = VirtualKeyModifiers.Control });

        App.CrashLog("before BuildShell");
        var root = BuildShell(filter, save, dup, pause, activate, openUri, export, offline);
        App.CrashLog("after BuildShell");
        _scaleRoot = root;
        Content = root;
        App.CrashLog("after Content");
        root.Loaded += (_, _) => DeskTheme.Apply(root);
        root.KeyDown += RootKeyDown;
        AssignTabOrder();
        ApplyWindowChrome(testDpiPercent);
        _vm.PropertyChanged += OnViewModelChanged;
        BindHeader();
        BindList();
        BindDraft();
        App.CrashLog("before Navigate chat");
        Navigate("chat");
        App.CrashLog("after Navigate chat");
        root.Loaded += async (_,_) => { Sovereign.Desktop.Updates.SiDeskUpdateHelper.SignalReady(ShellViewModel.AppVersion); if ((Environment.GetCommandLineArgs().Contains("--chat-layout-verify") || Environment.GetCommandLineArgs().Contains("--chat-steering-verify") || Environment.GetCommandLineArgs().Contains("--desk-store-verify") || Environment.GetCommandLineArgs().Contains("--quant-desk-verify"))) return; await InitializeUpdatesAsync(); StartSiDeskUpdateCheck(); if (!_updateStartupFailed) await InitializeAccountAsync(); await StartPhoneIfEnabledAsync(); await StartConnectorIfEnabledAsync(); await MaybeShowFirstRunSetupAsync(); };
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "SelectedNav" or "Query" or "SelectedDecision" or "SelectedOrder" or "SelectedEvidence" or "Mandate") return;
        if (_bindQueued)
        {
            return;
        }

        _bindQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _bindQueued = false;
            BindHeader();
            BindList();
        });
    }

    private void BindHeader()
    {
        _title.Text = _vm.AppTitle;
        _banner.Text = _vm.ModeBanner;
        _account.Text = _vm.AccountHeading;
        _engine.Text = _vm.EngineLabel;
        _provider.Text = _vm.ProviderLabel;
        _broker.Text = _vm.BrokerLabel;
        _recon.Text = _vm.ReconciliationLabel;
        _auth.Text = _vm.AuthorizationLabel;
        _status.Text = _vm.StatusLine + " | cash " + _vm.CashLabel + " | equity " + _vm.EquityLabel;
        _draftStatus.Text = _vm.DraftStatus;
        _draftError.Text = _vm.DraftErrors;
        _hint.Text = _vm.WorkspaceGuidance;
        BindDesign();
    }

    private void BindList()
    {
        _main.Items.Clear();
        var nav = CurrentSection;
        _vm.SelectedNav = nav;

        IEnumerable<string> rows = nav switch
        {
            "decisions" => _vm.Decisions.Select(DescribeDecision),
            "portfolio" => DescribePortfolio(),
            "orders" => _vm.Orders.Select(DescribeOrder).Concat(_vm.Events.Select(e => $"EVENT {e.Cursor} {e.Kind}: {e.Summary}")),
            "tools" => _vm.Capabilities.Select(c => $"{c.Id} {c.Version} status={c.CatalogStatus} avail={c.AvailabilityReason}"),
            "evaluation" => new[] { DescribeEval() },
            "overview" => new[] { "Offline demonstration", "The installed engine produces a synthetic three-cycle book. These balances and orders are not from a broker or from chat agents.", "Use the sections above to inspect the run, edit local drafts, or review the engine connection." },
            "connection" => new[]
            {
                $"Version {_vm.AppTitle}",
                $"Profile {_vm.ProfileDirectory}",
                $"Backend {_vm.Runtime?.BackendType}",
                $"OS {Environment.OSVersion}",
                $"Last error {_vm.LastError}",
                "Credential storage is not available in preview."
            },
            "chat" => new[] { XaiDeskIdentity.NavigationLabel("chat") },
            "room" => new[] { XaiDeskIdentity.NavigationLabel("room") },
            "grokbot" => new[] { XaiDeskIdentity.NavigationLabel("grokbot") },
            "settings" => new[] { $"Version {_vm.AppTitle}", $"Your data: {_vm.ProfileDirectory}", "Model and permission choices belong to each chat. Changing a page does not stop a working agent." },
            "mandate" => _vm.SavedMandates.Select(d => $"{d.Id} rev {d.Revision} {d.Name} {d.Activation}"),
            _ => DescribeHome()
        };
        foreach (var row in rows)
        {
            _main.Items.Add(row);
        }
        BindDesign();
    }

    private IEnumerable<string> DescribeHome()
    {
        yield return "Your SI Desk workspace\nOne place for conversations and the tools on this PC.";
        yield return $"Conversations\n{_conversations.Count} saved chats on this PC. Open Chat or choose a recent conversation to continue.";
        yield return "Later · offline demo\nThe synthetic portfolio, orders and engine controls are grouped separately. They are not your live account.";
    }

    private IEnumerable<string> DescribePortfolio()
    {
        if (!_vm.Account.IsKnown)
        {
            yield return "Account: " + _vm.Account.Describe();
            yield break;
        }

        var account = _vm.Account.Value!;
        yield return "Cash: " + account.Cash.Describe(money => money.Format());
        yield return "Ending equity: " + account.EndingEquity.Describe(money => money.Format());
        foreach (var holding in account.Holdings)
            yield return $"{holding.Instrument} qty={holding.Quantity.Describe()}";
    }

    private static string DescribeDecision(DecisionRecord d) =>
        $"{d.Id}  ·  {d.Action}  ·  {d.Instrument}\nQuantity: {d.Quantity.Describe()}\nAdmission: {d.Admission.Verdict}  ·  Risk: {d.Risk.Verdict}  ·  Execution: {d.Execution.Verdict}";

    private static string DescribeOrder(OrderRecord o) =>
        $"{o.Id}\nRequested: {o.RequestedState}  ·  Acknowledged: {o.AcknowledgedState.Describe()}\nCurrent: {o.CurrentState.Describe()}  ·  Remaining: {o.RemainingQuantity.Describe()} {string.Join(";", o.AmbiguityReasons)}";

    private string DescribeEval()
    {
        var e = _vm.Evaluation;
        return e is null
            ? "Evaluation not loaded."
            : $"campaign={e.CampaignId} sample={e.SampleCount.Describe()} missed={e.MissedCount} late={e.LateCount} abstain={e.AbstainCount} blocked={e.BlockedCount} software={e.SoftwareResult} ops={e.OperationalReadiness} investment={e.InvestmentVerdict} plot={e.PlotAbsenceReason}";
    }

    private void BindDraft()
    {
        _name.Text = _vm.Mandate.Name;
        _universe.Text = _vm.Mandate.Universe is null ? "" : string.Join(",", _vm.Mandate.Universe);
        _horizon.Text = _vm.Mandate.Horizon ?? "";
        _schedule.Text = _vm.Mandate.Schedule ?? "";
        _timezone.Text = _vm.Mandate.Timezone ?? "";
        _conc.Text = _vm.Mandate.ConcentrationCapBps?.ToString() ?? "";
        _sector.Text = _vm.Mandate.SectorCapBps?.ToString() ?? "";
        _cash.Text = _vm.Mandate.CashFloorMinorUnits?.ToString() ?? "";
        _turnover.Text = _vm.Mandate.TurnoverBps?.ToString() ?? "";
        _sources.Text = _vm.Mandate.AllowedSources is null ? "" : string.Join(",", _vm.Mandate.AllowedSources);
    }

    private async void ScenarioChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_scenarios.SelectedItem is ComboBoxItem item && item.Tag is string id)
        {
            await _vm.SwitchScenarioAsync(id);
            BindHeader();
            BindList();
        }
    }

    private async void ModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_modes.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            try
            {
                var mode = tag switch
                {
                    "archive" => ApplicationMode.EvidenceArchive,
                    "native" => ApplicationMode.NativeConnection,
                    _ => ApplicationMode.DesktopPreview
                };
                string? archive = mode == ApplicationMode.EvidenceArchive
                    ? Path.Combine(AppContext.BaseDirectory, "fixtures", "archives", "valid-hash-consistent")
                    : null;
                await _vm.SwitchModeAsync(mode, "admitted-blocked-execution", archive);
            }
            catch (Exception ex)
            {
                _host.Text = ex.Message;
            }

            BindHeader();
            BindList();
        }
    }

    private async void MainChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_main.SelectedItem is not string row)
        {
            return;
        }

        if (_vm.SelectedNav == "mandate")
        {
            var draft = _vm.SavedMandates.FirstOrDefault(d => row.StartsWith(d.Id + " ", StringComparison.Ordinal));
            if (draft is not null) { await _vm.LoadMandateAsync(draft.Id); BindDraft(); BindHeader(); }
            return;
        }

        var decision = _vm.Decisions.FirstOrDefault(d => row.StartsWith(d.Id, StringComparison.Ordinal));
        if (decision is not null)
        {
            await _vm.SelectDecisionAsync(decision.Id);
            _detail.Text = string.Join(Environment.NewLine, new[]
            {
                $"Decision {decision.Id}",
                $"Action {decision.Action} {decision.Instrument}",
                $"Qty {decision.Quantity.Describe()} Price {decision.Price.Describe(m => m.Format())}",
                $"Revision {decision.AccountRevision}",
                $"Admission {decision.Admission.Verdict}: {decision.Admission.Reason}",
                $"Risk {decision.Risk.Verdict}: {decision.Risk.Reason}",
                $"Execution {decision.Execution.Verdict}: {decision.Execution.Reason}",
                "Reasons: " + string.Join(" | ", decision.Reasons),
                "Override is not available. Invalid evidence cannot be accepted."
            });
            _evidence.Text = _vm.SelectedEvidence is null
                ? "No evidence document."
                : _vm.SelectedEvidence.Title + Environment.NewLine + _vm.SelectedEvidence.Text;
            return;
        }

        var order = _vm.Orders.FirstOrDefault(o => row.StartsWith(o.Id, StringComparison.Ordinal));
        if (order is not null)
        {
            _detail.Text = DescribeOrder(order) + Environment.NewLine + string.Join(Environment.NewLine, order.AmbiguityReasons);
        }
    }

    private async void OpenUri()
    {
        var uri = _vm.SelectedEvidence?.SourceUris.FirstOrDefault();
        if (uri is null)
        {
            _host.Text = "No source URI on the selected evidence.";
            return;
        }

        if (!Sovereign.Desktop.Core.Evidence.EvidenceSafety.IsAllowedUri(uri, out var parsed, out var reason))
        {
            _host.Text = "Refused: " + reason;
            return;
        }

        await Launcher.LaunchUriAsync(parsed);
    }

    internal static Button Button(string content, Action action)
    {
        var b = DeskButton(content);
        AutomationName(b, content);
        b.Click += (_, _) => action();
        return b;
    }

    internal static Button Button(string content, Func<Task> action)
    {
        var b = DeskButton(content);
        AutomationName(b, content);
        b.Click += async (_, _) =>
        {
            b.IsEnabled = false;
            try { await action(); }
            catch (Exception ex)
            {
                var dialog = new ContentDialog { Title = "Action could not complete", Content = new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap }, CloseButtonText = "Close", XamlRoot = b.XamlRoot };
                await dialog.ShowAsync();
            }
            finally { b.IsEnabled = true; }
        };
        return b;
    }

    internal static TextBlock Head(string text) => new()
    {
        Text = text, FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeHeading, Foreground = DeskTheme.Text,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, LineHeight = DeskTheme.Space24
    };

    internal static void AutomationName(FrameworkElement element, string name) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, name);

    private static void AutomationId(FrameworkElement element, string id) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(element, id);

    private static string Label(string tag) => tag is "chat" or "room" or "grokbot" or "settings"
        ? XaiDeskIdentity.NavigationLabel(tag)
        : "Chat";

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static int? Int(string? value) => int.TryParse(value, out var n) ? n : null;
    private static long? Long(string? value) => long.TryParse(value, out var n) ? n : null;

    private void AssignTabOrder()
    {
        foreach (var el in new FrameworkElement[]
                 {
                     _nav, _search, _scenarios, _modes, _main, _name, _universe, _horizon, _schedule, _timezone,
                     _conc, _sector, _cash, _turnover, _sources
                 })
        {
            el.TabIndex = ++_tab;
            el.UseSystemFocusVisuals = true;
        }
    }

    private void ApplyWindowChrome(int testDpiPercent)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(id);
        ApplyTaskbarIcon(hwnd, appWindow);
        Activated += (_, _) => ApplyTaskbarIcon(hwnd, appWindow);
        appWindow.TitleBar.ButtonBackgroundColor = DeskTheme.Surface.Color;
        appWindow.TitleBar.ButtonInactiveBackgroundColor = DeskTheme.Surface.Color;
        appWindow.TitleBar.ButtonForegroundColor = DeskTheme.Text.Color;
        appWindow.TitleBar.ButtonInactiveForegroundColor = DeskTheme.Muted.Color;
        appWindow.TitleBar.ButtonHoverBackgroundColor = DeskTheme.Hover.Color;
        appWindow.TitleBar.ButtonHoverForegroundColor = DeskTheme.Text.Color;
        appWindow.TitleBar.ButtonPressedBackgroundColor = DeskTheme.Pressed.Color;
        appWindow.TitleBar.ButtonPressedForegroundColor = DeskTheme.Text.Color;
        appWindow.Resize(new Windows.Graphics.SizeInt32(1440, 810));
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 1024;
            presenter.PreferredMinimumHeight = 720;
        }

        var scale = Math.Max(100, testDpiPercent) / 100.0;
        if (_scaleRoot is not null)
        {
            _scaleRoot.RenderTransformOrigin = new Windows.Foundation.Point(0, 0);
            _scaleRoot.RenderTransform = new ScaleTransform { ScaleX = scale, ScaleY = scale };
        }
    }

    private IntPtr _taskbarIconSmall;
    private IntPtr _taskbarIconBig;

    private void ApplyTaskbarIcon(IntPtr hwnd, AppWindow appWindow)
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "xAIDesk.ico");
        if (!System.IO.File.Exists(path)) return;
        appWindow.SetIcon(path);
        if (_taskbarIconSmall == IntPtr.Zero)
            _taskbarIconSmall = LoadImage(IntPtr.Zero, path, 1, 16, 16, 0x0010);
        if (_taskbarIconBig == IntPtr.Zero)
            _taskbarIconBig = LoadImage(IntPtr.Zero, path, 1, 32, 32, 0x0010);
        if (_taskbarIconSmall != IntPtr.Zero) SendMessage(hwnd, 0x0080, IntPtr.Zero, _taskbarIconSmall);
        if (_taskbarIconBig != IntPtr.Zero) SendMessage(hwnd, 0x0080, new IntPtr(1), _taskbarIconBig);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private void RootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            _vm.ClearSelection("escape");
            _exportDialog?.Hide();
            BindHeader();
            BindList();
            e.Handled = true;
        }
    }

    private async Task ShowExportDialog()
    {
        var preview = "Export app version, preferences, connection status, recovery status and recent errors. Source documents, chats, databases, credentials and env files stay out. Files with credential-like content are excluded.";
        var privateDrafts = new CheckBox { Content = "Include private draft content", IsChecked = false };
        AutomationName(privateDrafts, "Include private draft content in support export");
        var exportOptions = new StackPanel { Spacing = DeskTheme.Space12 };
        exportOptions.Children.Add(new TextBlock { Text = preview + Environment.NewLine + "Mode: " + _vm.ModeBanner, TextWrapping = TextWrapping.Wrap });
        exportOptions.Children.Add(privateDrafts);
        _exportDialog = new ContentDialog
        {
            Title = "Export redacted support zip",
            Content = exportOptions,
            PrimaryButtonText = "Export",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot
        };
        AutomationName(_exportDialog, "Export confirmation");
        var result = await _exportDialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            try
            {
                var zip = Path.Combine(_vm.ProfileDirectory, "support-export.zip");
                var exported = _vm.ExportSupport(zip, privateDrafts.IsChecked == true);
                _export.Text = $"Wrote {exported.ZipPath}. Included: {string.Join(", ", exported.IncludedRelativePaths)}. Excluded: {string.Join("; ", exported.ExcludedReasons)}";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _export.Text = "The support report could not be saved. Check the profile and destination, then retry.";
            }
        }

        _search.Focus(FocusState.Programmatic);
        _exportDialog = null;
    }

    public async Task RunEvidenceCapture(string outputDirectory, bool keyboardProbe, WalkthroughReceipt? walkthrough)
    {
        Directory.CreateDirectory(outputDirectory);
        await Task.Delay(400);
        var pages = XaiDeskIdentity.Navigation;
        foreach (var page in pages)
        {
            SelectNav(page);
            BindHeader();
            BindList();
            await Task.Delay(120);
            await CapturePng(Path.Combine(outputDirectory, $"walkthrough-{page}-dpi.png"));
        }

        try
        {
            if (keyboardProbe)
            {
                var names = ProbeTabOrder();
                File.WriteAllLines(Path.Combine(outputDirectory, "keyboard-focus-order.txt"), names);
            }
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(outputDirectory, "keyboard-focus-order.txt"), "PROBE_FAILED: " + ex);
        }

        File.WriteAllText(Path.Combine(outputDirectory, "ui-identity.txt"),
            _vm.AppTitle + Environment.NewLine + _vm.ModeBanner + Environment.NewLine + _vm.AccountHeading + Environment.NewLine + _vm.StatusLine);
        if (walkthrough is not null)
        {
            File.WriteAllText(Path.Combine(outputDirectory, "walkthrough-summary.txt"), walkthrough.Summary + Environment.NewLine + string.Join(Environment.NewLine, walkthrough.Steps.Select(s => $"{s.Id}: {(s.Passed ? "PASS" : "FAIL")} {s.Detail}")));
        }
    }

    public void SelectNav(string tag) => Navigate(tag);

    public IReadOnlyList<string> ProbeTabOrder()
    {
        var names = new List<string>();
        var options = new Microsoft.UI.Xaml.Input.FindNextElementOptions { SearchRoot = Content as DependencyObject };
        _nav.Focus(FocusState.Keyboard);
        for (var i = 0; i < 32; i++)
        {
            var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Content.XamlRoot) as FrameworkElement;
            var name = focused is null
                ? "(none)"
                : (string.IsNullOrWhiteSpace(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(focused)) ? focused.GetType().Name : Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(focused) + " " + focused.GetType().Name);
            names.Add(i + ": " + name);
            Microsoft.UI.Xaml.Input.FocusManager.TryMoveFocus(Microsoft.UI.Xaml.Input.FocusNavigationDirection.Next, options);
        }

        return names;
    }

    public async Task CapturePng(string path)
    {
        if (Content is not FrameworkElement root)
        {
            return;
        }

        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(root);
        var pixels = await bitmap.GetPixelsAsync();
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)!);
        var file = await folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth,
            (uint)bitmap.PixelHeight,
            96,
            96,
            ReadBuffer(pixels));
        await encoder.FlushAsync();
    }

    private static byte[] ReadBuffer(Windows.Storage.Streams.IBuffer buffer)
    {
        var data = new byte[buffer.Length];
        Windows.Storage.Streams.DataReader.FromBuffer(buffer).ReadBytes(data);
        return data;
    }
}
