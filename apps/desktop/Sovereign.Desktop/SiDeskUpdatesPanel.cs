using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sovereign.Desktop.Presentation;
using Sovereign.Desktop.Updates;

namespace Sovereign.Desktop;

/// <summary>Self-update from the public GitHub releases. Only the app folder is replaced; profile, chats, settings and saved keys stay put.</summary>
public sealed partial class MainWindow
{
    private static readonly string SiAppFolder = SiDeskInstall.Normalize(AppContext.BaseDirectory);
    private static readonly bool SiDevBuild = SiDeskInstall.IsDevBuild(SiAppFolder);
    private readonly TextBlock _siUpdateStatus = Note(SiDevBuild ? "dev build, updates off" : "Not checked yet.");
    private readonly ProgressBar _siUpdateProgress = new() { Minimum = 0, Maximum = 100, MaxWidth = 440, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
    private Button? _siUpdateCheck;
    private Button? _siUpdateInstall;
    private SiDeskRelease? _siUpdateRelease;
    private bool _siUpdateBusy;
    private readonly CancellationTokenSource _siUpdateLifetime = new();

    private StackPanel SiDeskUpdatesSection()
    {
        _siUpdateStatus.TextWrapping = TextWrapping.Wrap;
        AutomationName(_siUpdateStatus, "Update status");
        _siUpdateCheck = Button("Check for updates", () => CheckSiDeskUpdatesAsync(false));
        _siUpdateInstall = Button("Update", InstallSiDeskUpdateAsync);
        _siUpdateInstall.Visibility = _siUpdateRelease is null ? Visibility.Collapsed : Visibility.Visible;
        _siUpdateCheck.IsEnabled = !SiDevBuild;
        AutomationName(_siUpdateCheck, "Check for updates");
        Closed += (_, _) => _siUpdateLifetime.Cancel();
        return new StackPanel { Spacing = DeskTheme.Space12, Children = {
            Head("Updates"),
            Note("SI Desk asks GitHub for the latest public release when it starts. An update replaces only the app folder. Your chats, settings, profile and saved keys are not touched."),
            _siUpdateStatus,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = DeskTheme.Space8, Children = { _siUpdateCheck, _siUpdateInstall } },
            _siUpdateProgress } };
    }

    private void StartSiDeskUpdateCheck()
    {
        if (SiDevBuild) { _siUpdateStatus.Text = "dev build, updates off"; return; }
        foreach (var key in Environment.GetEnvironmentVariables().Keys)
            if (key is string name && name.StartsWith("SOVEREIGN_QA", StringComparison.OrdinalIgnoreCase)) return;
        _ = CheckSiDeskUpdatesAsync(true);
    }

    private async Task CheckSiDeskUpdatesAsync(bool startup)
    {
        if (SiDevBuild) { _siUpdateStatus.Text = "dev build, updates off"; return; }
        if (_siUpdateBusy) return;
        _siUpdateBusy = true;
        if (_siUpdateCheck is not null) _siUpdateCheck.IsEnabled = false;
        _siUpdateStatus.Text = "Checking GitHub for updates…";
        try
        {
            using var client = new SiDeskUpdateClient();
            var result = await client.CheckAsync(ShellViewModel.AppVersion, _siUpdateLifetime.Token);
            _siUpdateRelease = result.State == SiDeskCheckState.Available ? result.Release : null;
            _siUpdateStatus.Text = result.Message;
            if (_siUpdateInstall is not null) _siUpdateInstall.Visibility = _siUpdateRelease is null ? Visibility.Collapsed : Visibility.Visible;
            if (_siUpdateRelease is { } release) ShowSiDeskUpdateBanner(release);
            else _updateBanner.IsOpen = false;
        }
        catch (OperationCanceledException) when (_siUpdateLifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or System.Text.Json.JsonException or FormatException)
        {
            _siUpdateStatus.Text = "Could not check for updates. " + (ex is OperationCanceledException ? "GitHub did not answer in time." : ex.Message);
            if (!startup) App.CrashLog("Update check failed: " + ex.GetType().Name);
        }
        finally
        {
            _siUpdateBusy = false;
            if (_siUpdateCheck is not null) _siUpdateCheck.IsEnabled = true;
        }
    }

    private void ShowSiDeskUpdateBanner(SiDeskRelease release)
    {
        var update = Button("Update", InstallSiDeskUpdateAsync);
        var later = Button("Later", () => { _updateBanner.IsOpen = false; });
        AutomationName(update, "Update SI Desk");
        AutomationName(later, "Update later");
        _updateBanner.Title = "SI Desk " + release.DisplayVersion + " available";
        _updateBanner.Message = "";
        _updateBanner.ActionButton = null;
        _updateBanner.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DeskTheme.Space8, Margin = new Thickness(0, 0, 0, DeskTheme.Space8), Children = { update, later } };
        _updateBanner.IsOpen = true;
    }

    private async Task InstallSiDeskUpdateAsync()
    {
        if (SiDevBuild) { _siUpdateStatus.Text = "dev build, updates off"; return; }
        if (_siUpdateBusy || _siUpdateRelease is not { } release) return;
        _siUpdateBusy = true;
        _updateBanner.IsOpen = false;
        if (_siUpdateCheck is not null) _siUpdateCheck.IsEnabled = false;
        if (_siUpdateInstall is not null) _siUpdateInstall.IsEnabled = false;
        _siUpdateProgress.Value = 0;
        _siUpdateProgress.Visibility = Visibility.Visible;
        var work = Path.Combine(Path.GetTempPath(), "SIDesk-update-" + Guid.NewGuid().ToString("N"));
        try
        {
            var install = SiDeskInstall.ValidateInstall(SiAppFolder, SiDeskInstall.ProtectedPaths(_vm.ProfileDirectory));
            var helperSource = Path.Combine(install, SiDeskUpdateHelper.ExeName);
            if (!File.Exists(helperSource))
                throw new SiDeskUpdateRefusedException("This copy has no " + SiDeskUpdateHelper.ExeName + ". Download SIDesk-" + release.DisplayVersion + "-win-x64.zip from GitHub instead.");
            _siUpdateStatus.Text = "Downloading SI Desk " + release.DisplayVersion + "…";
            using var client = new SiDeskUpdateClient();
            var zip = await client.DownloadAsync(release, work, new Progress<int>(value => _siUpdateProgress.Value = value), _siUpdateLifetime.Token);
            _siUpdateStatus.Text = "Checked SHA-256. Unpacking next to the app folder…";
            var staged = await Task.Run(() => SiDeskFolderSwap.Stage(zip, install));
            var helper = Path.Combine(work, SiDeskUpdateHelper.ExeName);
            File.Copy(helperSource, helper, true);
            var request = new SiDeskHelperRequest(Environment.ProcessId, install, staged, release.DisplayVersion, Path.Combine(work, "ready.txt"), _vm.ProfileDirectory);
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = work };
            foreach (var argument in request.ToArgs()) start.ArgumentList.Add(argument);
            using (Process.Start(start) ?? throw new IOException("The update helper did not start.")) { }
            _siUpdateStatus.Text = "Restarting SI Desk to finish the update…";
            Close();
            Application.Current.Exit();
        }
        catch (OperationCanceledException) when (_siUpdateLifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            _siUpdateStatus.Text = "Update stopped. " + (ex is OperationCanceledException ? "The download timed out." : ex.Message);
            _siUpdateProgress.Visibility = Visibility.Collapsed;
            try { if (Directory.Exists(work)) Directory.Delete(work, true); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            try { var staged = SiDeskFolderSwap.StagedPath(SiAppFolder); if (Directory.Exists(staged)) Directory.Delete(staged, true); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
        }
        finally
        {
            _siUpdateBusy = false;
            if (_siUpdateCheck is not null) _siUpdateCheck.IsEnabled = true;
            if (_siUpdateInstall is not null) _siUpdateInstall.IsEnabled = true;
        }
    }
}
