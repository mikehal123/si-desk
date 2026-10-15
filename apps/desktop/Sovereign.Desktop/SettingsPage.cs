using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sovereign.Desktop.Client;
using Sovereign.Desktop.Core;
using Sovereign.Desktop.Presentation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Security.Credentials;

namespace Sovereign.Desktop;

public sealed partial class MainWindow
{
    private readonly Grid _settingsPage = new() { Visibility = Visibility.Collapsed, Padding = new Thickness(DeskTheme.Space24), Background = DeskTheme.Background };
    private readonly StackPanel _settingsUpdates = new() { Spacing = DeskTheme.Space12 };
    private readonly TextBlock _statusCursor = Note("Not checked");
    private readonly TextBlock _statusGrokBuild = Note("Not checked");
    private readonly ProgressRing _grokBuildProgress = new() { Width = 16, Height = 16, IsActive = false, Visibility = Visibility.Collapsed };
    private bool _grokBuildBusy;
    private readonly TextBlock _statusXaiApi = Note("Not set");
    private readonly PasswordBox _xaiApiKey = new() { Header = "API key", PlaceholderText = "xAI API key", MaxWidth = 440, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, DeskTheme.Space4, 0, 0) };
    private readonly TextBlock _settingsDataStatus = Note("");
    private readonly CheckBox _lineReviewOption = new() { Content = "Line review for plans and diffs" };
    private readonly CheckBox _classicChatOption = new() { Content = "Classic chat view", IsChecked = true };
    private readonly TextBlock _settingsProfilePath = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeBody, Foreground = DeskTheme.Text, LineHeight = DeskTheme.Space24 };
    private readonly StackPanel _githubWait = new() { Spacing = DeskTheme.Space8 };
    private readonly Button _cancelSignInButton = new() { Visibility = Visibility.Collapsed, Content = "Cancel sign-in" };

    private void OpenSettings()
    {
        CloseRoomView(); CloseChatView(); CloseGrokbot();
        if (_workspaceContent is not null) _workspaceContent.Visibility = Visibility.Collapsed;
        RefreshConnectionStatuses();
        _settingsPage.Visibility = Visibility.Visible;
        MarkSystem("settings");
    }
    private void CloseSettings() => _settingsPage.Visibility = Visibility.Collapsed;
    private void BuildSettingsPage()
    {
        AutomationName(_xaiApiKey, "xAI API key");
        AutomationId(_xaiApiKey, "grokbot-blocked-xai-key");
        AutomationName(_statusCursor, "Cursor status");
        AutomationName(_statusGrokBuild, "Grok Build status");
        _statusGrokBuild.TextWrapping = TextWrapping.Wrap;
        AutomationName(_statusXaiApi, "xAI API status");
        _lineReviewOption.IsChecked = LineReviewPreference.Load(_vm.ProfileDirectory);
        AutomationName(_lineReviewOption, "Line review for plans and diffs");
        ToolTipService.SetToolTip(_lineReviewOption, "Show Review on a plan or finished file edit. Turn off to hide the button. Saved reviews stay with the conversation.");
        _lineReviewOption.Click += (_, _) =>
        {
            LineReviewPreference.Save(_vm.ProfileDirectory, _lineReviewOption.IsChecked == true);
            foreach (var chat in _conversations) chat.ApplyLineReviewSetting();
        };
        _classicChatOption.IsChecked = ClassicChatPreference.Load(_vm.ProfileDirectory);
        AutomationName(_classicChatOption, "Classic chat view");
        ToolTipService.SetToolTip(_classicChatOption, "Use the previous card layout. Off keeps the terminal transcript.");
        _classicChatOption.Click += (_, _) =>
        {
            ClassicChatPreference.Save(_vm.ProfileDirectory, _classicChatOption.IsChecked == true);
            foreach (var chat in _conversations) chat.ApplyClassicChatSetting();
        };
        var heading = new StackPanel { Spacing = DeskTheme.Space4, Margin = new Thickness(0, 0, 0, DeskTheme.Space8), Children = {
            new TextBlock { Text = "Settings", FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeTitle, Foreground = DeskTheme.Text, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
            Note("Connections, data, and updates on this PC.") } };
        var page = new StackPanel { Spacing = DeskTheme.Space16, MaxWidth = 920, Children = {
            heading,
            SettingsCard(ConnectionsSection()), SettingsCard(DataSection()), SettingsCard(SkillLibrarySection()),
            SettingsCard(PhoneSection()), SettingsCard(ConnectorSection()), SettingsCard(SiDeskUpdatesSection()), SettingsCard(AboutSection()) } };
        _settingsPage.Children.Add(PageScroll(page, minimumWidth: 620, centered: true));
        RefreshConnectionStatuses();
    }
    private StackPanel ConnectionsSection()
    {
        var section = new StackPanel { Spacing = DeskTheme.Space12, Children = {
            Head("Connections"),
            Note("Cursor uses your Cursor account. Grok Build is the installed Grok CLI. The xAI API key stays in Windows Credential Manager.") } };
        var cursorActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DeskTheme.Space8, Children = {
            Button("Sign in", SignInCursorAsync), Button("Check", RefreshCursorStatus) } };
        ConnectionRow(section, "Cursor", _statusCursor, cursorActions);
        var grokSignIn = Button("Sign in", SignInGrokBuildAsync);
        var grokSignOut = Button("Sign out", SignOutGrokBuildAsync);
        var grokCheck = Button("Check", CheckGrokBuildAsync);
        AutomationName(grokSignIn, "Grok Build sign in");
        AutomationName(grokSignOut, "Grok Build sign out");
        AutomationName(grokCheck, "Grok Build check");
        var grokActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DeskTheme.Space8, Children = { grokSignIn, grokSignOut, grokCheck } };
        ConnectionRow(section, "Grok Build", _statusGrokBuild, grokActions, _grokBuildProgress);
        ConnectionRow(section, "xAI API", _statusXaiApi, Button("Save", SaveXaiApiKey));
        section.Children.Add(_xaiApiKey);
        var checkSetup = Button("Check setup", ShowSetupCheckAsync);
        AutomationName(checkSetup, "Check setup");
        section.Children.Add(checkSetup);
        return section;
    }
    private StackPanel DataSection()
    {
        _settingsProfilePath.Text = _vm.ProfileDirectory;
        return new StackPanel { Spacing = DeskTheme.Space12, Children = {
            Head("Data"),
            new StackPanel { Spacing = DeskTheme.Space4, Children = { _lineReviewOption, _classicChatOption } },
            SettingsDivider(),
            new StackPanel { Spacing = DeskTheme.Space8, Children = {
                SettingsCaption("Profile path"),
                new Border { Background = DeskTheme.Raised, CornerRadius = new CornerRadius(DeskTheme.Radius6), Padding = new Thickness(DeskTheme.Space12), Child = _settingsProfilePath } } },
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = DeskTheme.Space8, Children = {
                Button("Copy path", CopyProfilePath),
                Button("Open app data folder", async () => { await Windows.System.Launcher.LaunchFolderPathAsync(_vm.ProfileDirectory); }),
                Button("Export support report", ShowExportDialog) } },
            SettingsDivider(),
            _settingsDataStatus } };
    }
    private static StackPanel AboutSection()
    {
        var identity = new Grid { ColumnSpacing = DeskTheme.Space12 };
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        identity.ColumnDefinitions.Add(new ColumnDefinition());
        identity.Children.Add(DeskLogo(DeskTheme.Space24 + DeskTheme.Space8));
        var details = new StackPanel { Spacing = DeskTheme.Space4, VerticalAlignment = VerticalAlignment.Center, Children = {
            Head("Super Intelligence Desk"), SettingsCaption("Version " + ShellViewModel.AppVersion) } };
        Grid.SetColumn(details, 1); identity.Children.Add(details);
        return new StackPanel { Spacing = DeskTheme.Space12, Children = { Head("About"), identity } };
    }

    private static Border SettingsCard(StackPanel section)
    {
        section.Spacing = DeskTheme.Space12;
        return Card(section);
    }

    private static Border SettingsDivider() => new() { Height = 1, Background = DeskTheme.Divider };

    private static TextBlock SettingsCaption(string text)
    {
        var caption = Note(text);
        caption.FontSize = DeskTheme.SizeCaption;
        caption.LineHeight = DeskTheme.Space16;
        return caption;
    }

    private static void ConnectionRow(StackPanel parent, string name, TextBlock status, FrameworkElement action, FrameworkElement? besideStatus = null)
    {
        parent.Children.Add(SettingsDivider());
        var grid = new Grid { ColumnSpacing = DeskTheme.Space12, MinHeight = DeskTheme.Space24 + DeskTheme.Space16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, FontFamily = DeskTheme.UiFont, Foreground = DeskTheme.Text, FontSize = DeskTheme.SizeBody, TextWrapping = TextWrapping.Wrap });
        status.FontSize = DeskTheme.SizeCaption; status.LineHeight = DeskTheme.Space16;
        status.TextWrapping = TextWrapping.Wrap;
        status.VerticalAlignment = VerticalAlignment.Center;
        FrameworkElement statusCell = status;
        if (besideStatus is not null)
        {
            var pair = new Grid { ColumnSpacing = DeskTheme.Space8, VerticalAlignment = VerticalAlignment.Center };
            pair.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            pair.ColumnDefinitions.Add(new ColumnDefinition());
            besideStatus.VerticalAlignment = VerticalAlignment.Center;
            pair.Children.Add(besideStatus);
            Grid.SetColumn(status, 1);
            pair.Children.Add(status);
            statusCell = pair;
        }
        Grid.SetColumn(statusCell, 1); grid.Children.Add(statusCell);
        action.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(action, 2); grid.Children.Add(action);
        parent.Children.Add(grid);
    }
    private void SyncSignInCancel()
    {
        _cancelSignInButton.Visibility = _signInCancel is not null ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SyncGitHubCancel()
    {
        if (_cancelGitHubButton is not null) _cancelGitHubButton.Visibility = _githubUpdateSignIn is not null ? Visibility.Visible : Visibility.Collapsed;
    }
    private void RefreshConnectionStatuses()
    {
        RefreshCursorStatus();
        RefreshXaiApiStatus();
    }
    private void RefreshCursorStatus()
    {
        var runtime = CursorChatClient.RuntimeDirectory;
        var installed = File.Exists(Path.Combine(runtime, "node.exe")) && File.Exists(Path.Combine(runtime, "index.js"));
        _statusCursor.Text = installed ? "Connector installed" : "Connector not installed";
    }
    private async Task SignInCursorAsync()
    {
        _statusCursor.Text = "Waiting for Cursor sign-in…";
        try
        {
            using var login = new CursorChatClient(Path.Combine(_vm.ProfileDirectory, "cursor-chat-workspace"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await login.LoginAsync(timeout.Token);
            _statusCursor.Text = "Sign-in finished. Connect in Chat to refresh models.";
        }
        catch (Exception ex) { _statusCursor.Text = DeskConnectionErrors.ForConnect(ex, DeskConnectKind.Cursor); }
    }
    private Task SignInGrokBuildAsync() => RunGrokBuildAsync("Opening Grok sign-in…", static ct => GrokBuildChatClient.LoginAsync(ct));
    private Task SignOutGrokBuildAsync() => RunGrokBuildAsync("Opening Grok sign-out…", static ct => GrokBuildChatClient.LogoutAsync(ct));
    private Task CheckGrokBuildAsync() => RunGrokBuildAsync("Checking…", null);
    private async Task RunGrokBuildAsync(string opening, Func<CancellationToken, Task>? first)
    {
        if (_grokBuildBusy) return;
        _grokBuildBusy = true;
        _grokBuildProgress.IsActive = true;
        _grokBuildProgress.Visibility = Visibility.Visible;
        _statusGrokBuild.Text = opening;
        try
        {
            if (first is not null) await first(CancellationToken.None);
            _statusGrokBuild.Text = "Checking…";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var report = await GrokBuildAccount.CheckAsync(timeout.Token);
            _statusGrokBuild.Text = GrokBuildConnectionText.Format(report);
        }
        catch (Exception ex)
        {
            var message = DeskConnectionErrors.ForConnect(ex, DeskConnectKind.GrokBuild);
            _statusGrokBuild.Text = message;
            throw new IOException(message);
        }
        finally
        {
            _grokBuildProgress.IsActive = false;
            _grokBuildProgress.Visibility = Visibility.Collapsed;
            _grokBuildBusy = false;
        }
    }
    private void RefreshXaiApiStatus() => _statusXaiApi.Text = XaiApiKeySaved() ? "Key saved" : "Not set";
    private static bool XaiApiKeySaved()
    {
        try
        {
            return DeskVault.Exists(XaiDeskIdentity.XaiApiCredentialResource, XaiDeskIdentity.XaiApiCredentialAccount);
        }
        catch { return false; }
    }
    private void SaveXaiApiKey()
    {
        var key = _xaiApiKey.Password.Trim();
        if (key.Length == 0) { RefreshXaiApiStatus(); return; }
        try
        {
            DeskVault.Remove(XaiDeskIdentity.XaiApiCredentialResource, XaiDeskIdentity.XaiApiCredentialAccount);
            new PasswordVault().Add(new PasswordCredential(XaiDeskIdentity.XaiApiCredentialResource, XaiDeskIdentity.XaiApiCredentialAccount, key));
            _xaiApiKey.Password = "";
            _statusXaiApi.Text = "Key saved";
        }
        catch { RefreshXaiApiStatus(); }
    }
    private void CopyProfilePath()
    {
        var data = new DataPackage(); data.SetText(_vm.ProfileDirectory); Clipboard.SetContent(data);
        _settingsDataStatus.Text = "Profile path copied.";
    }

    private async Task MaybeShowFirstRunSetupAsync()
    {
        try
        {
            if (!DeskSetupPrompt.ShouldShow(_vm.ProfileDirectory)) return;
            if (Content is not FrameworkElement { XamlRoot: not null }) return;
            DeskSetupPrompt.MarkShown(_vm.ProfileDirectory);
            await ShowSetupCheckAsync();
        }
        catch (Exception)
        {
            // First launch still opens if the check cannot run. Settings can run it again.
        }
    }

    private async Task ShowSetupCheckAsync()
    {
        if (Content is not FrameworkElement root || root.XamlRoot is null) return;
        while (true)
        {
            var report = await DeskSetupCheck.CollectAsync(new DeskSetupSources
            {
                XaiApiKey = ReadSavedXaiKey(),
                CursorRuntimeInstalled = CursorChatClient.IsRuntime(CursorChatClient.RuntimeDirectory),
                CursorSignedIn = CursorLoginProbe.IsSignedIn(),
                GrokFound = GrokBuildChatClient.FindExecutable() is not null
            }, CancellationToken.None);
            var lines = new StackPanel { Spacing = DeskTheme.Space8 };
            foreach (var item in report.Items)
            {
                var row = new TextBlock
                {
                    Text = item.Name + " — " + item.Line,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = DeskTheme.UiFont,
                    FontSize = DeskTheme.SizeBody,
                    Foreground = item.Ok ? DeskTheme.Success : DeskTheme.Warning,
                    LineHeight = DeskTheme.Space24
                };
                AutomationName(row, item.Name + " " + item.State);
                lines.Children.Add(row);
            }
            var body = new StackPanel { Spacing = DeskTheme.Space12 };
            body.Children.Add(Note("Each item is OK or Missing. Secret values stay on this PC and are not shown."));
            body.Children.Add(lines);
            var dialog = new ContentDialog
            {
                XamlRoot = root.XamlRoot,
                Title = "Setup check",
                Content = body,
                PrimaryButtonText = "Check again",
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close
            };
            dialog.Resources["ContentDialogMaxWidth"] = 640d;
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }
    }

    private static void ApplyTailscaleRemoteNote(TextBlock note, ComboBox bind)
    {
        var offered = false;
        var selected = false;
        for (var i = 0; i < bind.Items.Count; i++)
        {
            if (bind.Items[i] as string != "Tailscale only") continue;
            offered = true;
            if (bind.SelectedIndex == i) selected = true;
        }

        note.Text = TailscaleRemoteAccessNote.Text;
        note.Visibility = TailscaleRemoteAccessNote.ShouldShow(selected, offered) ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string? ReadSavedXaiKey()
    {
        try
        {
            return DeskVault.ReadPassword(XaiDeskIdentity.XaiApiCredentialResource, XaiDeskIdentity.XaiApiCredentialAccount);
        }
        catch
        {
            return null;
        }
    }
}
