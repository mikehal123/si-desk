using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sovereign.Desktop.Client;
using Sovereign.Desktop.Core;
using Windows.ApplicationModel.DataTransfer;

namespace Sovereign.Desktop;

public sealed partial class MainWindow
{
    private readonly Grid _chatPage = new() { Visibility = Visibility.Collapsed };
    private Grid? _workspaceContent = null;
    private MenuFlyoutItem? _openProjectMenu;
    private MenuFlyoutItem? _closeProjectMenu;
    private readonly StackPanel _chatRecents = new() { Spacing = DeskTheme.Space4 };
    private readonly StackPanel _chatSidebar = new() { Spacing = DeskTheme.Space4 };
    private readonly Button _createProjectButton = new() { Content = "Create project", HorizontalAlignment = HorizontalAlignment.Left,
        Background = DeskTheme.Background, BorderThickness = new Thickness(0), Padding = new Thickness(DeskTheme.Space8, DeskTheme.Space4, DeskTheme.Space8, DeskTheme.Space4),
        CornerRadius = new CornerRadius(DeskTheme.Radius6), FontSize = DeskTheme.SizeCaption, Margin = new Thickness(DeskTheme.Space8, 0, DeskTheme.Space8, DeskTheme.Space4) };
    private readonly TextBlock _projectActionStatus = new() { TextWrapping = TextWrapping.Wrap, FontSize = DeskTheme.SizeCaption,
        Margin = new Thickness(DeskTheme.Space8, 0, DeskTheme.Space8, DeskTheme.Space4), Visibility = Visibility.Collapsed };
    private readonly List<ChatConversation> _conversations = [];
    private readonly List<SavedChatProject> _projects = [];
    private readonly SemaphoreSlim _chatDialogs = new(1);
    private ChatArchive? _chatArchive;
    private ChatProjectCatalog? _chatProjects;
    private ChatConversation _currentChat = null!;
    private bool _closingChats;
    private UIElement BuildChatRecents()
    {
        if (_chatSidebar.Children.Count == 0)
        {
            _chatSidebar.Children.Add(new TextBlock { Text = "Projects", FontSize = DeskTheme.SizeCaption, Margin = new Thickness(DeskTheme.Space8, DeskTheme.Space16, 0, DeskTheme.Space4), Opacity = 1 });
            AutomationName(_createProjectButton, "Create project");
            AutomationName(_projectActionStatus, "Project action status");
            _createProjectButton.Click += async (_, _) => await CreateProjectAsync();
            _chatSidebar.Children.Add(_createProjectButton);
            _chatSidebar.Children.Add(_projectActionStatus);
            _chatSidebar.Children.Add(_chatRecents);
        }
        return _chatSidebar;
    }
    private void BuildChatPage()
    {
        _chatArchive = new ChatArchive(_vm.ProfileDirectory);
        _chatProjects = new ChatProjectCatalog(_vm.ProfileDirectory);
        var projectWarnings = new List<string>();
        _projects.AddRange(_chatProjects.Load(projectWarnings.Add));
        var warnings = new List<string>();
        foreach (var saved in _chatArchive.Load(warnings.Add)) AddChat(saved, false);
        if (_conversations.Count == 0) AddChat(ChatArchive.New(), false);
        SelectChat(_conversations[0], false);
        if (warnings.Count > 0) _chatStatus.Text = $"{warnings.Count} saved conversation file(s) could not be loaded. Files are preserved in the profile’s chats folder.";
        if (projectWarnings.Count > 0)
        {
            var projectNote = $"{projectWarnings.Count} saved project(s) could not be loaded.";
            _chatStatus.Text = string.IsNullOrEmpty(_chatStatus.Text) ? projectNote : _chatStatus.Text + " " + projectNote;
        }
        Closed += (_, _) => { _closingChats = true; foreach (var chat in _conversations) chat.Dispose(); };
    }
    private void AddChat(SavedChat saved, bool show)
    {
        ChatConversation? chat = null;
        chat = new ChatConversation(this, _vm.ProfileDirectory, _chatArchive!, saved, _chatDialogs,
            (provider, folder) => NewChat(provider, folder), UpdateChatRecents, () => SelectChat(chat!));
        _conversations.Add(chat); _chatPage.Children.Add(chat.View);
        UpdateChatRecents(); if (show) SelectChat(chat);
    }
    private async Task DeleteChatAsync(ChatConversation chat)
    {
        await _chatDialogs.WaitAsync();
        try
        {
            if (_closingChats || !_conversations.Contains(chat) || chat._chatBusy) return;
            var result = await new ContentDialog {
                XamlRoot = ((FrameworkElement)Content).XamlRoot,
                Title = "Delete conversation?", PrimaryButtonText = "Delete", CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                Content = new TextBlock { Text = "Delete “" + chat.Title + "” and its saved messages and draft from this app? This cannot be undone. Project files and provider-side history are not deleted.", TextWrapping = TextWrapping.Wrap }
            }.ShowAsync();
            if (result != ContentDialogResult.Primary || _closingChats || chat._chatBusy) return;
            chat.Delete();
            _conversations.Remove(chat); _chatPage.Children.Remove(chat.View);
            if (_currentChat == chat)
            {
                if (_conversations.Count == 0) AddChat(ChatArchive.New(), false);
                SelectChat(MostRecentlyUpdatedChat());
            }
            UpdateChatRecents();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { _chatStatus.Text = "Could not delete conversation: " + ex.Message; }
        finally { _chatDialogs.Release(); }
    }
    private void NewChat(int provider = 0, string? folder = null)
    {
        if (_closingChats) return;
        _currentChat?.Save();
        AddChat(ChatArchive.New(DeskChatConnections.Stored(provider), folder), true); _currentChat?.Save();
    }
    private void SelectChat(ChatConversation chat, bool show = true)
    {
        _currentChat?.Save();
        foreach (var item in _conversations) item.View.Visibility = item == chat ? Visibility.Visible : Visibility.Collapsed;
        _currentChat = chat; UpdateChatRecents(); if (show) OpenChat();
    }
    private ChatConversation MostRecentlyUpdatedChat()
    {
        var selected = _conversations[0];
        for (var i = 1; i < _conversations.Count; i++)
            if (_conversations[i].Updated > selected.Updated) selected = _conversations[i];
        return selected;
    }
    private void UpdateChatRecents()
    {
        if (_closingChats || _chatProjects is null) return;
        var chats = _conversations.Select(chat => new ChatSidebarChat(chat.Id, chat.Title, chat.Updated, chat.ProjectId, chat.HasMessages)).ToArray();
        var projects = _projects.Select(project => new ChatSidebarProject(project.Id, project.Name, project.Created, project.Collapsed)).ToArray();
        var model = ChatSidebarLayout.Build(chats, projects, DateTimeOffset.Now, TimeZoneInfo.Local, CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek, CultureInfo.CurrentCulture);
        _chatRecents.Children.Clear();
        foreach (var section in model.Projects)
        {
            var projectId = section.Project.Id;
            var header = new Button { AllowDrop = true, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = DeskTheme.Background, BorderThickness = new Thickness(0), Padding = new Thickness(DeskTheme.Space8, DeskTheme.Space4, DeskTheme.Space8, DeskTheme.Space4), CornerRadius = new CornerRadius(DeskTheme.Radius6),
                Content = new TextBlock { Text = (section.Project.Collapsed ? "▸ " : "▾ ") + section.Project.Name, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = DeskTheme.SizeCaption, MaxWidth = 176 } };
            header.Click += (_, _) => ToggleProjectCollapsed(projectId);
            header.DragOver += (_, e) => AcceptChatDrag(e);
            header.Drop += (_, e) => DropChat(e, projectId);
            var menu = new MenuFlyout();
            var rename = new MenuFlyoutItem { Text = "Rename…" };
            rename.Click += async (_, _) => await RenameProjectAsync(projectId);
            var deleteProject = new MenuFlyoutItem { Text = "Delete project…" };
            deleteProject.Click += async (_, _) => await DeleteProjectAsync(projectId);
            menu.Items.Add(rename); menu.Items.Add(deleteProject);
            header.ContextFlyout = menu;
            _chatRecents.Children.Add(header);
            if (!section.Project.Collapsed)
                foreach (var row in section.Rows) AddChatSidebarRow(row);
        }
        _chatRecents.Children.Add(RecentsDropLabel("Recents", 12, .6, new Thickness(10, 16, 10, 4)));
        foreach (var group in model.Recents)
        {
            _chatRecents.Children.Add(RecentsDropLabel(group.Title, 11, .55, new Thickness(10, 8, 10, 2)));
            foreach (var row in group.Rows) AddChatSidebarRow(row);
        }
        if (_openProjectMenu is not null) _openProjectMenu.IsEnabled = _currentChat is not null && !_currentChat._chatBusy;
        if (_closeProjectMenu is not null) _closeProjectMenu.IsEnabled = _currentChat is not null && !_currentChat._chatBusy && _currentChat._projectFolder is not null;
    }
    private void AddChatSidebarRow(ChatSidebarRow row)
    {
        var chat = _conversations.FirstOrDefault(item => item.Id == row.Chat.Id);
        if (chat is null) return;
        var button = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = chat == _currentChat ? DeskTheme.Hover : DeskTheme.Background, BorderThickness = new Thickness(0),
            Padding = new Thickness(DeskTheme.Space8, DeskTheme.Space8, DeskTheme.Space8, DeskTheme.Space8), CornerRadius = new CornerRadius(DeskTheme.Radius6), CanDrag = true,
            Content = new StackPanel { Children = {
                new TextBlock { Text = chat.RecentLabel, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = DeskTheme.SizeCaption, MaxWidth = 152 },
                new TextBlock { Text = row.Timestamp, FontSize = DeskTheme.SizeCaption, Opacity = 1, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 152 } } } };
        button.Click += (_, _) => SelectChat(chat);
        button.DragStarting += (_, e) => StartChatDrag(e, chat.Id);
        AutomationName(button, (chat.ProjectId is null ? "Recent chat: " : "Project chat: ") + chat.Title + (chat._chatBusy ? " · Working" : ""));
        ToolTipService.SetToolTip(button, chat.Title + (chat._chatBusy ? " — working in background" : "") + "\n" +
            (chat.HasMessages ? "Last message: " + chat.Updated.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "No messages yet") +
            (chat._projectFolder is null ? "" : "\n" + chat._projectFolder));
        var menu = new MenuFlyout();
        var delete = new MenuFlyoutItem { Text = "Delete conversation…", Icon = new SymbolIcon(Symbol.Delete) };
        delete.Click += async (_, _) => await DeleteChatAsync(chat);
        menu.Items.Add(delete);
        foreach (var project in _projects)
        {
            if (chat.ProjectId == project.Id) continue;
            var targetId = project.Id;
            var move = new MenuFlyoutItem { Text = "Move to " + project.Name };
            move.Click += (_, _) => MoveChatToProject(chat, targetId);
            menu.Items.Add(move);
        }
        if (chat.ProjectId is not null)
        {
            var remove = new MenuFlyoutItem { Text = "Remove from project" };
            remove.Click += (_, _) => MoveChatToProject(chat, null);
            menu.Items.Add(remove);
        }
        menu.Opening += (_, _) => delete.IsEnabled = !chat._chatBusy;
        var more = new Button { Content = "⋯", Width = 28, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center,
            Background = DeskTheme.Background, BorderThickness = new Thickness(0), Flyout = menu };
        AutomationName(more, "Conversation options: " + chat.Title);
        ToolTipService.SetToolTip(more, "Conversation options · stop a working chat before deleting");
        var line = new Grid { CanDrag = true };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.DragStarting += (_, e) => StartChatDrag(e, chat.Id);
        Grid.SetColumn(more, 1); line.Children.Add(button); line.Children.Add(more);
        _chatRecents.Children.Add(line);
    }
    private static void StartChatDrag(DragStartingEventArgs e, string chatId)
    {
        e.Data.SetText(chatId);
        e.AllowedOperations = DataPackageOperation.Move;
    }
    private static void AcceptChatDrag(DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Move;
        e.Handled = true;
    }
    private async void DropChat(DragEventArgs e, string? projectId)
    {
        var deferral = e.GetDeferral();
        try
        {
            e.Handled = true;
            if (_closingChats || !e.DataView.Contains(StandardDataFormats.Text)) return;
            var id = await e.DataView.GetTextAsync();
            if (_closingChats || string.IsNullOrEmpty(id)) return;
            var chat = _conversations.FirstOrDefault(item => item.Id == id);
            if (chat is null) return;
            MoveChatToProject(chat, projectId);
        }
        catch (Exception ex)
        { if (_currentChat is not null) _chatStatus.Text = "Could not move conversation: " + ex.Message; }
        finally { deferral.Complete(); }
    }
    private void MoveChatToProject(ChatConversation chat, string? projectId)
    {
        try
        {
            var project = _projects.FirstOrDefault(project => project.Id == projectId);
            if (project?.Folder is { } folder) chat.AssignProjectWorkspace(projectId, folder);
            else chat.AssignProject(projectId);
            UpdateChatRecents();
        }
        catch (Exception ex)
        { if (_currentChat is not null) _chatStatus.Text = "Could not move conversation: " + ex.Message; }
    }
    private void ToggleProjectCollapsed(string projectId)
    {
        if (_closingChats) return;
        var index = _projects.FindIndex(project => project.Id == projectId);
        if (index < 0) return;
        var previous = _projects[index];
        _projects[index] = previous with { Collapsed = !previous.Collapsed };
        if (!TrySaveChatProjects(out _)) _projects[index] = previous;
        UpdateChatRecents();
    }
    private async Task CreateProjectAsync()
    {
        if (_closingChats) return;
        var chat = _currentChat;
        if (chat.IsWorking) { SetProjectActionStatus("Finish or stop this response before attaching a project folder."); return; }
        var accepted = await ShowProjectNameDialogAsync("Create project", "Create", "", (name, folder) =>
        {
            if (!ChatProjectCatalog.TryCreate(name, _projects, DateTimeOffset.Now, out var project, out var error) || project is null)
                return (false, error);
            if (folder is null) return (false, "Choose a folder for this project.");
            if (!Directory.Exists(folder)) return (false, "The selected folder is unavailable. Choose another folder.");
            if (chat.IsWorking) return (false, "Finish or stop this response before attaching the folder.");
            project = project with { Folder = Path.GetFullPath(folder) };
            _projects.Add(project);
            if (!TrySaveChatProjects(out var saveError)) { _projects.Remove(project); return (false, "Could not save projects: " + saveError); }
            try { chat.AssignProjectWorkspace(project.Id, project.Folder); }
            catch (Exception ex)
            {
                _projects.Remove(project);
                if (!TrySaveChatProjects(out var rollback)) { _projects.Add(project); return (false, "Could not attach the chat; the empty project was kept. " + rollback); }
                return (false, "Could not attach the chat: " + CommandPresentation.Redact(ex.Message));
            }
            return (true, (string?)null);
        }, chooseFolder: true);
        if (accepted) { SelectChat(chat); _chatInput.Focus(FocusState.Programmatic); }
    }
    private async Task RenameProjectAsync(string projectId)
    {
        if (_closingChats) return;
        var index = _projects.FindIndex(project => project.Id == projectId);
        if (index < 0) return;
        var current = _projects[index];
        var accepted = await ShowProjectNameDialogAsync("Rename project", "Rename", current.Name, (name, _) =>
        {
            if (!ChatProjectCatalog.TryRename(current, name, _projects, out var renamed, out var error) || renamed is null)
                return (false, error);
            var latest = _projects.FindIndex(project => project.Id == projectId);
            if (latest < 0) return (false, "That project is no longer available.");
            var before = _projects[latest];
            _projects[latest] = renamed;
            if (!TrySaveChatProjects(out var saveError)) { _projects[latest] = before; return (false, "Could not save projects: " + saveError); }
            return (true, (string?)null);
        });
        if (accepted) UpdateChatRecents();
    }
    private async Task<bool> ShowProjectNameDialogAsync(string title, string primary, string initial, Func<string, string?, (bool Ok, string? Error)> commit, bool chooseFolder = false)
    {
        if (_closingChats) return false;
        if (!await _chatDialogs.WaitAsync(0))
        {
            SetProjectActionStatus("Another dialog is open. Close it and try again.");
            return false;
        }
        try
        {
            if (_closingChats) return false;
            if (Content.XamlRoot is not { } root)
            {
                SetProjectActionStatus("The project dialog is not ready. Reopen Chat and try again.");
                return false;
            }
            var name = new TextBox { PlaceholderText = "Project name", Text = initial };
            var errorText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = DeskTheme.SizeCaption };
            string? selectedFolder = null;
            var panel = new StackPanel { Spacing = DeskTheme.Space8, MinWidth = 320, Children = { name } };
            if (chooseFolder)
            {
                var folderLabel = new TextBlock { Text = "Choose the folder your agent will work in.", TextWrapping = TextWrapping.Wrap, MaxWidth = 440 };
                var folderButton = new Button { Content = "Choose folder…" };
                AutomationName(folderButton, "Choose project folder");
                folderButton.Click += async (_, _) =>
                {
                    folderButton.IsEnabled = false;
                    try
                    {
                        var selected = await PickChatProjectFolderAsync();
                        if (selected is not null) { selectedFolder = Path.GetFullPath(selected); folderLabel.Text = selectedFolder; errorText.Text = ""; }
                    }
                    catch (Exception ex) { errorText.Text = "Could not choose folder: " + CommandPresentation.Redact(ex.Message); }
                    finally { folderButton.IsEnabled = true; }
                };
                panel.Children.Add(folderLabel); panel.Children.Add(folderButton);
            }
            panel.Children.Add(errorText);
            var dialog = new ContentDialog
            {
                XamlRoot = root, Title = title, PrimaryButtonText = primary, CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary, Content = panel
            };
            dialog.Opened += (_, _) => { SetProjectActionStatus(""); name.Focus(FocusState.Programmatic); };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                try
                {
                    var (ok, error) = commit(name.Text, selectedFolder);
                    if (ok) return;
                    args.Cancel = true;
                    errorText.Text = error ?? "";
                }
                catch (Exception ex)
                {
                    args.Cancel = true;
                    errorText.Text = "Could not save project: " + CommandPresentation.Redact(ex.Message);
                    SetProjectActionStatus(errorText.Text);
                }
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            SetProjectActionStatus("Could not open project dialog: " + CommandPresentation.Redact(ex.Message));
            return false;
        }
        finally { _chatDialogs.Release(); }
    }
    internal async Task<string?> PickChatProjectFolderAsync()
    {
        if (_qaProjectFolderPicker is not null)
        {
            ValidateCodexQaProfile(_vm.ProfileDirectory);
            if (Environment.GetEnvironmentVariable("SOVEREIGN_QA_PROJECT_DIALOG") != "1") throw new IOException("QA folder selection requires the guarded project dialog test.");
            return await _qaProjectFolderPicker();
        }
        var picker = new Windows.Storage.Pickers.FolderPicker(); picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }
    private void SetProjectActionStatus(string text)
    {
        if (_closingChats) return;
        _projectActionStatus.Text = text.Length > 600 ? text[..600] : text;
        _projectActionStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }
    private async Task DeleteProjectAsync(string projectId)
    {
        if (_closingChats) return;
        await _chatDialogs.WaitAsync();
        try
        {
            if (_closingChats) return;
            var project = _projects.FirstOrDefault(item => item.Id == projectId);
            if (project is null) return;
            var result = await new ContentDialog
            {
                XamlRoot = ((FrameworkElement)Content).XamlRoot, Title = "Delete project?", PrimaryButtonText = "Delete", CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                Content = new TextBlock { Text = "Remove “" + project.Name + "”? Chats return to Recents. Saved messages are not deleted.", TextWrapping = TextWrapping.Wrap }
            }.ShowAsync();
            if (result != ContentDialogResult.Primary || _closingChats) return;
            foreach (var chat in _conversations)
                if (chat.ProjectId == projectId) chat.AssignProject(null);
            var removed = _projects.Where(item => item.Id == projectId).ToArray();
            _projects.RemoveAll(item => item.Id == projectId);
            if (!TrySaveChatProjects(out _)) _projects.AddRange(removed);
            UpdateChatRecents();
        }
        finally { _chatDialogs.Release(); }
    }
    private Border RecentsDropLabel(string text, double fontSize, double opacity, Thickness padding)
    {
        var label = new Border { AllowDrop = true, Background = DeskTheme.Background, Padding = padding,
            Child = new TextBlock { Text = text, FontSize = fontSize, Opacity = opacity } };
        label.DragOver += (_, e) => AcceptChatDrag(e);
        label.Drop += (_, e) => DropChat(e, null);
        return label;
    }
    private bool TrySaveChatProjects(out string? error)
    {
        error = null;
        if (_chatProjects is null) { error = "Projects are not available yet."; return false; }
        try { _chatProjects.Save(_projects); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            error = ex.Message;
            if (_currentChat is not null) _chatStatus.Text = "Could not save projects: " + ex.Message;
            return false;
        }
    }
    private void OpenChat()
    {
        CloseRoomView(); CloseSettings(); CloseGrokbot();
        if (_workspaceContent is not null) _workspaceContent.Visibility = Visibility.Collapsed;
        _chatPage.Visibility = Visibility.Visible; MarkSystem("chat");
    }
    private void CloseChatView()
    {
        _chatPage.Visibility = Visibility.Collapsed;
        if (_workspaceContent is not null) _workspaceContent.Visibility = Visibility.Visible;
    }
    private async Task OpenProjectFolderAsync() { OpenChat(); await _currentChat.OpenProjectFolderAsync(); UpdateChatRecents(); }
    private void CloseProjectFolder() { _currentChat.CloseProjectFolder(); UpdateChatRecents(); }
    // Other workflows use the selected chat's provider configuration. The chat owns all private conversation state.
    private ComboBox _chatProvider => _currentChat._chatProvider;
    private ComboBox _chatModel => _currentChat._chatModel;
    private TextBox _chatInput => _currentChat._chatInput;
    private TextBlock _chatStatus => _currentChat._chatStatus;
    private StackPanel _chatMessages => _currentChat._chatMessages;
    private List<ChatMessage> _ollamaHistory => _currentChat._ollamaHistory;
    private OllamaChatClient _ollamaChat => _currentChat._ollamaChat;
    private OllamaChatOptions? _ollamaQaOptions { get => _currentChat._ollamaQaOptions; set => _currentChat._ollamaQaOptions = value; }
    private OllamaChatReceipt? _lastOllamaReceipt => _currentChat._lastOllamaReceipt;
    private CodingPlanChatClient? _planChat => _currentChat._planChat;
    private CancellationTokenSource? _chatCancellation => _currentChat._chatCancellation;
    private bool _chatReady => _currentChat._chatReady;
    private bool LocalChat => _currentChat.LocalChat;
    private bool PlanChat => _currentChat.PlanChat;
    private bool CursorChat => _currentChat.CursorChat;
    private bool XaiApiChat => _currentChat.XaiApiChat;
    private CursorChatClient? _cursorChat => _currentChat._cursorChat;
    private GrokBuildChatClient? _grokBuild => _currentChat._grokBuild;
    private bool GrokBuildChat => _currentChat.GrokBuildChat;
    private XaiApiChatClient? _xaiChat => _currentChat._xaiChat;
    private string PlanProvider => _currentChat.PlanProvider;
    private string? SelectedChatModel => _currentChat.SelectedChatModel;
    private bool HasChatModel(string id) => _currentChat.HasChatModel(id);
    private bool SelectChatModel(string id) => _currentChat.SelectChatModel(id);
    private void ResetChat() { var qa = _ollamaQaOptions; NewChat(_chatProvider.SelectedIndex, _currentChat._projectFolder); _ollamaQaOptions = qa; }
    private Task ConnectChatAsync() => _currentChat.ConnectChatAsync();
    private Task SendChatAsync() => _currentChat.SendChatAsync();
    private Task<bool> StartChatSignInAsync() => _currentChat.StartChatSignInAsync();
    private void SetChatBusy(bool busy) => _currentChat.SetChatBusy(busy);

    public static void ValidateCodexQaProfile(string profile)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(profile));
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), XaiDeskIdentity.DataFolderName);
        if (!directory.Name.Equals("profile", StringComparison.OrdinalIgnoreCase)
            || directory.Parent is not { } instance
            || !instance.Name.StartsWith("qa-", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(instance.Parent?.FullName, root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Codex chat verification requires an explicit xAI Desk qa-* instance profile. Main and default profiles are refused.");
    }
}
