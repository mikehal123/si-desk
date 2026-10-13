using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sovereign.Desktop.Updates;
using Sovereign.Desktop.Presentation;

namespace Sovereign.Desktop;

public sealed partial class MainWindow
{
    private readonly InfoBar _updateBanner = new() { IsOpen = false, IsClosable = true, Severity = InfoBarSeverity.Informational, Title = "Update available" };
    private readonly TextBlock _updateStatus = Note("Check for new SI Desk versions.");
    private readonly Button _updateAction = new() { Content = "Download update", Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    private UpdateRelease? _availableUpdate;
    private string? _updateEnvelope;
    private string? _downloadedUpdate;
    private bool _updateBusy;
    private bool _updateClosing;
    private readonly CancellationTokenSource _updateLifetime = new();

    private void BuildUpdates()
    {
        BuildGitHubUpdates();
        _settingsUpdates.Children.Add(_updateStatus);
        _settingsUpdates.Children.Add(Button("Check for updates", async () => await CheckUpdatesAsync(false)));
        _settingsUpdates.Children.Add(_updateAction);
        _updateAction.Click += async (_, _) => await DownloadOrInstallAsync();
        _updateBanner.ActionButton = Button("View update", () => Navigate("settings"));
        _updateTimer.Tick += async (_, _) => await CheckUpdatesAsync(true);
        Closed += (_, _) => { _updateTimer.Stop(); _updateLifetime.Cancel(); };
    }

    private Task InitializeUpdatesAsync()
    {
        _updateStartupFailed = false;
        _updateStatus.Text = "Updates are off.";
        return Task.CompletedTask;
    }

    private Task CheckUpdatesAsync(bool quiet)
    {
        if (!quiet) _updateStatus.Text = "Updates are off.";
        return Task.CompletedTask;
    }

    private async Task CheckUpdatesAsyncRemoved(bool quiet)
    {
        if (_updateBusy || _downloadedUpdate is not null) return;
        _updateBusy = true;
        try
        {
            if (!quiet) _updateStatus.Text = "Checking for updates…";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_updateLifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var client = new UpdateClient(await GitHubUpdateTokenAsync(timeout.Token));
            var (release, envelope) = await client.CheckAsync(timeout.Token);
            _githubUpdateStatus.Text = "GitHub connected — private release access verified.";
            if (UpdatePackage.ParseVersion(release.Version) <= UpdatePackage.ParseVersion(ShellViewModel.AppVersion.Split('-')[0]))
            { if (!quiet) _updateStatus.Text = "You’re up to date — " + ShellViewModel.AppVersion; return; }
            if (_availableUpdate is not null && UpdatePackage.ParseVersion(release.Version) < UpdatePackage.ParseVersion(_availableUpdate.Version)) return;
            _availableUpdate = release; _updateEnvelope = envelope;
            _updateStatus.Text = $"Version {release.Version} · {release.Bytes / 1_000_000} MB\n{release.Notes}";
            _updateBanner.Message = $"SI Desk {release.Version} is ready to download.";
            _updateBanner.IsOpen = true; _updateAction.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) { if (!quiet) _updateStatus.Text = "Update check timed out. Try again when connected."; }
        catch (Exception ex) { if (!quiet) _updateStatus.Text = "Could not check for updates. " + ex.Message; }
        finally { _updateBusy = false; }
    }

    private bool WorkBlocksUpdate => _conversations.Any(c => c.IsWorking) ||
        _roomAiCancel is not null || _roomConnection is not null || _roomHost is not null || _signInCancel is not null || _githubUpdateSignIn is not null || _runButton?.IsEnabled == false || DeskWorkBlocksUpdate;

    private async Task DownloadOrInstallAsync()
    {
        if (_updateBusy || _availableUpdate is null || _updateClosing) return;
        _updateBusy = true; _updateAction.IsEnabled = false;
        try
        {
            var baseDir = UpdatePaths.ValidateInstall(AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            if (_downloadedUpdate is null)
            {
                var work = Path.Combine(baseDir, "updates", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
                using var client = new UpdateClient(await GitHubUpdateTokenAsync(_updateLifetime.Token));
                await client.DownloadAsync(_availableUpdate, Path.Combine(work, "update.zip"), new Progress<int>(p => _updateStatus.Text = $"Downloading update… {p}%"), _updateLifetime.Token);
                await File.WriteAllTextAsync(Path.Combine(work, "update.json"), _updateEnvelope);
                _downloadedUpdate = work;
            }
            _updateAction.Content = "Update and restart";
            if (WorkBlocksUpdate)
            {
                _updateStatus.Text = "Update downloaded and verified. Finish active agents and calculations, and leave any shared room, then click Update and restart.";
                return;
            }
            var dialog = new ContentDialog { XamlRoot = Content.XamlRoot, Title = "Update and restart SI Desk?", Content = $"Install {_availableUpdate.Version}. Your chats, settings and profile stay on this PC. The previous app is kept for recovery.", PrimaryButtonText = "Update and restart", CloseButtonText = "Later", DefaultButton = ContentDialogButton.Close };
            await _chatDialogs.WaitAsync();
            ContentDialogResult result;
            try { result = await dialog.ShowAsync(); } finally { _chatDialogs.Release(); }
            if (result != ContentDialogResult.Primary) { _updateStatus.Text = "Update downloaded. Install whenever you’re ready."; return; }
            if (WorkBlocksUpdate) { _updateStatus.Text = "Work started while the dialog was open. Finish it before restarting."; return; }
            await WithUpdateBarrierAsync("Saving and verifying a full backup of profile data and retained evidence. Active work will not be cancelled or resumed.", async () =>
            {
                var snapshot = await PrepareDeskUpdateAsync(_availableUpdate.Version);
                var helper = Path.Combine(_downloadedUpdate, "SovereignAI.Updater.exe");
                File.Copy(Path.Combine(AppContext.BaseDirectory, "updater", "SovereignAI.Updater.exe"), helper, true);
                using var process = Process.GetCurrentProcess();
                var request = new UpdateRequest(AppContext.BaseDirectory, _vm.ProfileDirectory, process.Id, process.StartTime.ToUniversalTime().Ticks, ShellViewModel.AppVersion.Split('-')[0], snapshot);
                await File.WriteAllTextAsync(Path.Combine(_downloadedUpdate, "request.json"), JsonSerializer.Serialize(request));
                var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _downloadedUpdate }; start.ArgumentList.Add(_downloadedUpdate);
                _ = Process.Start(start) ?? throw new IOException("Could not launch update helper.");
                _updateClosing = true;
            });
            Close();
        }
        catch (Exception ex) { _updateStatus.Text = "Update was not installed. " + ex.Message; }
        finally
        {
            if (!_updateClosing) { _deskUpdatePause?.Dispose(); _deskUpdatePause = null; }
            _updateBusy = false; _updateAction.IsEnabled = true;
        }
    }
}
