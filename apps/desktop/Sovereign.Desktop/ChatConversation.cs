using System.Collections.Concurrent;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sovereign.Desktop.Client;
using Sovereign.Desktop.Phone;
using static Sovereign.Desktop.MainWindow;

namespace Sovereign.Desktop;

internal sealed partial class ChatConversation : IDisposable
{
    private readonly MainWindow _owner;
    private readonly string _profileDirectory;
    private readonly ChatArchive _archive;
    private readonly SemaphoreSlim _dialogs;
    private readonly Action<int, string?> _newChat;
    private readonly Action _changed;
    private readonly Action _show;
    private SavedChat _saved;
    private bool _restoring = true;
    private bool _needsContext;
    private bool _disposed;
    private bool _deleted;
    private int _providerBeforeChange;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly ConcurrentQueue<string> _partialReply = new();
    private readonly ConcurrentQueue<CursorActivityEvent> _partialActivity = new();
    private readonly System.Text.StringBuilder _savedReply = new();
    private readonly List<CursorActivityEvent> _savedActivity = [];
    private bool _savePending;
    private int _savedTurnIndex = -1;
    public bool IsWorking => _chatBusy || _attachmentBusy || _cursorSendLoop || _codexSendLoop || _acceptingCursorInstruction;
    public string Id => _saved.Id;
    public string Title => _saved.Title;
    public string? ProjectId => _saved.ProjectId;
    public DateTimeOffset Updated => _saved.Updated;
    public bool HasMessages => _saved.Turns.Length > 0;
    public string LastReply => _saved.Turns.LastOrDefault()?.Reply ?? "";
    public string RecentLabel => (_chatBusy ? "◌ " : "") + Title;
    internal string PhoneProviderLabel => CursorChat ? "Cursor" : XaiApiChat ? "xAI API" : "Grok Build";
    internal string? PhoneModel => _saved.Model ?? SelectedChatModel;

    internal PhoneChatDetail PhoneSnapshot()
    {
        var live = _savedReply + string.Concat(_partialReply);
        var phase = _chatStatus.Text;
        if (_partialActivity.Any(item => item.Kind == "permission") || _subagentRows.Values.Any(item => item.CurrentWork.Contains("Waiting for your approval", StringComparison.Ordinal)))
            phase = "Waiting for your approval";
        var turns = _saved.Turns.Select((turn, index) =>
        {
            var reply = index == _saved.Turns.Length - 1 && turn.Outcome == "running" && live.Length > 0 ? live : turn.Reply;
            return new PhoneChatTurn(PhoneClip(turn.Prompt), PhoneClip(reply), turn.Outcome, turn.Model);
        }).ToArray();
        var agents = _subagentRows.Values.Select(item => new PhoneSubagentStatus(item.Name, item.State, PhoneClip(item.CurrentWork))).ToArray();
        return new PhoneChatDetail(Id, Title, PhoneProviderLabel, PhoneModel, PhoneClip(_chatStatus.Text), IsWorking, ProjectId, PhoneClip(live), PhoneClip(phase), turns, agents);
    }

    private static string PhoneClip(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length <= 20000 ? value : value[..20000] + "…";
    }
    public Grid View => _chatPage;

    public ChatConversation(MainWindow owner, string profile, ChatArchive archive, SavedChat saved,
        SemaphoreSlim dialogs, Action<int, string?> newChat, Action changed, Action show)
    {
        _owner = owner; _profileDirectory = profile; _archive = archive; _saved = saved;
        _lineReviewOn = LineReviewPreference.Load(profile);
        _classicChat = ClassicChatPreference.Load(profile);
        _steeringQueue = new(saved.PendingInstructions);
        _dialogs = dialogs; _newChat = newChat; _changed = changed; _show = show;
        _projectFolder = saved.Folder;
        _draftFiles.AddRange(saved.DraftAttachments ?? []);
        BuildChatPage();
        RenderDraftFiles();
        _chatProvider.SelectedIndex = DeskChatConnections.Combo(saved.Provider); _providerBeforeChange = _chatProvider.SelectedIndex;
        ResetChat();
        var renderedInstructions = new HashSet<string>();
        foreach (var message in saved.Turns)
        {
            if (message.Instructions is { Length: > 0 })
            {
                foreach (var instruction in message.Instructions)
                {
                    if (!renderedInstructions.Add(instruction.Id)) continue;
                    _chatMessages.Children.Add(UserMessage(instruction.Text));
                    if (instruction.Attachments.Length > 0) _chatMessages.Children.Add(AttachmentHistory(instruction.Attachments));
                }
            }
            else
            {
                _chatMessages.Children.Add(UserMessage(message.Prompt, message.Author));
                if (message.Attachments is { Length: > 0 }) _chatMessages.Children.Add(AttachmentHistory(message.Attachments));
            }
            var view = new ChatActivityView(message.Model, UpdateChangesPanel, () => { }, _ => { }, PauseFollow, Path.Combine(_profileDirectory, "chat-activity"), _classicChat);
            foreach (var activity in message.Activity) view.Observe(activity);
            view.RestoreProgress(message.ProgressUpdates ?? []); view.ShowFailure(message.FailureReason);
            view.Append(message.DisplayReply ?? message.Reply); view.Finish(message.Outcome, restored: true); TrackChatTurn(view); _chatMessages.Children.Add(view.Root);
            _ollamaHistory.Add(new("user", message.SentText ?? message.Prompt));
            if (message.Outcome == "completed") _ollamaHistory.Add(new("assistant", message.Reply));
        }
        _chatInput.Text = saved.Draft;
        var representedInstructions = saved.Turns.SelectMany(t => t.Instructions ?? []).Select(i => i.Id).ToHashSet();
        foreach (var instruction in _steeringQueue.Snapshot) ShowPendingInstruction(instruction, representedInstructions.Contains(instruction.Id));
        if (saved.Turns.Length > 0) _chatStatus.Text = "Saved conversation · connect to continue with saved text context. Chat preferences are restored; previous tool sessions are not.";
        if (saved.Provider is DeskChatConnections.CursorStored or DeskChatConnections.GrokBuildStored && saved.Model is not null && saved.Turns.LastOrDefault()?.Outcome is "stopped" or "failed")
        {
            _reconnectOnSend = true;
            SetChatBusy(false);
            _chatStatus.Text = "Previous response stopped. Edit your draft and send to reconnect with saved conversation context.";
        }
        if (_steeringQueue.Count > 0) _chatStatus.Text = "Saved instructions need review. Interrupted instructions may already have been sent; queued instructions were not sent. Use their controls below. Restart never sends automatically.";
        ApplyTeamWarning();
        _restoring = false;
        _chatPage.Loaded += (_, _) => RefreshTeamBoard();
        _chatInput.TextChanged += (_, _) => { _savePending = true; RefreshComposerAction(); };
        AttachComposerCancelsHandoffWatch();
        _chatModel.SelectionChanged += (_, _) => { if (!_restoring && _chatReady) _savePending = true; RefreshAttachmentStatus(); DescribeSelectedModel(); };
        _chatModel.RegisterPropertyChangedCallback(ComboBox.TextProperty, (_, _) => RefreshAttachmentStatus());
        _saveTimer.Tick += (_, _) => { DrainSavedTurn(); RefreshSubagentElapsed(); if (_savePending) Save(); };
        _saveTimer.Start();
        DrainGrokbotReviewQueue();
    }
    internal bool ClassicChat => _classicChat;
    private static Border UserBubble(string text) => new() { Padding = new Thickness(DeskTheme.Space12, DeskTheme.Space8, DeskTheme.Space12, DeskTheme.Space8), Margin = new Thickness(0, DeskTheme.Space8, 0, DeskTheme.Space4), CornerRadius = new CornerRadius(DeskTheme.Radius6),
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 46, 46, 50)),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Child = new TextBlock { Text = "› " + text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = DeskTheme.SizeBody } };
    internal void AcceptGrokBotMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 8000 || text.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t'))
            throw new ArgumentException("Grok Bot message is empty, too long, or has unsupported characters.");
        var clean = text.Trim();
        var turn = new SavedChatTurn(clean, "Grok Bot", "", "completed", [], Author: "Grok Bot");
        _saved = _saved with { Turns = [.. _saved.Turns, turn], Updated = DateTimeOffset.UtcNow };
        Save(); _changed();
        _chatMessages.Children.Add(UserMessage(clean, "Grok Bot"));
    }

    internal IReadOnlyList<CursorSubagent> ConnectorSubagents() => _subagentRows.Values.ToArray();

    internal (string Author, string Text, string? Model)[] ConnectorTranscript()
    {
        var lines = new List<(string Author, string Text, string? Model)>();
        foreach (var turn in _saved.Turns)
        {
            lines.Add((turn.Author ?? "You", turn.Prompt, turn.Author == "Grok Bot" ? "Grok Bot" : null));
            if (!string.IsNullOrWhiteSpace(turn.Reply)) lines.Add(("Grok", turn.Reply, turn.Model));
        }
        return lines.ToArray();
    }

    private UIElement UserMessage(string text, string? author = null)
    {
        if (author == "Grok Bot") text = "Grok Bot · " + text;
        if (_classicChat) return UserBubble(text);
        var bar = GrokTuiPresentation.UserBar(text, DateTimeOffset.Now);
        var grid = new Grid { ColumnSpacing = DeskTheme.Space12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var body = new TextBlock { Text = bar.Text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = DeskTheme.SizeBody, FontFamily = DeskTheme.MonoFont };
        var time = new TextBlock { Text = bar.Time, Opacity = 1, FontSize = DeskTheme.SizeCaption, VerticalAlignment = VerticalAlignment.Center, FontFamily = DeskTheme.MonoFont };
        Grid.SetColumn(time, 1);
        grid.Children.Add(body);
        grid.Children.Add(time);
        return new Border
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 36, 36, 42)),
            Padding = new Thickness(DeskTheme.Space8, DeskTheme.Space4, DeskTheme.Space12, DeskTheme.Space4),
            Margin = new Thickness(0, DeskTheme.Space12, 0, DeskTheme.Space4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = grid
        };
    }
    private void PauseFollow() { _chatFollowLatest = false; if (_chatLatest is not null) _chatLatest.Visibility = Visibility.Visible; SyncReadingControls(); }
    private void SyncReadingControls()
    {
        if (_readingControls is null) return;
        var show = _chatClearReasoning?.Visibility == Visibility.Visible || _chatLatest?.Visibility == Visibility.Visible;
        _readingControls.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }
    private void TrackChatTurn(ChatActivityView turn)
    {
        _chatTurns.Add(turn);
        turn.BindLineReview(() => OpenLineReview(turn, "main", _chatTurns.IndexOf(turn), null), () => _lineReviewOn);
        turn.ReasoningDisplayChanged += RefreshClearReasoning;
        RefreshClearReasoning();
    }
    private void RefreshClearReasoning()
    {
        if (_chatClearReasoning is null) return;
        var hasReasoning = (_activeChatTurn ?? _chatTurns.LastOrDefault())?.HasDisplayedReasoning == true;
        _chatClearReasoning.IsEnabled = hasReasoning;
        _chatClearReasoning.Visibility = hasReasoning ? Visibility.Visible : Visibility.Collapsed;
        SyncReadingControls();
    }
    private void ClearChatReasoningDisplay()
    {
        (_activeChatTurn ?? _chatTurns.LastOrDefault())?.ClearDisplayedReasoning();
        RefreshClearReasoning();
    }
    private void ProviderChanged()
    {
        if (_restoring) return;
        var selected = _chatProvider.SelectedIndex;
        if (IsWorking)
        {
            _restoring = true; _chatProvider.SelectedIndex = _providerBeforeChange; _restoring = false;
            _chatStatus.Text = "Finish or stop the current response before changing provider.";
            return;
        }
        _connectionFlyout?.Hide();
        _steeringQueue.Pause();
        _providerBeforeChange = selected;
            _saved = _saved with { Provider = DeskChatConnections.Stored(selected), Connection = DeskChatConnections.Key(selected), Model = null };
        ResetChat(); Save(); _changed();
        if (_saved.Turns.Length > 0 || _steeringQueue.Count > 0)
            _chatStatus.Text = "Provider changed in this conversation. Connect and choose a model to continue with the saved messages. Pending instructions remain for review.";
    }
    private void RestoreModelSelection()
    {
        if (_saved.Model is null) return;
        foreach (var item in _chatModel.Items)
            if (ReadPicker(item).Id == _saved.Model) { _chatModel.SelectedItem = item; DescribeSelectedModel(); return; }
        if (PlanChat) _chatModel.Text = _saved.Model;
    }
    private void BeginSavedTurn(string prompt, string model, ChatAttachment[]? attachments = null, string? sentText = null, SavedChatInstruction[]? instructions = null)
    {
        BeginSubagentTurn();
        _savedReply.Clear(); _savedActivity.Clear();
        while (_partialReply.TryDequeue(out _)) { }
        while (_partialActivity.TryDequeue(out _)) { }
        _savedTurnIndex = _saved.Turns.Length;
        _saved = _saved with { Updated = DateTimeOffset.UtcNow, Title = _saved.Turns.Length == 0 ? ChatArchive.TitleFor(prompt) : _saved.Title,
            Turns = [.. _saved.Turns, new(prompt, model, "", "running", [], attachments, sentText, Instructions: instructions)] };
        Save(); _changed();
    }
    private void DrainSavedTurn()
    {
        var dirty = false;
        while (_partialReply.TryDequeue(out var text)) { _savedReply.Append(text); dirty = true; }
        while (_partialActivity.TryDequeue(out var activity))
        {
            var savedActivity = activity;
            if (activity.Subagent is { } child)
            {
                _savedActivity.RemoveAll(a => a.Subagent?.SessionId == child.SessionId);
                if (activity.Tool is { } childTool)
                {
                    // Keep the final edit in order, without retaining a large copy for every progress chunk.
                    _savedActivity.RemoveAll(a => a.Kind == "tool" && a.Tool is { } previous && previous.Id == childTool.Id
                        && previous.Status is "pending" or "in_progress" or "running");
                    _savedActivity.Add(new("tool", Tool: childTool with { Title = child.Name + " · " + childTool.Title }));
                }
                ObserveSubagent(activity);
                // Child.Tool and Child.Tools already contain bounded previews for the sidebar.
                savedActivity = activity with { Tool = null };
            }
            _savedActivity.Add(savedActivity); dirty = true;
        }
        if (!dirty || _savedTurnIndex < 0 || _savedTurnIndex >= _saved.Turns.Length) return;
        var retained = CursorActivityRetention.Compact(_savedActivity, artifactDirectory: SavedTurnActivityDirectory);
        _savedActivity.Clear(); _savedActivity.AddRange(retained);
        _saved.Turns[_savedTurnIndex] = _saved.Turns[_savedTurnIndex] with { Reply = _savedReply.ToString(), Activity = _savedActivity.ToArray() };
        _savePending = true;
    }
    private string SavedTurnActivityDirectory => Path.Combine(_profileDirectory, "chat-activity", _saved.Id,
        _savedTurnIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
    private void FinishSavedTurn(string reply, string outcome, ChatActivityView turn, string? failureReason = null)
    {
        DrainSavedTurn();
        FinishSubagents(outcome);
        if (_savedTurnIndex < 0 || _savedTurnIndex >= _saved.Turns.Length) return;
        _saved.Turns[_savedTurnIndex] = _saved.Turns[_savedTurnIndex] with { Reply = reply, Outcome = outcome, FailureReason = failureReason, ProgressUpdates = turn.ProgressUpdates, DisplayReply = turn.DisplayReply,
            Activity = CursorActivityRetention.Compact(_savedActivity.Select(a => a.Subagent is { } child && _subagentRows.TryGetValue(child.SessionId, out var finalChild)
                ? a with { Subagent = finalChild } : a), outcome, SavedTurnActivityDirectory).ToArray() };
        RecordTeamHandoff(reply);
        _saved = _saved with { Updated = DateTimeOffset.UtcNow };
        _savedTurnIndex = -1; Save(); _changed();
    }
    public void Save()
    {
        if (_restoring || _deleted) return;
        try
        {
            _saved = _saved with { Provider = DeskChatConnections.Stored(_chatProvider.SelectedIndex), Connection = DeskChatConnections.Key(_chatProvider.SelectedIndex), Folder = _projectFolder,
                Model = _chatReady ? SelectedChatModel : _saved.Model, Draft = _chatInput.Text, DraftAttachments = _draftFiles.ToArray(),
                PendingInstructions = _steeringQueue.Count > 0 ? _steeringQueue.Snapshot : null };
            _archive.Save(_saved); _teamBook?.Save();
            if (_teamBook?.Warning is { } teamWarning && !string.Equals(_teamWarning, teamWarning, StringComparison.Ordinal))
            {
                _teamWarning = teamWarning;
                ApplyTeamWarning();
            }
            _savePending = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _chatStatus.Text = "Could not save this conversation locally: " + ex.Message; }
    }
    public void AssignProject(string? projectId)
    {
        if (_restoring || _deleted) return;
        if (projectId is not null && !Guid.TryParseExact(projectId, "N", out _))
            throw new ArgumentException("Invalid project identity.", nameof(projectId));
        var updated = _saved.Updated;
        var previous = _saved;
        try
        {
            _saved = _saved with { Provider = DeskChatConnections.Stored(_chatProvider.SelectedIndex), Connection = DeskChatConnections.Key(_chatProvider.SelectedIndex), Folder = _projectFolder,
                Model = _chatReady ? SelectedChatModel : _saved.Model, Draft = _chatInput.Text, DraftAttachments = _draftFiles.ToArray(),
                PendingInstructions = _steeringQueue.Count > 0 ? _steeringQueue.Snapshot : null, ProjectId = projectId, Updated = updated };
            _archive.Save(_saved);
            // Save() would stamp UtcNow. Leaving the timer pending would do the same on the next tick.
            _savePending = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _saved = previous;
            _chatStatus.Text = "Could not save this conversation locally: " + ex.Message;
        }
    }
    internal void AssignProjectWorkspace(string? projectId, string? folder)
    {
        if (IsWorking) throw new InvalidOperationException("Finish or stop this response before changing the project folder.");
        if (projectId is not null && !Guid.TryParseExact(projectId, "N", out _)) throw new ArgumentException("Invalid project identity.");
        var full = folder is null ? null : Path.GetFullPath(folder);
        if (full is not null && !Directory.Exists(full)) throw new DirectoryNotFoundException("The selected project folder is unavailable.");
        var changedFolder = !string.Equals(_projectFolder, full, StringComparison.OrdinalIgnoreCase);
        var model = _chatReady ? SelectedChatModel : _saved.Model;
        var next = _saved with { ProjectId = projectId, Folder = full, Provider = DeskChatConnections.Stored(_chatProvider.SelectedIndex), Connection = DeskChatConnections.Key(_chatProvider.SelectedIndex),
            Model = model, Draft = _chatInput.Text, DraftAttachments = _draftFiles.ToArray(),
            PendingInstructions = _steeringQueue.Count > 0 ? _steeringQueue.Snapshot : null,
            Permissions = changedFolder ? CursorPermissionPolicy.AlwaysAsk : _saved.Permissions };
        // Persist before disposing the old transport or changing the visible workspace.
        _archive.Save(next);
        _saved = next; _projectFolder = full; _savePending = false;
        _swarmMemoryCaptured = null;
        RenderProjectMemory();
        if (changedFolder)
        {
            _steeringQueue.Pause();
            ResetChat(); _reconnectOnSend = model is not null && (CursorChat || GrokBuildChat || CodexChat);
            _restoring = true; RestoreProjectBehavior(); _restoring = false;
            SetChatBusy(false);
            _chatStatus.Text = full is null ? "Folder closed. This conversation and its messages are kept."
                : "Project folder selected. This conversation is kept; reconnect to continue in the selected folder.";
        }
        else UpdateProjectHeader();
    }
    public void Delete()
    {
        if (IsWorking) throw new InvalidOperationException("Stop this conversation and wait for it to finish before deleting it.");
        _archive.Delete(Id);
        _deleted = true;
        Dispose();
    }
    public void Dispose()
    {
        SetLiveIndicator(false); _tuiClock?.Stop();
        if (_disposed) return; _disposed = true;
        ReleaseSwarmWatch();
        _composerSpinner.IsActive = false; if (_composerProgress is not null) _composerProgress.Visibility = Visibility.Collapsed;
        _steeringQueue.Pause(); DrainSavedTurn(); Save(); _attachmentLifetime.Cancel(); _saveTimer.Stop(); _chatCancellation?.Cancel();
        foreach (var turn in _chatTurns) turn.Dispose();
        foreach (var panel in _openSubagentPanels.Values) panel.Release();
        _openSubagentPanels.Clear();
        foreach (var view in _subagentViews.Values) view.Retire();
        _codexChat?.Dispose(); _cursorChat?.Dispose(); _grokBuild?.Dispose(); _xaiChat?.Dispose(); _planChat?.Dispose(); _ollamaChat.Dispose();
    }
}
