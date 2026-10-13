using static Sovereign.Desktop.MainWindow;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sovereign.Desktop.Client;

namespace Sovereign.Desktop;

internal sealed partial class ChatConversation
{
    internal string? _projectFolder;
    private int _projectSessionEpoch;
    private Button? _openProject;
    private Button? _closeProject;
    private readonly ComboBox _projectBehavior = new() { Width = 156 };
    private readonly TextBlock _projectDescription = Note("Chat only · Open Folder to enable project tools.");

    private UIElement BuildProjectToolbar()
    {
        _openProject = Button("Open Folder…", OpenProjectFolderAsync);
        _closeProject = Button("Close Folder", CloseProjectFolder); _closeProject.IsEnabled = false;
        return new StackPanel { Spacing = DeskTheme.Space8, Children = { new StackPanel { Orientation = Orientation.Horizontal, Spacing = DeskTheme.Space8, Children = { _openProject, _closeProject, _chatNew! } }, BuildSubagentOption(), BuildSubagentPanelsOption() } };
    }

    private UIElement BuildProjectControls()
    {
        _projectBehavior.Items.Add("Always ask");
        _projectBehavior.Items.Add("Always allow");
        _projectBehavior.Items.Add("Plan");
        RestoreProjectBehavior();
        AutomationName(_projectBehavior, "Agent mode and permissions");
        ToolTipService.SetToolTip(_projectBehavior,
            "Always ask: approve each tool request. Always allow — tools within this project's permissions. Plan: read and search only. The selected folder stays the writable root. GLM/Kimi terminal commands run as your Windows user and are not sandboxed. Codex and Cursor retain their runtime policies.");
        _projectBehavior.SelectionChanged += async (_, _) => await ProjectPreferencesChangedAsync();
        StyleComposerPicker(_projectBehavior);
        return _projectBehavior;
    }

    private string ProjectConnectionStatus(string accountNote)
    {
        var folder = _projectFolder ?? "";
        var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(name)) name = folder;
        var permission = _saved.ProjectMode == CursorProjectMode.Plan
            ? "Plan · inspect this project without implementing."
            : _saved.Permissions == CursorPermissionPolicy.AlwaysAllow
                ? "Always allow — tools within this project's permissions."
                : "Always ask — each tool request needs approval.";
        return "Agent connected · " + permission + " Project: " + name + ". " + accountNote;
    }

    private void RestoreProjectBehavior() => _projectBehavior.SelectedIndex = _saved.ProjectMode == CursorProjectMode.Plan
        ? 2 : _saved.Permissions == CursorPermissionPolicy.AlwaysAllow ? 1 : 0;

    private async Task ProjectPreferencesChangedAsync()
    {
        if (_restoring || _chatBusy) return;
        var reconnect = _chatReady || _reconnectOnSend || ((CodexChat || CursorChat || GrokBuildChat) && !string.IsNullOrWhiteSpace(_saved.Model));
        Save(); // Capture model/draft before the new transport session clears the picker.
        _saved = _saved with { ProjectMode = _projectBehavior.SelectedIndex == 2 ? CursorProjectMode.Plan : CursorProjectMode.Agent,
            Permissions = _projectBehavior.SelectedIndex == 1 ? CursorPermissionPolicy.AlwaysAllow : CursorPermissionPolicy.AlwaysAsk };
        ResetChat(); _reconnectOnSend = reconnect; SetChatBusy(false); Save();
        if (reconnect) await ConnectChatAsync();
    }

    private void UpdateProjectHeader()
    {
        _projectDescription.Text = !_classicChat
            ? _projectFolder ?? "No project open"
            : _projectFolder is null
            ? "Chat only · Open Folder to enable project tools."
            : _projectFolder + (CursorChat || GrokBuildChat ? "" : " · This provider is chat only.");
        if (!_classicChat) RefreshTuiFooter();
        _projectBehavior.Visibility = (CodexChat || CursorChat || GrokBuildChat || PlanChat) && _projectFolder is not null ? Visibility.Visible : Visibility.Collapsed;
        _projectBehavior.IsEnabled = !_chatBusy;
        UpdateSubagentOption();
        UpdateSubagentPanelsOption();
        UpdateTeamOption();
        if (_closeProject is not null) _closeProject.IsEnabled = !_chatBusy && _projectFolder is not null;
    }

    internal async Task OpenProjectFolderAsync()
    {
        if (IsWorking) return;
        _connectionFlyout?.Hide();
        _show();
        // File selection is the explicit switch into project editing. A new session prevents stale cwd/context.
        SetChatBusy(true);
        try
        {
            var folder = await _owner.PickChatProjectFolderAsync();
            if (folder is null) return;
            SetChatBusy(false);
            AssignProjectWorkspace(_saved.ProjectId, folder);
        }
        catch (Exception ex) { _chatStatus.Text = "Could not open project: " + ex.Message; }
        finally { SetChatBusy(false); }
    }

    internal void CloseProjectFolder()
    {
        if (IsWorking) return;
        _connectionFlyout?.Hide();
        AssignProjectWorkspace(_saved.ProjectId, null);
    }

    private Task<string?> RequestProjectPermissionAsync(CursorPermissionRequest permission, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = _owner.PhoneGate.Track(permission, completion, ct, _saved.Title);
        if (!_owner.DispatcherQueue.TryEnqueue(async () =>
        {
            ContentDialog? dialog = null;
            var held = false;
            try
            {
                if (ct.IsCancellationRequested) { completion.TrySetCanceled(ct); return; }
                var allow = permission.Options.FirstOrDefault(o => o.Kind == "allow_once");
                var reject = permission.Options.FirstOrDefault(o => o.Kind == "reject_once");
                _owner.PhoneGate.BindDesktopHide(id, () => { if (dialog is not null) _owner.DispatcherQueue.TryEnqueue(() => dialog.Hide()); });
                if (completion.Task.IsCompleted) return;
                await _dialogs.WaitAsync(ct);
                held = true;
                if (ct.IsCancellationRequested || completion.Task.IsCompleted) return;
                var input = permission.Tool.Input;
                var origin = permission.Subagent is { } child ? "Subagent · " + child.Name : "Agent";
                dialog = new ContentDialog { XamlRoot = ((FrameworkElement)_owner.Content).XamlRoot, Title = "Permission · " + origin + " · " + _saved.Title,
                    PrimaryButtonText = allow is null ? "" : "Allow once", CloseButtonText = "Deny", DefaultButton = ContentDialogButton.Close,
                    Content = new ScrollViewer { MaxHeight = 380, Content = new StackPanel { Spacing = DeskTheme.Space8, Children = {
                        Note(_projectFolder ?? ""), new TextBlock { Text = permission.Tool.Title, TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = input.Length > 12000 ? input[..12000] + "\n[Display truncated — deny if details are insufficient]" : input,
                            TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }, Note("Applies to this request only. A phone can answer this same request.")
                    } } } };
                using var registration = ct.Register(() => { completion.TrySetCanceled(ct); _owner.DispatcherQueue.TryEnqueue(() => dialog.Hide()); });
                var result = await dialog.ShowAsync();
                if (!completion.Task.IsCompleted) completion.TrySetResult(result == ContentDialogResult.Primary ? allow?.Id : reject?.Id);
            }
            catch (Exception) { if (!completion.Task.IsCompleted) completion.TrySetResult(null); }
            finally { if (held) _dialogs.Release(); _owner.PhoneGate.Release(id); }
        }))
        {
            completion.TrySetResult(null);
            _owner.PhoneGate.Release(id);
        }
        return completion.Task;
    }
}
