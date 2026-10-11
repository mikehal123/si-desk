using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Sovereign.Desktop.Client;

namespace Sovereign.Desktop;

public sealed partial class MainWindow
{
    private Func<Task<string?>>? _qaProjectFolderPicker;
    // Explicit QA profile + --chat-layout-verify + environment opt-in only.
    private async Task RunProjectDialogVerificationAsync(string output)
    {
        var checks = new List<string>();
        void Check(bool value, string message) { if (!value) throw new IOException(message); checks.Add(message); }
        try
        {
            ValidateCodexQaProfile(_vm.ProfileDirectory);
            if (Environment.GetEnvironmentVariable("SOVEREIGN_QA_PROJECT_LIVE") == "1") throw new IOException("Project dialog QA forbids live providers.");
            Title = _vm.AppTitle + " · project dialog QA";
            await Task.Delay(500);
            SelectNav("home");
            var folder = Path.Combine(_vm.ProfileDirectory, "selected-workspace");
            Directory.CreateDirectory(folder);
            _qaProjectFolderPicker = () => Task.FromResult<string?>(folder);
            if (Environment.GetEnvironmentVariable("SOVEREIGN_QA_PROJECT_DIALOG_REOPEN") == "1")
            {
                Check(_projects.Count == 1 && _projects[0].Name == "QA working project", "fresh process reloads the project created by the native dialog");
                Check(_projects[0].Folder == folder && _conversations.Any(chat => chat.ProjectId == _projects[0].Id && chat._projectFolder == folder), "fresh process restores the folder and its filed conversation");
                Check(ProjectNodes(_chatRecents).OfType<TextBlock>().Any(text => text.Text == "▾ QA working project"), "reopened project is visible in the sidebar");
                await CapturePng(Path.Combine(output, "project-reopened.png"));
                File.WriteAllText(Path.Combine(output, "receipt.json"), JsonSerializer.Serialize(new { passed = true, checks, main_accessed = false, provider_calls = 0 }));
                return;
            }
            var activity = DateTimeOffset.UtcNow.AddDays(-2);
            var seed = ChatArchive.New(DeskChatConnections.CursorStored) with { Title = "Existing question", Model = "synthetic-model", Draft = "Keep this draft", Updated = activity,
                PendingInstructions = [new SavedChatInstruction(Guid.NewGuid().ToString("N"), "Keep this pending instruction", "synthetic-model", [])],
                Turns = [new SavedChatTurn("Keep this question", "synthetic-model", "Keep this answer", "completed", [])] };
            AddChat(seed, true);
            var conversation = _currentChat;
            var count = _conversations.Count;
            conversation._chatProvider.SelectedIndex = DeskChatConnections.GrokBuild;
            var saved = _chatArchive!.Load().Single(chat => chat.Id == seed.Id);
            Check(_currentChat == conversation && _conversations.Count == count && saved.Provider == DeskChatConnections.GrokBuildStored && saved.Turns[0].Reply == "Keep this answer" && saved.Draft == "Keep this draft", "provider picker changes connection in the same chat with transcript and draft preserved");
            Check(conversation.SavedContextPendingForQa, "new provider will receive the saved transcript context");
            Check(saved.PendingInstructions?.Single().Text == "Keep this pending instruction", "provider change preserves pending instructions for explicit review");
            Check(conversation.PendingInstructionsPausedForQa, "provider change never automatically sends pending instructions");
            conversation.SetChatBusy(true);
            conversation._chatProvider.SelectedIndex = 0;
            Check(conversation._chatProvider.SelectedIndex == DeskChatConnections.GrokBuild && _currentChat == conversation && _conversations.Count == count,
                "provider change is refused during active work without replacing or clearing the chat");
            conversation.SetChatBusy(false);
            conversation.Save();
            SelectChat(_conversations.First(chat => chat != conversation)); SelectChat(conversation);
            Check(conversation.Updated == activity && _chatArchive.Load().Single(chat => chat.Id == seed.Id).Updated == activity, "saving and switching chats do not overwrite message time");
            SelectNav("home");
            Check(_chatDialogs.CurrentCount == 1, "fresh window has an available shared dialog lock");
            var create = _createProjectButton;
            UpdateChatRecents();
            Check(ReferenceEquals(create.Parent, _chatSidebar) && ProjectNodes(_chatSidebar).OfType<Button>().Count(button => button.Content as string == "Create project") == 1,
                "sidebar refresh retains the same Create project button and handler");
            InvokeProjectButton(create);
            var dialog = await WaitForProjectDialog("Create project");
            Check(dialog.XamlRoot == Content.XamlRoot, "sidebar Create project opens its native name dialog from Home on the window root");
            await CapturePng(Path.Combine(output, "create-project-dialog.png"));
            var editor = ((Panel)dialog.Content).Children.OfType<TextBox>().Single();
            editor.Text = "QA working project";
            InvokeProjectButton(ProjectNodes(dialog).OfType<Button>().Single(button => button.Content as string == "Choose folder…"));
            await ProjectWait(() => ((Panel)dialog.Content).Children.OfType<TextBlock>().Any(label => label.Text == folder), "Folder selection did not appear.");
            InvokeProjectButton(ProjectDialogButton(dialog, "PrimaryButton", "Create"));
            await ProjectWait(() => _projects.Any(project => project.Name == "QA working project") && _chatDialogs.CurrentCount == 1, "Create did not commit and release the dialog lock.");
            Check(new ChatProjectCatalog(_vm.ProfileDirectory).Load().Any(project => project.Name == "QA working project"), "native Create click commits a project to the profile catalog");
            saved = _chatArchive.Load().Single(chat => chat.Id == seed.Id);
            Check(_currentChat == conversation && _conversations.Count == count && saved.Folder == folder && saved.ProjectId == _projects[0].Id && saved.Draft == "Keep this draft" && saved.Turns[0].Reply == "Keep this answer", "Create files this same chat in the selected workspace and keeps its transcript and draft");
            Check(saved.Updated == activity && saved.Permissions == CursorPermissionPolicy.AlwaysAsk, "folder change keeps message time and resets permission for the new root");
            await CapturePng(Path.Combine(output, "project-created.png"));
            InvokeProjectButton(create);
            dialog = await WaitForProjectDialog("Create project");
            ((Panel)dialog.Content).Children.OfType<TextBox>().Single().Text = "";
            InvokeProjectButton(ProjectDialogButton(dialog, "PrimaryButton", "Create"));
            await Task.Delay(150);
            Check(ProjectDialogs().Contains(dialog) && ((Panel)dialog.Content).Children.OfType<TextBlock>().Last().Text == "Enter a project name.",
                "blank input stays in the native dialog with a visible validation error");
            InvokeProjectButton(ProjectDialogButton(dialog, "CloseButton", "Cancel"));
            await ProjectWait(() => _chatDialogs.CurrentCount == 1 && !ProjectDialogs().Any(), "Cancel did not release the dialog lock.");
            Check(_projects.Count == 1 && new ChatProjectCatalog(_vm.ProfileDirectory).Load().Count == 1, "Cancel creates no project and releases the shared lock");
            _qaProjectFolderPicker = () => Task.FromResult<string?>(null);
            InvokeProjectButton(create); dialog = await WaitForProjectDialog("Create project");
            ((Panel)dialog.Content).Children.OfType<TextBox>().Single().Text = "No folder project";
            InvokeProjectButton(ProjectNodes(dialog).OfType<Button>().Single(button => button.Content as string == "Choose folder…"));
            await Task.Delay(150);
            InvokeProjectButton(ProjectDialogButton(dialog, "PrimaryButton", "Create"));
            await Task.Delay(150);
            Check(_projects.Count == 1 && ((Panel)dialog.Content).Children.OfType<TextBlock>().Last().Text == "Choose a folder for this project.", "cancelled folder picker does not create a group or modify the conversation");
            InvokeProjectButton(ProjectDialogButton(dialog, "CloseButton", "Cancel"));
            await ProjectWait(() => _chatDialogs.CurrentCount == 1 && !ProjectDialogs().Any(), "Cancelled folder dialog did not close.");
            await _chatDialogs.WaitAsync();
            try
            {
                await CreateProjectAsync().WaitAsync(TimeSpan.FromSeconds(1));
                Check(_projectActionStatus.Visibility == Visibility.Visible && _projectActionStatus.Text.Contains("Another dialog is open") && _chatDialogs.CurrentCount == 0,
                    "busy dialog lock returns immediately with visible sidebar feedback without taking or releasing another dialog's lock");
                await CapturePng(Path.Combine(output, "project-dialog-busy.png"));
            }
            finally { _chatDialogs.Release(); }
            var other = new ContentDialog { XamlRoot = Content.XamlRoot, Title = "QA competing dialog", Content = "Only a disposable QA dialog.", CloseButtonText = "Close" };
            var showing = other.ShowAsync().AsTask();
            await WaitForProjectDialog("QA competing dialog");
            await CreateProjectAsync().WaitAsync(TimeSpan.FromSeconds(1));
            Check(_projectActionStatus.Text.StartsWith("Could not open project dialog:") && _chatDialogs.CurrentCount == 1,
                "real WinUI competing-dialog error is caught and reported while releasing the shared lock");
            other.Hide(); await showing;
            await CapturePng(Path.Combine(output, "project-dialog-error.png"));
            InvokeProjectButton(create);
            dialog = await WaitForProjectDialog("Create project");
            InvokeProjectButton(ProjectDialogButton(dialog, "CloseButton", "Cancel"));
            await ProjectWait(() => _chatDialogs.CurrentCount == 1 && !ProjectDialogs().Any(), "A later click did not recover after the dialog error.");
            Check(_projectActionStatus.Visibility == Visibility.Collapsed, "later native Create click recovers after an error and clears the old notice");
            File.WriteAllText(Path.Combine(output, "receipt.json"), JsonSerializer.Serialize(new { passed = true, checks, main_accessed = false, provider_calls = 0, folder_picker_result = "QA folder supplied at picker boundary; OS picker UI not automated", profile = _vm.ProfileDirectory }));
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(output, "receipt.json"), JsonSerializer.Serialize(new { passed = false, checks, error = ex.ToString() }));
        }
        finally { Close(); }
    }

    private IEnumerable<ContentDialog> ProjectDialogs() => Content.XamlRoot is { } root
        ? VisualTreeHelper.GetOpenPopupsForXamlRoot(root).SelectMany(popup => ProjectNodes(popup.Child)).OfType<ContentDialog>() : [];
    private async Task<ContentDialog> WaitForProjectDialog(string title)
    {
        await ProjectWait(() => ProjectDialogs().Any(dialog => dialog.Title as string == title), "Project dialog did not open; lock count " + _chatDialogs.CurrentCount);
        return ProjectDialogs().Single(dialog => dialog.Title as string == title);
    }
    private static Button ProjectDialogButton(ContentDialog dialog, string name, string label) =>
        ProjectNodes(dialog).OfType<Button>().First(button => button.Name == name || button.Content as string == label);
    private static IEnumerable<DependencyObject> ProjectNodes(DependencyObject? root)
    {
        if (root is null) yield break;
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in ProjectNodes(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void InvokeProjectButton(Button button) =>
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static async Task ProjectWait(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(50);
        if (!condition()) throw new IOException(message);
    }
}

internal sealed partial class ChatConversation
{
    internal bool SavedContextPendingForQa => _needsContext;
    internal bool PendingInstructionsPausedForQa => !_steeringQueue.HasAutomaticPending;
}
