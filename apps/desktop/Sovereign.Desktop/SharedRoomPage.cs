using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Sovereign.Desktop.Client;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;

namespace Sovereign.Desktop;

public sealed partial class MainWindow
{
    private readonly Grid _roomPage = new() { Visibility = Visibility.Collapsed, Padding = new Thickness(DeskTheme.Space24 + DeskTheme.Space4), RowSpacing = DeskTheme.Space12 };
    private readonly TextBox _roomName = new() { Header = "Your name", Text = "Host", MaxLength = 40, Width = 180 };
    private readonly TextBox _roomInvite = new() { Header = "Invitation · about 20 minutes · one use", PlaceholderText = "https://…/#…", IsSpellCheckEnabled = false };
    private readonly TextBlock _roomStatus = Note("Create a room on this PC, or paste an invitation to join. Nothing is shared until you connect.");
    private readonly TextBlock _roomMembers = Note("");
    private readonly TextBlock _roomModelChip = Note("No model connected");
    private readonly TextBlock _roomEmpty = Note("No messages yet. A room holds up to 8 members and 500 messages. Invitations last about 20 minutes and work once. Your AI draft stays on this PC until you share it.");
    private readonly StackPanel _roomMessages = new() { Spacing = DeskTheme.Space8 };
    private readonly TextBox _roomInput = new() { PlaceholderText = "Message everyone in this room", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64, MaxHeight = 110, MaxLength = 8000 };
    private readonly TextBox _roomAiDraft = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 160, BorderThickness = new Thickness(0) };
    private readonly StackPanel _savedRooms = new() { Spacing = DeskTheme.Space4 };
    private readonly List<RoomMessage> _roomHistory = [];
    private readonly List<Button> _roomActions = [];
    private SharedRoomHost? _roomHost;
    private RoomTunnel? _roomTunnel;
    private RoomConnection? _roomConnection;
    private Uri? _roomPublicAddress;
    private CancellationTokenSource? _roomLifetime;
    private CancellationTokenSource? _roomAiCancel;
    private ScrollViewer? _roomScroll;
    private Button? _roomShare;
    private Button? _roomDiscard;
    private Button? _roomJump;
    private Button? _roomRetry;
    private Border? _roomDraftCard;
    private bool _roomBusy;
    private bool _roomFollowTail = true;
    private long _roomSequence;
    private RoomPost? _roomPending;
    private string _roomAiModel = "";
    private Guid _roomAiRequestId;

    private void OpenRoom()
    {
        CloseChatView(); CloseSettings(); CloseGrokbot();
        if (_workspaceContent is not null) _workspaceContent.Visibility = Visibility.Collapsed;
        _roomPage.Visibility = Visibility.Visible; UpdateRoomModelChip(); RefreshSavedRooms(); MarkSystem("room");
    }
    private void CloseRoomView() => _roomPage.Visibility = Visibility.Collapsed;
    private void BuildRoomPage()
    {
        _roomPage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _roomPage.RowDefinitions.Add(new RowDefinition());
        _roomPage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Button Act(string name, Func<Task> action) { var b = Button(name, () => RunRoom(action)); _roomActions.Add(b); return b; }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DeskTheme.Space8, Children = {
            Act("Create online room", CreateRoomAsync), Act("Join room", JoinRoomAsync),
            Act("Copy invitation", CopyRoomInvitationAsync), Act("New invitation", RotateRoomInvitationAsync),
            Act("Leave / stop room", StopRoomAsync) } };
        _roomRetry = Button("Retry", () => RunRoom(CreateRoomAsync));
        _roomRetry.Visibility = Visibility.Collapsed;
        _roomActions.Add(_roomRetry);
        var saved = new Expander { Header = "Rooms on this PC", IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = _savedRooms };
        var top = new StackPanel { Spacing = DeskTheme.Space8, Children = { Head("Shared room"), _roomName, actions, _roomInvite, _roomStatus, _roomRetry, _roomMembers, saved } };
        _roomPage.Children.Add(top);
        _roomMessages.Children.Add(_roomEmpty);
        _roomScroll = new ScrollViewer { Content = _roomMessages, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _roomScroll.ViewChanged += (_, e) =>
        {
            if (e.IsIntermediate || _roomScroll is null) return;
            var gap = _roomScroll.ScrollableHeight - _roomScroll.VerticalOffset;
            _roomFollowTail = gap < 80 || _roomScroll.ScrollableHeight <= 0;
            if (_roomJump is not null) _roomJump.Visibility = _roomFollowTail ? Visibility.Collapsed : Visibility.Visible;
        };
        Grid.SetRow(_roomScroll, 1); _roomPage.Children.Add(_roomScroll);
        _roomJump = Button("Jump to latest", () =>
        {
            _roomFollowTail = true;
            if (_roomJump is not null) _roomJump.Visibility = Visibility.Collapsed;
            _roomScroll?.ChangeView(null, _roomScroll.ScrollableHeight, null, true);
        });
        _roomJump.Visibility = Visibility.Collapsed;
        _roomShare = new Button { Content = "Share", IsEnabled = false };
        AutomationName(_roomShare, "Share AI draft");
        _roomShare.Click += async (_, _) => await RunRoom(ShareRoomAiAsync);
        _roomDiscard = Button("Discard", () => ClearRoomDraft(true));
        _roomDraftCard = new Border
        {
            Visibility = Visibility.Collapsed, Background = DeskTheme.Hover, BorderBrush = DeskTheme.Hover, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(DeskTheme.Radius10), Padding = new Thickness(DeskTheme.Space12),
            Child = new StackPanel { Spacing = DeskTheme.Space8, Children = {
                Note("Preview · only you can see this until you share"),
                _roomAiDraft,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = DeskTheme.Space8, Children = { _roomShare, _roomDiscard } } } }
        };
        var askRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DeskTheme.Space8, Children = {
            Act("Send to room", SendRoomAsync), Act("Ask my connected AI", AskRoomAiAsync), _roomModelChip, Button("Stop AI", () => _roomAiCancel?.Cancel()) } };
        var security = new Expander
        {
            Header = "Connection and privacy", IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = Note("Your PC hosts while this app stays open. The relay carries messages encrypted in transit, not end-to-end. An invitation grants access to the room history. Joining also shares your signed account identity (including email) with the host. Refresh and provider tokens stay on your PC. Connect your provider in New chat first. Ask my connected AI sends the unsent box text plus this room’s messages to that provider. Private chats are excluded. Replies never trigger other AIs automatically. Room history is saved on the host PC.")
        };
        var bottom = new StackPanel { Spacing = DeskTheme.Space8, Children = { _roomJump, _roomDraftCard, _roomInput, askRow, Note("Enter to send · Shift+Enter for a new line"), security } };
        Grid.SetRow(bottom, 2); _roomPage.Children.Add(bottom);
        _roomInput.PreviewKeyDown += RoomInputKeyDown;
        AutomationName(_roomInput, "Shared room message"); AutomationName(_roomInvite, "Room invitation"); AutomationName(_roomName, "Room display name");
        Closed += async (_, _) => { try { await StopRoomAsync(); } catch { /* Process shutdown closes the loopback server. */ } };
    }
    private async void RoomInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        if ((shift & CoreVirtualKeyStates.Down) != 0) return;
        e.Handled = true;
        if (e.KeyStatus.WasKeyDown || _roomBusy) return;
        await RunRoom(SendRoomAsync);
    }
    private void UpdateRoomModelChip()
    {
        try
        {
            var model = _currentChat is null || !_chatReady ? null : SelectedChatModel;
            if (string.IsNullOrWhiteSpace(model)) { _roomModelChip.Text = "No model connected"; return; }
            var provider = CursorChat ? "Cursor" : XaiApiChat ? "xAI API" : "Grok Build";
            _roomModelChip.Text = provider + " · " + model;
        }
        catch { _roomModelChip.Text = "No model connected"; }
    }
    private async Task RunRoom(Func<Task> action)
    {
        if (_roomBusy) return;
        SetRoomBusy(true);
        if (_roomRetry is not null) _roomRetry.Visibility = Visibility.Collapsed;
        try { await action(); }
        catch (Exception ex) { _roomStatus.Text = ex.Message; }
        finally { SetRoomBusy(false); }
    }
    private void SetRoomBusy(bool value)
    {
        _roomBusy = value;
        foreach (var b in _roomActions) b.IsEnabled = !value;
        _roomName.IsEnabled = !value && _roomConnection is null;
        _roomInvite.IsEnabled = !value;
        if (_roomShare is not null) _roomShare.IsEnabled = !value && _roomAiRequestId != Guid.Empty;
        if (_roomDiscard is not null) _roomDiscard.IsEnabled = _roomDraftCard?.Visibility == Visibility.Visible;
    }
    private void ClearRoomDraft(bool announce)
    {
        _roomAiCancel?.Cancel();
        _roomAiDraft.Text = ""; _roomAiRequestId = Guid.Empty; _roomAiModel = "";
        if (_roomDraftCard is not null) _roomDraftCard.Visibility = Visibility.Collapsed;
        if (_roomShare is not null) _roomShare.IsEnabled = false;
        if (announce) _roomStatus.Text = "AI draft discarded.";
    }
    private static void CopyRoomText(string text)
    {
        var data = new DataPackage(); data.SetText(text); Clipboard.SetContent(data);
    }
    private async Task CopyRoomInvitationAsync()
    {
        if (_roomHost is null || _roomPublicAddress is null) throw new IOException("Only the host can invite someone.");
        if (string.IsNullOrWhiteSpace(_roomInvite.Text)) throw new IOException("The invitation field is empty. Use New invitation to create another. It expires in about 20 minutes.");
        CopyRoomText(_roomInvite.Text.Trim());
        _roomInvite.Text = "";
        _roomStatus.Text = "Invitation copied. It expires in about 20 minutes. The field was cleared.";
        await Task.CompletedTask;
    }
    private async Task RotateRoomInvitationAsync()
    {
        if (_roomHost is null || _roomPublicAddress is null) throw new IOException("Only the host can invite someone.");
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot, Title = "Replace invitation?",
            Content = new TextBlock { Text = "The current invitation stops working. The new one expires in about 20 minutes and can be used once.", TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
            PrimaryButtonText = "Replace and copy", CloseButtonText = "Keep current"
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var created = _roomHost.NewInvitation(_roomPublicAddress);
        CopyRoomText(created);
        _roomInvite.Text = "";
        _roomStatus.Text = "New invitation copied. It expires in about 20 minutes. The previous invitation no longer works, and the field was cleared.";
    }
    private async Task CreateRoomAsync()
    {
        if (_roomConnection is not null) throw new IOException("Leave the current room before creating another.");
        _roomStatus.Text = "Starting your room and temporary internet relay…";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        try {
            using (var download = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
                await CloudflaredTool.EnsureAsync(downloading: () => _roomStatus.Text = "Downloading tunnel tool…", ct: download.Token);
            _roomStatus.Text = "Starting your room and temporary internet relay…";
            var name = string.IsNullOrWhiteSpace(_roomName.Text) ? "Host" : _roomName.Text.Trim();
            _roomHost = new SharedRoomHost(_vm.ProfileDirectory, name); await _roomHost.StartAsync(timeout.Token);
            _roomTunnel = new RoomTunnel(); _roomPublicAddress = await _roomTunnel.StartAsync(AppContext.BaseDirectory, _vm.ProfileDirectory, _roomHost.Address, timeout.Token);
            _roomConnection = new RoomConnection(_roomHost.Address, _roomHost.Owner);
            _roomInvite.Text = _roomHost.NewInvitation(_roomPublicAddress); StartRoomPolling();
            _roomStatus.Text = "Hosting on this PC. Copy the invitation for the person you're inviting. It expires in about 20 minutes. Keep this app open.";
            RefreshSavedRooms();
        } catch { await StopRoomAsync(); if (_roomRetry is not null) _roomRetry.Visibility = Visibility.Visible; throw; }
    }
    private async Task JoinRoomAsync()
    {
        if (_roomConnection is not null) throw new IOException("Leave the current room first.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var name = string.IsNullOrWhiteSpace(_roomName.Text) ? "Guest" : _roomName.Text.Trim();
        _roomConnection = await RoomConnection.JoinAsync(_roomInvite.Text, name, timeout.Token);
        _roomInvite.Text = ""; StartRoomPolling(); _roomStatus.Text = "Joined. Keep this app open while you are in the room.";
    }
    private void StartRoomPolling()
    {
        _roomHistory.Clear(); _roomMessages.Children.Clear(); _roomMessages.Children.Add(_roomEmpty); _roomEmpty.Visibility = Visibility.Visible;
        _roomSequence = 0; _roomPending = null; ClearRoomDraft(false); _roomFollowTail = true;
        _roomLifetime = new CancellationTokenSource(); _ = PollRoomAsync(_roomConnection!, _roomLifetime.Token);
    }
    private FrameworkElement RoomBubble(RoomMessage message)
    {
        var self = _roomConnection?.Session.ParticipantId == message.ParticipantId || (_roomConnection is null && message.Kind != "ai" && message.Name == _roomName.Text);
        var role = message.Kind == "ai" ? "AI" : self ? "You" : "Them";
        var title = message.Kind == "ai" ? message.Name + "’s AI · " + message.Model : self ? "You · " + message.Name : message.Name;
        var body = new StackPanel { Spacing = DeskTheme.Space4, Children = {
            new TextBlock { Text = role + " · " + title + " · " + message.Created.ToLocalTime().ToString("HH:mm"), FontSize = DeskTheme.SizeCaption, Foreground = DeskTheme.Muted },
            new TextBlock { Text = message.Text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = DeskTheme.SizeBody } } };
        var color = message.Kind == "ai" ? DeskTheme.Hover : self ? DeskTheme.Hover : DeskTheme.Raised;
        return new Border { Background = color, BorderBrush = DeskTheme.Hover, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(DeskTheme.Radius10), Padding = new Thickness(DeskTheme.Space12), Child = body };
    }
    private void ShowRoomMessage(RoomMessage message)
    {
        _roomEmpty.Visibility = Visibility.Collapsed;
        _roomMessages.Children.Add(RoomBubble(message));
        if (!_roomFollowTail || _roomScroll is null) return;
        var scroll = _roomScroll;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_roomFollowTail) scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
        });
    }
    private async Task PollRoomAsync(RoomConnection connection, CancellationToken ct)
    {
        var lost = false;
        while (!ct.IsCancellationRequested) {
            try {
                var snapshot = await connection.ReadAsync(_roomSequence, ct);
                if (ct.IsCancellationRequested) return;
                if (lost) { _roomStatus.Text = "Room connection restored."; lost = false; }
                _roomMembers.Text = snapshot.Members.Length == 0 ? "" : "Members: " + string.Join(" · ", snapshot.Members.Select(m => m.Name + (m.Host || (_roomHost is not null && m.Id == _roomHost.Owner.ParticipantId) ? " (host)" : "")));
                var added = false;
                foreach (var m in snapshot.Messages.OrderBy(m => m.Sequence)) {
                    if (m.Sequence <= _roomSequence) continue;
                    _roomHistory.Add(m); _roomSequence = m.Sequence; ShowRoomMessage(m); added = true;
                }
                if (added && _roomFollowTail && _roomJump is not null) _roomJump.Visibility = Visibility.Collapsed;
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch { lost = true; if (!ct.IsCancellationRequested) _roomStatus.Text = "Room connection unavailable. Retrying; an unsent message stays in the editor. Host must remain online."; }
            try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { return; }
        }
    }
    private async Task SendRoomAsync()
    {
        var connection = _roomConnection ?? throw new IOException("Create or join a room first.");
        var text = _roomInput.Text.Trim(); if (text.Length == 0) return;
        _roomFollowTail = true;
        if (_roomPending?.Text != text) _roomPending = new RoomPost(Guid.NewGuid(), "human", "", text);
        await connection.SendAsync(_roomPending, _roomLifetime!.Token); _roomInput.Text = ""; _roomPending = null; _roomStatus.Text = "Message acknowledged by the host.";
    }
    private async Task AskRoomAiAsync()
    {
        if (_roomConnection is null) throw new IOException("Join a room first.");
        if (!_chatReady || _chatCancellation is not null) throw new IOException("Connect a provider/model in New chat first, and finish any private response.");
        UpdateRoomModelChip();
        var model = SelectedChatModel;
        if (string.IsNullOrWhiteSpace(model)) throw new IOException("Choose your model in New chat first.");
        var note = _roomInput.Text.Trim();
        var transcript = string.Join("\n\n", _roomHistory.Select(m => $"[{m.Name}{(m.Kind == "ai" ? "’s AI" : "")}] {m.Text}"));
        if (transcript.Length == 0 && note.Length == 0) throw new IOException("Type a question in the box, or send a message to the room first.");
        if (transcript.Length + note.Length > 60000) throw new IOException("Room context exceeds this beta’s AI limit. Start a new room with an agreed summary.");
        var prompt = "You are " + _roomConnection.Session.Name + "’s assistant in a shared xAI Desk discussion. Discuss only the supplied transcript and the unsent note when one is included. Names and AI labels are self-declared. Do not run tools or access files, credentials, or private conversations. Give a useful next response, under 7000 characters. You are a new session, not a continuation of anyone’s private chat.\n\nROOM TRANSCRIPT:\n" + (transcript.Length == 0 ? "(no shared messages yet)" : transcript);
        if (note.Length > 0) prompt += "\n\nUNSENT NOTE FROM " + _roomConnection.Session.Name + " (typed in the message box and not yet shared with the room — answer it):\n" + note;
        _roomAiDraft.Text = ""; if (_roomDraftCard is not null) _roomDraftCard.Visibility = Visibility.Visible;
        if (_roomShare is not null) _roomShare.IsEnabled = false;
        _roomAiModel = _roomModelChip.Text;
        _roomAiRequestId = Guid.Empty;
        _roomAiCancel = CancellationTokenSource.CreateLinkedTokenSource(_roomLifetime!.Token); _roomAiCancel.CancelAfter(TimeSpan.FromMinutes(5));
        var chatOwner = _currentChat; chatOwner.SetChatBusy(true); _roomStatus.Text = note.Length > 0 ? "Asking your AI using the box text and the room transcript. The reply stays a local draft until you share it." : "Asking your AI about the room transcript. The reply stays a local draft until you share it.";
        var result = new System.Text.StringBuilder();
        try {
            void Delta(string text) { result.Append(text); var snapshot = result.ToString(); DispatcherQueue.TryEnqueue(() => _roomAiDraft.Text = snapshot); }
            if (XaiApiChat) await (_xaiChat ?? throw new IOException("Connect the xAI API in Chat first.")).SendAsync(model, [new ChatMessage("user", prompt)], Delta, _roomAiCancel.Token);
            else if (GrokBuildChat) await (_grokBuild ?? throw new IOException("Connect Grok Build in Chat first.")).SendAsync(model, prompt, Delta, _roomAiCancel.Token);
            else await (_cursorChat ?? throw new IOException("Connect Cursor in Chat first.")).SendAsync(model, prompt, Delta, _roomAiCancel.Token);
            _roomAiDraft.Text = result.ToString();
            if (result.Length == 0 || result.Length > 8000) throw new IOException("AI reply is empty or too long to share in this beta.");
            _roomAiRequestId = Guid.NewGuid(); _roomStatus.Text = "Draft ready. Share it with the room, or discard it.";
        }
        catch (OperationCanceledException)
        {
            _roomAiRequestId = Guid.Empty; _roomAiDraft.Text = "";
            if (_roomDraftCard is not null) _roomDraftCard.Visibility = Visibility.Collapsed;
            if (_roomConnection is not null) _roomStatus.Text = "AI draft discarded.";
        }
        finally { _roomAiCancel.Dispose(); _roomAiCancel = null; chatOwner.SetChatBusy(false); }
    }
    private async Task ShareRoomAiAsync()
    {
        if (_roomConnection is null || _roomAiRequestId == Guid.Empty) throw new IOException("Generate a complete AI draft first.");
        await _roomConnection.SendAsync(new RoomPost(_roomAiRequestId, "ai", _roomAiModel, _roomAiDraft.Text), _roomLifetime!.Token);
        _roomStatus.Text = "AI reply shared. Repeating Share will not duplicate it.";
    }
    private void RefreshSavedRooms()
    {
        try { RefreshSavedRoomsCore(); }
        catch { /* A missing profile folder should not block the room page. */ }
    }
    private void RefreshSavedRoomsCore()
    {
        _savedRooms.Children.Clear();
        var root = Path.Combine(_vm.ProfileDirectory, "shared-rooms");
        if (!Directory.Exists(root)) { _savedRooms.Children.Add(Note("No saved rooms on this PC yet.")); return; }
        var files = Directory.GetDirectories(root).Select(dir => new FileInfo(Path.Combine(dir, "transcript.json"))).Where(file => file.Exists).OrderByDescending(file => file.LastWriteTimeUtc).ToList();
        if (files.Count == 0) { _savedRooms.Children.Add(Note("No saved rooms on this PC yet.")); return; }
        foreach (var file in files)
        {
            var id = file.Directory!.Name;
            var path = file.FullName;
            _savedRooms.Children.Add(Button(id[..Math.Min(8, id.Length)] + " · " + file.LastWriteTime.ToString("yyyy-MM-dd HH:mm"), () => ShowSavedTranscript(path)));
        }
    }
    private void ShowSavedTranscript(string path)
    {
        if (_roomConnection is not null) { _roomStatus.Text = "Leave the live room before opening a saved transcript."; return; }
        try
        {
            var messages = JsonSerializer.Deserialize<List<RoomMessage>>(File.ReadAllText(path)) ?? [];
            _roomHistory.Clear(); _roomMessages.Children.Clear(); _roomMessages.Children.Add(_roomEmpty);
            _roomHistory.AddRange(messages);
            _roomEmpty.Visibility = messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var message in messages) _roomMessages.Children.Add(RoomBubble(message));
            _roomMembers.Text = "Saved transcript on this PC. This does not restart the room.";
            _roomStatus.Text = "Opened a host transcript from this PC.";
            _roomFollowTail = true;
            _roomScroll?.ChangeView(null, _roomScroll.ScrollableHeight, null, true);
        }
        catch { _roomStatus.Text = "Could not read that saved transcript."; }
    }
    private async Task StopRoomAsync()
    {
        _roomAiCancel?.Cancel(); _roomLifetime?.Cancel(); _roomLifetime?.Dispose(); _roomLifetime = null;
        var connection = _roomConnection; _roomConnection = null;
        if (connection is not null) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)); try { await connection.LeaveAsync(timeout.Token); } catch { /* Host may already be offline. */ } finally { connection.Dispose(); } }
        _roomTunnel?.Dispose(); _roomTunnel = null;
        var host = _roomHost; _roomHost = null; if (host is not null) await host.DisposeAsync();
        _roomPublicAddress = null; _roomInvite.Text = ""; _roomMembers.Text = "";
        _roomStatus.Text = "Room stopped / left. Host transcript remains in the profile; invitations and sessions are no longer usable after host shutdown.";
        RefreshSavedRooms();
    }
}
