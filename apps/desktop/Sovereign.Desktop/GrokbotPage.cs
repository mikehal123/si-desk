using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Sovereign.Desktop.Client;
using Sovereign.Desktop.Connector;
using Sovereign.Desktop.Core;

namespace Sovereign.Desktop;

public sealed partial class MainWindow
{
    private readonly Grid _grokbotPage = new() { Visibility = Visibility.Collapsed, Padding = new Thickness(DeskTheme.Space24, DeskTheme.Space12, DeskTheme.Space24, DeskTheme.Space12), RowSpacing = DeskTheme.Space8 };
    private readonly StackPanel _grokbotMessages = new() { Spacing = DeskTheme.Space8 };
    private readonly StackPanel _grokbotAudit = new() { Spacing = DeskTheme.Space4 };
    private readonly TextBlock _grokbotLink = new() { TextWrapping = TextWrapping.Wrap, FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeBody, Foreground = DeskTheme.Text };
    private readonly TextBlock _grokbotHint = Note("");
    private readonly TextBox _grokbotInput = new() { PlaceholderText = "Message Grokbot", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64, MaxHeight = 140 };
    private readonly ToggleSwitch _grokbotAsk = new() { OnContent = "On", OffContent = "Off" };
    private readonly DispatcherTimer _grokbotTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Border _grokbotControlBanner = new()
    {
        Visibility = Visibility.Collapsed,
        Background = DeskTheme.Raised,
        Padding = new Thickness(DeskTheme.Space16, DeskTheme.Space8, DeskTheme.Space16, DeskTheme.Space8),
        Child = new TextBlock
        {
            Text = "Grokbot is controlling the app",
            FontFamily = DeskTheme.UiFont,
            FontSize = DeskTheme.SizeBody,
            Foreground = DeskTheme.Text,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        }
    };
    private readonly GrokbotMailbox _grokbotMail = GrokbotMailbox.Open(System.IO.Path.Combine(XaiDeskIdentity.DataDirectory(), "grokbot"));
    private GrokbotWakeDebounce? _grokbotWake;
    private Button? _grokbotStop;
    private bool _grokbotUi;
    private bool _grokbotNoticesPrimed;
    private Border? _grokbotUnread;
    private bool _grokbotWindowActive;
    private int _grokbotArrivedWhileClosed;
    private string? _grokbotCopiedId;
    private DateTimeOffset _grokbotCopiedUntil;
    private int _grokbotCopyGeneration;
    private DispatcherTimer? _grokbotCopyTimer;
    private int _grokbotDotEpoch;
    private readonly List<Storyboard> _grokbotDotMotion = [];
    private readonly HashSet<string> _grokbotAnnouncedReplies = new(StringComparer.Ordinal);
    private readonly GrokbotAppToaster _grokbotToaster = new();
    private readonly Image _grokbotPendingView = new() { MaxWidth = 160, MaxHeight = 96, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
    private byte[]? _grokbotImage;
    private ScrollViewer? _grokbotScroll;
    private Button? _grokbotJump;
    private Border? _grokbotJumpBadge;
    private TextBlock? _grokbotJumpCount;
    private GrokbotTranscript.Jump _grokbotJumpState = GrokbotTranscript.Jump.Following;
    private int _grokbotSeenEntries = -1;
    private bool _grokbotHoldScroll;
    private int _grokbotHoldGeneration;
    private bool _grokbotJumpAnimating;
    private bool _grokbotPainting;
    private string? _grokbotHoverCopyId;
    private EventHandler<object>? _grokbotScrollRetry;

    internal GrokbotMailbox GrokbotMail => _grokbotMail;

    private Grid GrokbotNavHost()
    {
        var prefs = _grokbotMail.PreferencesCopy();
        var host = new Grid { Width = 28, Height = 28 };
        host.Children.Add(GrokbotAvatar.Mark(prefs.AvatarColor, prefs.AvatarShape, 28));
        var badge = new Border
        {
            MinWidth = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(255, 196, 32, 38)),
            Padding = new Thickness(4, 0, 4, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            Child = new TextBlock
            {
                FontFamily = DeskTheme.UiFont,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        AutomationName(badge, "Grokbot unread");
        host.Children.Add(badge);
        _grokbotUnread = badge;
        ShowGrokbotUnread();
        return host;
    }

    private void BuildGrokbotPage()
    {
        RememberGrokbotRepliesOnDisk();
        AutomationName(_grokbotInput, "Grokbot message");
        AutomationName(_grokbotAsk, "Ask Grokbot to act");
        ToolTipService.SetToolTip(_grokbotAsk, "Ask Grokbot to act");
        AutomationName(_grokbotLink, "Grokbot connection");
        AutomationName(_grokbotControlBanner, "Grokbot is controlling the app");
        _grokbotPage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _grokbotPage.RowDefinitions.Add(new RowDefinition());
        _grokbotPage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _grokbotPage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var prefs = _grokbotMail.PreferencesCopy();
        var header = new Grid { ColumnSpacing = DeskTheme.Space12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var leading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DeskTheme.Space12 };
        var headerMark = GrokbotAvatar.Mark(prefs.AvatarColor, prefs.AvatarShape, 36);
        headerMark.VerticalAlignment = VerticalAlignment.Center;
        leading.Children.Add(headerMark);
        var titles = new StackPanel { Spacing = DeskTheme.Space4 };
        titles.Children.Add(Head("Grokbot"));
        titles.Children.Add(_grokbotLink);
        leading.Children.Add(titles);
        header.Children.Add(leading);
        var menu = GrokbotHeaderMenu();
        menu.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(menu, 1);
        header.Children.Add(menu);
        _grokbotPage.Children.Add(header);
        var list = new Grid();
        _grokbotScroll = new ScrollViewer { Content = _grokbotMessages, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _grokbotScroll.ViewChanged += OnGrokbotViewChanged;
        _grokbotScroll.SizeChanged += (_, _) => FollowGrokbotIfStuck();
        list.Children.Add(_grokbotScroll);
        list.Children.Add(GrokbotJumpButton());
        Grid.SetRow(list, 1);
        _grokbotPage.Children.Add(list);
        var audit = new Expander
        {
            Header = new TextBlock { Text = "Grokbot audit", FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeCaption, Foreground = DeskTheme.Muted },
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            FontSize = DeskTheme.SizeCaption,
            Padding = new Thickness(0),
            MinHeight = 0,
            Content = _grokbotAudit
        };
        AutomationName(audit, "Grokbot audit");
        Grid.SetRow(audit, 2);
        _grokbotPage.Children.Add(audit);
        _grokbotStop = DeskButton("Stop Grokbot");
        _grokbotStop.MinHeight = 28;
        _grokbotStop.Padding = new Thickness(DeskTheme.Space12, DeskTheme.Space4, DeskTheme.Space12, DeskTheme.Space4);
        _grokbotStop.HorizontalAlignment = HorizontalAlignment.Left;
        _grokbotStop.FontSize = DeskTheme.SizeCaption;
        _grokbotStop.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
        _grokbotStop.Click += (_, _) => ToggleGrokbotStop();
        AutomationName(_grokbotStop, "Stop Grokbot");
        var send = Button("Send", SendGrokbot);
        AutomationName(send, "Send to Grokbot");
        send.MinHeight = 28;
        send.Padding = new Thickness(DeskTheme.Space12, DeskTheme.Space4, DeskTheme.Space12, DeskTheme.Space4);
        send.VerticalAlignment = VerticalAlignment.Center;
        send.HorizontalAlignment = HorizontalAlignment.Right;
        _grokbotInput.PreviewKeyDown += GrokbotInputKeyDown;
        _grokbotInput.Paste += async (_, e) =>
        {
            e.Handled = true;
            await PasteGrokbotAsync();
        };
        _grokbotInput.BorderThickness = new Thickness(0);
        _grokbotInput.BorderBrush = DeskTheme.Transparent;
        _grokbotInput.Background = DeskTheme.Transparent;
        _grokbotInput.VerticalAlignment = VerticalAlignment.Center;
        var attach = DeskButton("Image");
        attach.MinHeight = 28;
        attach.Padding = new Thickness(DeskTheme.Space12, DeskTheme.Space4, DeskTheme.Space12, DeskTheme.Space4);
        attach.VerticalAlignment = VerticalAlignment.Center;
        AutomationName(attach, "Attach image");
        attach.Click += async (_, _) =>
        {
            attach.IsEnabled = false;
            try { await PickGrokbotImageAsync(); }
            finally { attach.IsEnabled = true; }
        };
        var inputGrid = new Grid { ColumnSpacing = DeskTheme.Space8 };
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputGrid.Children.Add(_grokbotInput);
        Grid.SetColumn(attach, 1);
        inputGrid.Children.Add(attach);
        Grid.SetColumn(send, 2);
        inputGrid.Children.Add(send);
        var rounded = new Border
        {
            Background = DeskTheme.Surface,
            BorderBrush = DeskTheme.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(DeskTheme.Radius10),
            Padding = new Thickness(DeskTheme.Space12, DeskTheme.Space4, DeskTheme.Space8, DeskTheme.Space4),
            Child = inputGrid
        };
        _grokbotAsk.OnContent = "";
        _grokbotAsk.OffContent = "";
        _grokbotAsk.MinWidth = 0;
        _grokbotAsk.VerticalAlignment = VerticalAlignment.Center;
        var composerRow = new Grid { ColumnSpacing = DeskTheme.Space12 };
        composerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        composerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        composerRow.Children.Add(rounded);
        Grid.SetColumn(_grokbotAsk, 1);
        composerRow.Children.Add(_grokbotAsk);
        AutomationName(_grokbotPendingView, "Grokbot attached image");
        var composer = new StackPanel { Spacing = DeskTheme.Space8, Children = { _grokbotHint, _grokbotPendingView, composerRow, _grokbotStop } };
        Grid.SetRow(composer, 3);
        _grokbotPage.Children.Add(composer);
        EnableGrokbotDrop(_grokbotPage);
        EnableGrokbotDrop(_grokbotMessages);
        EnableGrokbotDrop(_grokbotInput);
        EnableGrokbotDrop(rounded);
        _grokbotTimer.Tick += (_, _) => TickGrokbot();
        _grokbotTimer.Start();
        Activated += (_, args) =>
        {
            _grokbotWindowActive = args.WindowActivationState is Microsoft.UI.Xaml.WindowActivationState.CodeActivated or Microsoft.UI.Xaml.WindowActivationState.PointerActivated;
            ShowGrokbotUnread();
        };
        PaintGrokbot();
        ApplyGrokbotNav();
    }

    private void OpenGrokbot()
    {
        if (!_grokbotMail.PreferencesCopy().TabEnabled)
        {
            OpenChat();
            return;
        }
        CloseChatView();
        CloseRoomView();
        CloseSettings();
        if (_workspaceContent is not null) _workspaceContent.Visibility = Visibility.Collapsed;
        _grokbotPage.Visibility = Visibility.Visible;
        ShowGrokbotUnread();
        PaintGrokbot();
        MarkSystem("grokbot");
    }

    private void CloseGrokbot() => _grokbotPage.Visibility = Visibility.Collapsed;

    internal void ApplyGrokbotNav()
    {
        var on = _grokbotMail.PreferencesCopy().TabEnabled;
        foreach (ListViewItem item in _nav.Items)
            if ((string)item.Tag == "grokbot")
                item.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on && _activeSystem == "grokbot") Navigate("chat");
    }

    internal void SetGrokbotControlling(bool on) =>
        _grokbotControlBanner.Visibility = on || _grokbotMail.Controlling ? Visibility.Visible : Visibility.Collapsed;

    internal void RefreshGrokbot()
    {
        AnnounceNewGrokbotReplies();
        if (_grokbotPage.Visibility == Visibility.Visible) PaintGrokbot();
    }

    private void RememberGrokbotRepliesOnDisk()
    {
        foreach (var mail in _grokbotMail.Messages)
        {
            if (!string.IsNullOrWhiteSpace(mail.Reply))
                _grokbotAnnouncedReplies.Add(mail.Id);
        }
        _grokbotNoticesPrimed = true;
    }

    private void AnnounceNewGrokbotReplies()
    {
        if (!_grokbotNoticesPrimed)
        {
            RememberGrokbotRepliesOnDisk();
            ShowGrokbotUnread();
            return;
        }
        var tabVisible = _grokbotPage.Visibility == Visibility.Visible;
        foreach (var mail in _grokbotMail.Messages)
        {
            if (string.IsNullOrWhiteSpace(mail.Reply) || !_grokbotAnnouncedReplies.Add(mail.Id)) continue;
            var line = GrokbotNotice.FirstLine(mail.Reply);
            if (GrokbotNotice.ShouldToast(tabVisible, _grokbotWindowActive) && line.Length > 0)
                _grokbotToaster.Show(line);
            if (GrokbotNotice.ShouldMarkUnread(tabVisible, _grokbotWindowActive)) _grokbotArrivedWhileClosed++;
        }
        ShowGrokbotUnread();
    }

    private void TickGrokbot()
    {
        var stopped = _grokbotMail.PreferencesCopy().Stopped;
        var anyPickedUp = !stopped && _grokbotMail.Messages.Any(mail => mail.Status == GrokbotMailStatus.PickedUp && string.IsNullOrWhiteSpace(mail.Reply));
        _grokbotTimer.Interval = GrokbotThinking.PollInterval(TimeSpan.FromSeconds(1), anyPickedUp);
        RefreshGrokbot();
    }

    private void ShowGrokbotUnread()
    {
        var tabVisible = _grokbotPage.Visibility == Visibility.Visible;
        if (tabVisible && _grokbotWindowActive) _grokbotArrivedWhileClosed = 0;
        if (_grokbotUnread?.Child is not TextBlock label) return;
        var count = GrokbotNotice.UnreadCount(_grokbotArrivedWhileClosed, tabVisible, _grokbotWindowActive);
        label.Text = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _grokbotUnread.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void GrokbotInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var shift = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0;
        var control = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0;
        switch (GrokTuiPresentation.Key(e.Key.ToString(), shift, control))
        {
            case GrokTuiKeyAction.NewLine:
                return;
            case GrokTuiKeyAction.Send:
                e.Handled = true;
                if (e.KeyStatus.WasKeyDown) return;
                SendGrokbot();
                return;
        }
    }

    private void SendGrokbot()
    {
        try
        {
            var pending = _grokbotImage;
            var mail = pending is null
                ? _grokbotMail.Queue(_grokbotInput.Text ?? "", _grokbotAsk.IsOn)
                : _grokbotMail.Queue(_grokbotInput.Text ?? "", _grokbotAsk.IsOn, pending);
            _grokbotInput.Text = "";
            _grokbotImage = null;
            ShowGrokbotPending();
            _grokbotHint.Text = "";
            PaintGrokbot();
            ScheduleGrokbotWake(mail);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            _grokbotHint.Text = ex.Message;
        }
    }

    private void ToggleGrokbotStop()
    {
        var prefs = _grokbotMail.PreferencesCopy();
        prefs.Stopped = !prefs.Stopped;
        _grokbotMail.SavePreferences(prefs);
        PaintGrokbot();
    }

    private void PaintGrokbot()
    {
        _grokbotPainting = true;
        _grokbotHoldScroll = true;
        try
        {
            StopGrokbotDotMotion();
            _grokbotMessages.Children.Clear();
            var messages = _grokbotMail.Messages;
            var entries = GrokbotTranscript.EntryCount(messages);
            var arrived = _grokbotSeenEntries < 0 ? 0 : Math.Max(0, entries - _grokbotSeenEntries);
            _grokbotSeenEntries = entries;
            if (messages.Count == 0)
                _grokbotMessages.Children.Add(Note("No messages yet. Send one here. Grokbot picks it up through the connector."));
            var prefs = _grokbotMail.PreferencesCopy();
            var now = DateTimeOffset.UtcNow;
            foreach (var mail in messages)
            {
                var status = mail.Status switch
                {
                    GrokbotMailStatus.Queued => "Queued",
                    GrokbotMailStatus.PickedUp => "Picked up",
                    _ => "Answered"
                };
                var bar = GrokTuiPresentation.UserBar(mail.Text, mail.Created.ToLocalTime());
                var footer = bar.Time + " · " + status + (mail.AskToAct ? " · Act on" : "");
                _grokbotMessages.Children.Add(GrokbotMine(mail, bar.Text, footer));
                if (!string.IsNullOrWhiteSpace(mail.Reply))
                {
                    var when = mail.AnsweredAt?.ToLocalTime() ?? mail.Created.ToLocalTime();
                    var reply = GrokTuiPresentation.UserBar(mail.Reply, when);
                    _grokbotMessages.Children.Add(GrokbotReply(mail, reply.Text, reply.Time + " · Answered", prefs.AvatarColor, prefs.AvatarShape));
                }
                else
                {
                    var label = GrokbotThinking.Label(mail.Status, mail.Reply, prefs.Stopped, mail.Created, mail.PickedUpAt, now);
                    if (!prefs.Stopped && label.Length > 0)
                        _grokbotMessages.Children.Add(GrokbotThinkingBubble(label, prefs.AvatarColor, prefs.AvatarShape));
                }
            }
            _grokbotAudit.Children.Clear();
            var audit = _grokbotMail.Audit;
            if (audit.Count == 0) _grokbotAudit.Children.Add(Note("No tool calls yet."));
            foreach (var line in audit.TakeLast(40))
                _grokbotAudit.Children.Add(Note(line.At.ToLocalTime().ToString("HH:mm:ss") + "  " + line.Tool + "  " + line.Args + "  " + line.Result));
            var listening = _connectorHost is { IsBound: true };
            var link = _grokbotMail.Link(listening);
            _grokbotLink.Text = link.State switch
            {
                GrokbotLinkState.Connected => "Connected · last poll " + PollText(link.LastPoll),
                GrokbotLinkState.Waiting => "Waiting · last poll " + PollText(link.LastPoll),
                _ => "Off"
            };
            if (_grokbotStop is not null)
            {
                _grokbotStop.Content = prefs.Stopped ? "Resume Grokbot" : "Stop Grokbot";
                AutomationName(_grokbotStop, prefs.Stopped ? "Resume Grokbot" : "Stop Grokbot");
            }
            if (_grokbotMail.Controlling) _grokbotControlBanner.Visibility = Visibility.Visible;
            PlaceGrokbotScroll(arrived);
        }
        finally
        {
            _grokbotPainting = false;
            var generation = ++_grokbotHoldGeneration;
            if (!DispatcherQueue.TryEnqueue(() =>
            {
                if (generation == _grokbotHoldGeneration) _grokbotHoldScroll = false;
            })) _grokbotHoldScroll = false;
        }
    }

    private const double GrokbotBubbleMaxWidth = 520;

    private UIElement GrokbotMine(GrokbotMail mail, string text, string footer)
    {
        var column = GrokbotBubbleColumn(text, footer, true, "you:" + mail.Id, mail.Image);
        if (!string.IsNullOrWhiteSpace(mail.WakeNote))
        {
            column.Children.Add(new TextBlock
            {
                Text = mail.WakeNote,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                FontSize = DeskTheme.SizeCaption,
                FontFamily = DeskTheme.UiFont,
                Foreground = DeskTheme.Text,
                MaxWidth = GrokbotBubbleMaxWidth,
                HorizontalAlignment = HorizontalAlignment.Right
            });
        }
        return column;
    }

    private UIElement GrokbotReply(GrokbotMail mail, string text, string footer, string? color, string? shape)
    {
        var mark = GrokbotAvatar.Mark(color, shape, 28);
        mark.VerticalAlignment = VerticalAlignment.Top;
        var column = GrokbotBubbleColumn(text, footer, false, "bot:" + mail.Id);
        column.Children.Insert(1, GrokbotReplyActions(mail));
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = DeskTheme.Space8,
            HorizontalAlignment = HorizontalAlignment.Left,
            Children = { mark, column }
        };
    }

    private UIElement GrokbotThinkingBubble(string label, string? color, string? shape)
    {
        var mark = GrokbotAvatar.Mark(color, shape, 28);
        mark.VerticalAlignment = VerticalAlignment.Top;
        var text = new TextBlock
        {
            Text = label,
            TextWrapping = TextWrapping.Wrap,
            FontSize = DeskTheme.SizeBody,
            FontFamily = DeskTheme.UiFont,
            Foreground = DeskTheme.Text,
            MaxWidth = GrokbotBubbleMaxWidth - 48,
            VerticalAlignment = VerticalAlignment.Center
        };
        var bubble = new Border
        {
            Background = DeskTheme.Surface,
            BorderBrush = DeskTheme.Divider,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(DeskTheme.Space12, DeskTheme.Space8, DeskTheme.Space12, DeskTheme.Space8),
            CornerRadius = new CornerRadius(DeskTheme.Radius10),
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = GrokbotBubbleMaxWidth,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = DeskTheme.Space8,
                Children = { text, GrokbotThinkingDots() }
            }
        };
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = DeskTheme.Space8,
            HorizontalAlignment = HorizontalAlignment.Left,
            Children = { mark, bubble }
        };
    }

    private UIElement GrokbotThinkingDots()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        var epoch = _grokbotDotEpoch;
        for (var i = 0; i < 3; i++)
        {
            var dot = new Ellipse
            {
                Width = 5,
                Height = 5,
                Fill = DeskTheme.Text,
                Opacity = 0.2,
                VerticalAlignment = VerticalAlignment.Center
            };
            var pulse = new DoubleAnimation
            {
                From = 0.2,
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(480)),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromMilliseconds(160 * i)
            };
            var board = new Storyboard();
            Storyboard.SetTarget(pulse, dot);
            Storyboard.SetTargetProperty(pulse, "Opacity");
            board.Children.Add(pulse);
            _grokbotDotMotion.Add(board);
            void Begin()
            {
                if (epoch != _grokbotDotEpoch) return;
                try { board.Begin(); }
                catch (Exception) { }
            }
            if (dot.IsLoaded) Begin();
            else dot.Loaded += (_, _) => Begin();
            row.Children.Add(dot);
        }
        return row;
    }

    private void StopGrokbotDotMotion()
    {
        _grokbotDotEpoch++;
        foreach (var board in _grokbotDotMotion)
        {
            try { board.Stop(); }
            catch (Exception) { }
        }
        _grokbotDotMotion.Clear();
    }

    private StackPanel GrokbotReplyActions(GrokbotMail mail)
    {
        var id = mail.Id;
        var like = GrokbotActionButton(mail.Liked ? "Liked" : "Like", mail.Liked);
        var liked = mail.Liked;
        like.Click += (_, _) =>
        {
            _grokbotMail.SetLiked(id, !liked);
            PaintGrokbot();
        };
        AutomationName(like, liked ? "Liked Grokbot reply" : "Like Grokbot reply");
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = DeskTheme.Space8,
            HorizontalAlignment = HorizontalAlignment.Left,
            Children = { like }
        };
    }

    private static Button GrokbotActionButton(string label, bool selected) => new()
    {
        Content = label,
        FontFamily = DeskTheme.UiFont,
        FontSize = DeskTheme.SizeCaption,
        FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        Foreground = DeskTheme.Text,
        Background = selected ? DeskTheme.Raised : DeskTheme.Transparent,
        BorderBrush = selected ? DeskTheme.Text : DeskTheme.Divider,
        BorderThickness = new Thickness(selected ? 2 : 1),
        CornerRadius = new CornerRadius(DeskTheme.Radius6),
        Padding = new Thickness(DeskTheme.Space8, 2, DeskTheme.Space8, 2),
        MinHeight = 0,
        MinWidth = 0,
        HorizontalAlignment = HorizontalAlignment.Left,
        UseSystemFocusVisuals = true,
        FocusVisualPrimaryBrush = DeskTheme.Accent,
        FocusVisualSecondaryBrush = DeskTheme.Background
    };

    private void CopyGrokbotText(string reply, string id)
    {
        var queue = DispatcherQueue;
        _ = FinishGrokbotCopyAsync(queue, reply, id);
    }

    private async Task FinishGrokbotCopyAsync(Microsoft.UI.Dispatching.DispatcherQueue queue, string reply, string id)
    {
        try
        {
            if (!await ChatClipboard.CopyAsync(queue, reply)) return;
        }
        catch { return; }
        queue.TryEnqueue(() =>
        {
            var generation = ++_grokbotCopyGeneration;
            _grokbotCopiedId = id;
            _grokbotCopiedUntil = DateTimeOffset.UtcNow.AddSeconds(1);
            if (_grokbotPage.Visibility == Visibility.Visible) PaintGrokbot();
            _grokbotCopyTimer?.Stop();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _grokbotCopyTimer = timer;
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (generation != _grokbotCopyGeneration) return;
                _grokbotCopiedId = null;
                if (_grokbotPage.Visibility == Visibility.Visible) PaintGrokbot();
            };
            timer.Start();
        });
    }

    private StackPanel GrokbotBubbleColumn(string text, string footer, bool mine, string copyId, byte[]? image = null)
    {
        var align = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        var body = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            FontSize = DeskTheme.SizeBody,
            FontFamily = DeskTheme.UiFont,
            Foreground = DeskTheme.Text,
            MaxWidth = GrokbotBubbleMaxWidth
        };
        UIElement bubbleChild = body;
        if (mine && image is { Length: > 0 })
        {
            var thumb = GrokbotThumbnail(image);
            if (thumb is not null)
                bubbleChild = new StackPanel { Spacing = DeskTheme.Space8, Children = { thumb, body } };
        }
        var bubble = new Border
        {
            Background = mine ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 36, 36, 42)) : DeskTheme.Surface,
            BorderBrush = DeskTheme.Divider,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(DeskTheme.Space12, DeskTheme.Space8, DeskTheme.Space12, DeskTheme.Space8),
            CornerRadius = new CornerRadius(DeskTheme.Radius10),
            HorizontalAlignment = align,
            MaxWidth = GrokbotBubbleMaxWidth,
            Child = bubbleChild
        };
        var copy = GrokbotCopyIcon(text, copyId);
        var overlay = new Canvas { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        overlay.Children.Add(copy);
        overlay.SizeChanged += (_, _) =>
        {
            Canvas.SetLeft(copy, Math.Max(0, overlay.ActualWidth - copy.Width - DeskTheme.Space4));
            Canvas.SetTop(copy, DeskTheme.Space4);
        };
        var bubbleHost = new Grid { HorizontalAlignment = align };
        bubbleHost.Children.Add(bubble);
        bubbleHost.Children.Add(overlay);
        AttachGrokbotCopyHover(bubbleHost, bubble, copy);
        var meta = new TextBlock
        {
            Text = footer,
            FontSize = DeskTheme.SizeCaption,
            FontFamily = DeskTheme.UiFont,
            Foreground = DeskTheme.Muted,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = GrokbotBubbleMaxWidth,
            HorizontalAlignment = align
        };
        return new StackPanel
        {
            Spacing = DeskTheme.Space4,
            HorizontalAlignment = align,
            MaxWidth = GrokbotBubbleMaxWidth,
            Children = { bubbleHost, meta }
        };
    }

    private Button GrokbotCopyIcon(string text, string id)
    {
        var copied = GrokbotCopyShown(id);
        var shown = copied || _grokbotHoverCopyId == id;
        var button = new Button
        {
            Tag = id,
            Content = new FontIcon
            {
                Glyph = copied ? "\uE73E" : "\uE8C8",
                FontFamily = DeskTheme.IconFont,
                FontSize = DeskTheme.SizeCaption,
                Foreground = DeskTheme.Text,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            },
            Width = 28,
            Height = 28,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(DeskTheme.Radius6),
            Background = DeskTheme.Raised,
            BorderBrush = DeskTheme.Divider,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Opacity = shown ? 1 : 0,
            IsHitTestVisible = shown,
            UseSystemFocusVisuals = true,
            FocusVisualPrimaryBrush = DeskTheme.Accent,
            FocusVisualSecondaryBrush = DeskTheme.Background
        };
        ToolTipService.SetToolTip(button, copied ? "Copied" : "Copy");
        AutomationName(button, copied ? "Copied" : "Copy message");
        button.Click += (_, _) => CopyGrokbotText(text, id);
        return button;
    }

    private bool GrokbotCopyShown(string id) =>
        _grokbotCopiedId == id && DateTimeOffset.UtcNow < _grokbotCopiedUntil;

    private void AttachGrokbotCopyHover(FrameworkElement host, FrameworkElement bubble, Button copy)
    {
        if (copy.Tag is not string id) return;
        void Show()
        {
            _grokbotHoverCopyId = id;
            copy.Opacity = 1;
            copy.IsHitTestVisible = true;
        }
        void Hide()
        {
            if (_grokbotPainting || GrokbotCopyShown(id) || copy.FocusState != FocusState.Unfocused) return;
            if (_grokbotHoverCopyId == id) _grokbotHoverCopyId = null;
            copy.Opacity = 0;
            copy.IsHitTestVisible = false;
        }
        void Exit(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(host).Position;
            if (point.X >= 0 && point.Y >= 0 && point.X <= host.ActualWidth && point.Y <= host.ActualHeight) return;
            Hide();
        }
        host.PointerEntered += (_, _) => Show();
        host.PointerExited += Exit;
        bubble.PointerEntered += (_, _) => Show();
        bubble.PointerExited += Exit;
        copy.PointerEntered += (_, _) => Show();
        copy.PointerExited += Exit;
        copy.GotFocus += (_, _) => Show();
        copy.LostFocus += (_, _) => Hide();
    }

    private Button GrokbotHeaderMenu()
    {
        var menu = new MenuFlyout();
        var copy = new MenuFlyoutItem { Text = "Copy conversation" };
        copy.Click += (_, _) => CopyGrokbotConversation();
        menu.Items.Add(copy);
        var button = new Button
        {
            Content = new FontIcon
            {
                Glyph = "\uE712",
                FontFamily = DeskTheme.IconFont,
                FontSize = DeskTheme.SizeBody,
                Foreground = DeskTheme.Muted,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            },
            Width = 36,
            Height = 36,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            Background = DeskTheme.Transparent,
            BorderBrush = DeskTheme.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(DeskTheme.Radius6),
            Flyout = menu,
            UseSystemFocusVisuals = true,
            FocusVisualPrimaryBrush = DeskTheme.Accent,
            FocusVisualSecondaryBrush = DeskTheme.Background
        };
        AutomationName(button, "Grokbot menu");
        ToolTipService.SetToolTip(button, "Grokbot menu");
        AutomationName(copy, "Copy conversation");
        return button;
    }

    private void CopyGrokbotConversation() => _ = CopyGrokbotConversationAsync();

    private async Task CopyGrokbotConversationAsync()
    {
        try { await ChatClipboard.CopyAsync(DispatcherQueue, GrokbotTranscript.Format(_grokbotMail.Messages)); }
        catch { }
    }

    private Button GrokbotJumpButton()
    {
        var count = new TextBlock
        {
            FontFamily = DeskTheme.UiFont,
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var badge = new Border
        {
            MinWidth = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(255, 196, 32, 38)),
            Padding = new Thickness(4, 0, 4, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            Child = count
        };
        var face = new Grid
        {
            Width = 40,
            Height = 40,
            Children =
            {
                new FontIcon
                {
                    Glyph = "\uE74B",
                    FontFamily = DeskTheme.IconFont,
                    FontSize = DeskTheme.SizeBody,
                    Foreground = DeskTheme.Text,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                },
                badge
            }
        };
        var jump = new Button
        {
            Content = face,
            Width = 40,
            Height = 40,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(20),
            Background = DeskTheme.Raised,
            BorderBrush = DeskTheme.Divider,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, DeskTheme.Space16, DeskTheme.Space16),
            Visibility = Visibility.Collapsed,
            UseSystemFocusVisuals = true,
            FocusVisualPrimaryBrush = DeskTheme.Accent,
            FocusVisualSecondaryBrush = DeskTheme.Background
        };
        AutomationName(jump, "Jump to latest");
        ToolTipService.SetToolTip(jump, "Jump to latest");
        jump.Click += (_, _) => JumpGrokbotToLatest();
        _grokbotJump = jump;
        _grokbotJumpBadge = badge;
        _grokbotJumpCount = count;
        return jump;
    }

    private void OnGrokbotViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (sender is not ScrollViewer scroll || _grokbotHoldScroll || _grokbotPainting) return;
        if (_grokbotJumpAnimating)
        {
            if (e.IsIntermediate) return;
            _grokbotJumpAnimating = false;
        }
        _grokbotJumpState = GrokbotTranscript.OnScroll(_grokbotJumpState, scroll.ExtentHeight, scroll.ViewportHeight, scroll.VerticalOffset);
        ApplyGrokbotJumpChrome(scroll.ExtentHeight, scroll.ViewportHeight);
    }

    private void FollowGrokbotIfStuck()
    {
        if (_grokbotPainting || _grokbotJumpAnimating || _grokbotScroll is null || !_grokbotJumpState.StickToEnd) return;
        var scroll = _grokbotScroll;
        var latest = GrokbotTranscript.LatestOffset(scroll.ExtentHeight, scroll.ViewportHeight);
        if (Math.Abs(scroll.VerticalOffset - latest) <= 1) return;
        _grokbotJumpState = _grokbotJumpState with { Offset = latest, Unread = 0 };
        MoveGrokbotScroll(latest, false);
        ApplyGrokbotJumpChrome(scroll.ExtentHeight, scroll.ViewportHeight);
    }

    private void PlaceGrokbotScroll(int arrived)
    {
        var scroll = _grokbotScroll;
        if (scroll is null) return;
        scroll.UpdateLayout();
        _grokbotJumpState = GrokbotTranscript.OnArrivals(_grokbotJumpState, arrived, scroll.ExtentHeight, scroll.ViewportHeight);
        MoveGrokbotScroll(_grokbotJumpState.Offset, _grokbotJumpAnimating && arrived == 0);
        ApplyGrokbotJumpChrome(scroll.ExtentHeight, scroll.ViewportHeight);
    }

    private void JumpGrokbotToLatest()
    {
        var scroll = _grokbotScroll;
        if (scroll is null) return;
        scroll.UpdateLayout();
        _grokbotJumpState = GrokbotTranscript.OnJump(_grokbotJumpState, scroll.ExtentHeight, scroll.ViewportHeight);
        ApplyGrokbotJumpChrome(scroll.ExtentHeight, scroll.ViewportHeight);
        MoveGrokbotScroll(_grokbotJumpState.Offset, true);
    }

    private void ApplyGrokbotJumpChrome(double extent, double viewport)
    {
        if (_grokbotJump is null) return;
        var visible = GrokbotTranscript.ButtonVisible(_grokbotJumpState, extent, viewport);
        _grokbotJump.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        var unread = _grokbotJumpState.Unread;
        if (_grokbotJumpCount is not null)
            _grokbotJumpCount.Text = unread.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (_grokbotJumpBadge is not null)
            _grokbotJumpBadge.Visibility = visible && unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationName(_grokbotJump, unread > 0
            ? "Jump to latest, " + unread.ToString(System.Globalization.CultureInfo.InvariantCulture) + " unread"
            : "Jump to latest");
    }

    private void MoveGrokbotScroll(double offset, bool animate)
    {
        var scroll = _grokbotScroll;
        if (scroll is null) return;
        DisarmGrokbotScrollRetry();
        if (animate)
        {
            _grokbotJumpAnimating = true;
            scroll.ChangeView(null, offset, null, false);
            return;
        }
        _grokbotJumpAnimating = false;
        var target = scroll.ScrollableHeight > 0 ? Math.Min(offset, scroll.ScrollableHeight) : offset;
        if (scroll.ScrollableHeight > 0 && Math.Abs(scroll.VerticalOffset - target) <= 1) return;
        scroll.ChangeView(null, target, null, true);
        if (target > 1 && (scroll.ScrollableHeight <= 0 || Math.Abs(scroll.VerticalOffset - target) > 1))
            ArmGrokbotScrollRetry(target);
    }

    private void DisarmGrokbotScrollRetry()
    {
        if (_grokbotScrollRetry is null || _grokbotScroll is null) return;
        _grokbotScroll.LayoutUpdated -= _grokbotScrollRetry;
        _grokbotScrollRetry = null;
    }

    private void ArmGrokbotScrollRetry(double offset)
    {
        var scroll = _grokbotScroll;
        if (scroll is null) return;
        DisarmGrokbotScrollRetry();
        EventHandler<object> retry = (_, _) => { };
        retry = (_, _) =>
        {
            if (scroll.ScrollableHeight <= 0 && offset > 1) return;
            scroll.LayoutUpdated -= retry;
            if (_grokbotScrollRetry == retry) _grokbotScrollRetry = null;
            var apply = Math.Min(offset, Math.Max(0, scroll.ScrollableHeight));
            if (Math.Abs(scroll.VerticalOffset - apply) <= 1) return;
            scroll.ChangeView(null, apply, null, true);
        };
        _grokbotScrollRetry = retry;
        scroll.LayoutUpdated += retry;
    }

    private static string PollText(DateTimeOffset? at) =>
        at is DateTimeOffset value ? value.ToLocalTime().ToString("HH:mm") : "none";

    internal void LoadGrokbotSettings(ToggleSwitch tab, ToggleSwitch control, ToggleSwitch screenshots)
    {
        var prefs = _grokbotMail.PreferencesCopy();
        _grokbotUi = true;
        tab.IsOn = prefs.TabEnabled;
        control.IsOn = prefs.AllowControl;
        screenshots.IsOn = prefs.Screenshots;
        _grokbotUi = false;
    }

    internal void SaveGrokbotSettings(bool tab, bool control, bool screenshots)
    {
        if (_grokbotUi) return;
        var prefs = _grokbotMail.PreferencesCopy();
        prefs.TabEnabled = tab;
        prefs.AllowControl = control;
        prefs.Screenshots = screenshots;
        _grokbotMail.SavePreferences(prefs);
        ApplyGrokbotNav();
        RefreshGrokbot();
    }

    internal void QueueGrokbotPing()
    {
        var mail = _grokbotMail.Queue("ping", false);
        RefreshGrokbot();
        ScheduleGrokbotWake(mail);
    }

    private GrokbotWakeDebounce WakeDebounce => _grokbotWake ??= new GrokbotWakeDebounce(DeliverGrokbotWakeAsync);

    private void ScheduleGrokbotWake(GrokbotMail mail)
    {
        if (string.IsNullOrWhiteSpace(_grokbotMail.PreferencesCopy().WakeUrl)) return;
        if (string.IsNullOrEmpty(CurrentGrokbotWakeKey()))
        {
            try { _grokbotMail.NoteWake(mail.Id, "Webhook sender key is not set."); }
            catch (Exception) { }
            RefreshGrokbot();
            return;
        }
        WakeDebounce.Schedule(new GrokbotWakeCall(mail.Id, mail.AskToAct, GrokbotWake.Preview(mail.Text)));
    }

    private async Task DeliverGrokbotWakeAsync(GrokbotWakeCall call, CancellationToken ct)
    {
        var url = (_grokbotMail.PreferencesCopy().WakeUrl ?? "").Trim();
        if (url.Length == 0) return;
        var key = CurrentGrokbotWakeKey();
        if (string.IsNullOrEmpty(key)) return;
        var preview = call.Preview;
        if (preview.Contains(key, StringComparison.Ordinal))
            preview = preview.Replace(key, "[redacted]", StringComparison.Ordinal);
        string note;
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            note = (await GrokbotWake.DeliverAsync(handler, url, key, call.MessageId, call.Act, preview, ct, swarmId: call.SwarmId, taskId: call.TaskId, title: call.Title, details: call.Details, runId: call.RunId).ConfigureAwait(false)).Note;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch { note = "Wake failed."; }
        if (note.Contains(key, StringComparison.Ordinal)) note = "Wake failed.";
        try { _grokbotMail.NoteWake(call.MessageId, note); }
        catch (Exception) { }
        try { DispatcherQueue.TryEnqueue(RefreshGrokbot); }
        catch (Exception) { }
    }

    private void EnableGrokbotDrop(UIElement element)
    {
        element.AllowDrop = true;
        element.DragOver += GrokbotDragOver;
        element.Drop += GrokbotDrop;
    }

    private static void GrokbotDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.Handled = true;
    }

    private async void GrokbotDrop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            e.Handled = true;
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            var files = items.OfType<StorageFile>().ToList();
            if (files.Count == 0)
            {
                _grokbotHint.Text = "wrong type";
                return;
            }
            foreach (var file in files) await AcceptGrokbotFileAsync(file);
        }
        catch { _grokbotHint.Text = "wrong type"; }
        finally { deferral.Complete(); }
    }

    private async Task PickGrokbotImageAsync()
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".webp");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            await AcceptGrokbotFileAsync(file);
        }
        catch { _grokbotHint.Text = "wrong type"; }
    }

    private async Task PasteGrokbotAsync()
    {
        var original = _grokbotInput.Text;
        var start = _grokbotInput.SelectionStart;
        var length = _grokbotInput.SelectionLength;
        bool Unchanged() => _grokbotInput.Text == original && _grokbotInput.SelectionStart == start && _grokbotInput.SelectionLength == length;
        try
        {
            var data = await ChatClipboard.ReadAsync(_grokbotInput.DispatcherQueue);
            if (!Unchanged()) return;
            if (data.Contains(StandardDataFormats.StorageItems))
            {
                var items = await data.GetStorageItemsAsync();
                var files = items.OfType<StorageFile>().ToList();
                if (!Unchanged()) return;
                if (files.Count > 0)
                {
                    foreach (var file in files) await AcceptGrokbotFileAsync(file);
                    return;
                }
            }
            if (data.Contains(StandardDataFormats.Bitmap))
            {
                if (!Unchanged()) return;
                using var stream = await (await data.GetBitmapAsync()).OpenReadAsync();
                var png = await PrepareGrokbotPngAsync(stream, ".png", clipboard: true, (long)stream.Size);
                if (png is null) return;
                KeepGrokbotImage(png);
                return;
            }
            if (!data.Contains(StandardDataFormats.Text)) return;
            var text = await data.GetTextAsync();
            if (!Unchanged()) return;
            _grokbotInput.SelectedText = text;
            var inserted = _grokbotInput.Text.Length - (original.Length - length);
            _grokbotInput.Select(start + inserted, 0);
        }
        catch
        {
            if (_grokbotHint.Text.Length == 0) _grokbotHint.Text = "Clipboard is busy. Try pasting again.";
        }
    }

    private async Task AcceptGrokbotFileAsync(StorageFile file)
    {
        var type = file.FileType;
        var typed = GrokbotImage.Reject(type, false, false, 0, 0, 0, 0);
        if (typed is not null)
        {
            _grokbotHint.Text = typed;
            return;
        }
        var props = await file.GetBasicPropertiesAsync();
        var sourceBytes = (long)props.Size;
        var sized = GrokbotImage.Reject(type, false, false, sourceBytes, 0, 0, 0);
        if (sized is not null)
        {
            _grokbotHint.Text = sized;
            return;
        }
        using var stream = await file.OpenAsync(FileAccessMode.Read);
        var png = await PrepareGrokbotPngAsync(stream, type, clipboard: false, sourceBytes);
        if (png is null) return;
        KeepGrokbotImage(png);
    }

    private async Task<byte[]?> PrepareGrokbotPngAsync(IRandomAccessStream stream, string? type, bool clipboard, long sourceBytes)
    {
        if (clipboard)
        {
            var sized = GrokbotImage.Reject(".png", true, true, sourceBytes, 0, 0, 0);
            if (sized is not null)
            {
                _grokbotHint.Text = sized;
                return null;
            }
        }
        BitmapDecoder decoder;
        try
        {
            stream.Seek(0);
            decoder = await BitmapDecoder.CreateAsync(stream);
        }
        catch
        {
            _grokbotHint.Text = GrokbotImage.Reject(type, clipboard, false, sourceBytes, 0, 0, 0) ?? "wrong type";
            return null;
        }
        var width = (int)decoder.PixelWidth;
        var height = (int)decoder.PixelHeight;
        if (width < 1 || height < 1)
        {
            _grokbotHint.Text = "wrong type";
            return null;
        }
        var beforeScale = clipboard
            ? GrokbotImage.Reject(".png", true, true, sourceBytes, width, height, 0)
            : GrokbotImage.Reject(type, false, false, sourceBytes, width, height, 0);
        if (beforeScale is not null)
        {
            _grokbotHint.Text = beforeScale;
            return null;
        }
        byte[] png;
        try { png = await EncodeGrokbotPngAsync(decoder, width, height); }
        catch
        {
            _grokbotHint.Text = "wrong type";
            return null;
        }
        var encodedAsPng = GrokbotImage.IsPng(png);
        var after = GrokbotImage.Reject(clipboard ? ".png" : type, clipboard, encodedAsPng, sourceBytes, width, height, png.Length);
        if (after is not null)
        {
            _grokbotHint.Text = after;
            return null;
        }
        return png;
    }

    private static async Task<byte[]> EncodeGrokbotPngAsync(BitmapDecoder decoder, int width, int height)
    {
        var scale = GrokbotImage.Scale(width, height);
        var scaledWidth = Math.Max(1u, (uint)(width * scale));
        var scaledHeight = Math.Max(1u, (uint)(height * scale));
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform { ScaledWidth = scaledWidth, ScaledHeight = scaledHeight }, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb);
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, scaledWidth, scaledHeight, 96, 96, pixels.DetachPixelData());
        await encoder.FlushAsync();
        output.Seek(0);
        using var result = new MemoryStream();
        await output.AsStreamForRead().CopyToAsync(result);
        return result.ToArray();
    }

    private void KeepGrokbotImage(byte[] png)
    {
        _grokbotImage = png;
        _grokbotHint.Text = "";
        ShowGrokbotPending();
    }

    private void ShowGrokbotPending()
    {
        if (_grokbotImage is not { Length: > 0 } png)
        {
            _grokbotPendingView.Source = null;
            _grokbotPendingView.Visibility = Visibility.Collapsed;
            return;
        }
        try
        {
            _grokbotPendingView.Source = GrokbotPngSource(png);
            _grokbotPendingView.Visibility = Visibility.Visible;
        }
        catch
        {
            _grokbotPendingView.Source = null;
            _grokbotPendingView.Visibility = Visibility.Collapsed;
        }
    }

    private static Image? GrokbotThumbnail(byte[] png)
    {
        try
        {
            var image = new Image
            {
                Source = GrokbotPngSource(png),
                MaxWidth = GrokbotBubbleMaxWidth,
                MaxHeight = 160,
                Stretch = Stretch.Uniform
            };
            AutomationName(image, "Grokbot image");
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static BitmapImage GrokbotPngSource(byte[] png)
    {
        var stream = new InMemoryRandomAccessStream();
        var write = stream.AsStreamForWrite();
        write.Write(png, 0, png.Length);
        write.Flush();
        stream.Seek(0);
        var bitmap = new BitmapImage();
        bitmap.SetSource(stream);
        return bitmap;
    }
}
