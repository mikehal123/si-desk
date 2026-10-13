using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
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
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DeskTheme.Space12 };
        var headerMark = GrokbotAvatar.Mark(prefs.AvatarColor, prefs.AvatarShape, 36);
        headerMark.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(headerMark);
        var titles = new StackPanel { Spacing = DeskTheme.Space4 };
        titles.Children.Add(Head("Grokbot"));
        titles.Children.Add(_grokbotLink);
        header.Children.Add(titles);
        _grokbotPage.Children.Add(header);
        var scroll = new ScrollViewer { Content = _grokbotMessages, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1);
        _grokbotPage.Children.Add(scroll);
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
        _grokbotInput.BorderThickness = new Thickness(0);
        _grokbotInput.BorderBrush = DeskTheme.Transparent;
        _grokbotInput.Background = DeskTheme.Transparent;
        _grokbotInput.VerticalAlignment = VerticalAlignment.Center;
        var inputGrid = new Grid { ColumnSpacing = DeskTheme.Space8 };
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputGrid.Children.Add(_grokbotInput);
        Grid.SetColumn(send, 1);
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
        var composer = new StackPanel { Spacing = DeskTheme.Space8, Children = { _grokbotHint, composerRow, _grokbotStop } };
        Grid.SetRow(composer, 3);
        _grokbotPage.Children.Add(composer);
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
            var mail = _grokbotMail.Queue(_grokbotInput.Text ?? "", _grokbotAsk.IsOn);
            _grokbotInput.Text = "";
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
        StopGrokbotDotMotion();
        _grokbotMessages.Children.Clear();
        var messages = _grokbotMail.Messages;
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
            _grokbotMessages.Children.Add(GrokbotMine(bar.Text, footer, mail.WakeNote));
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
    }

    private const double GrokbotBubbleMaxWidth = 520;

    private static UIElement GrokbotMine(string text, string footer, string? note)
    {
        var column = GrokbotBubbleColumn(text, footer, true);
        if (!string.IsNullOrWhiteSpace(note))
        {
            column.Children.Add(new TextBlock
            {
                Text = note,
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
        var column = GrokbotBubbleColumn(text, footer, false);
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
        var copied = _grokbotCopiedId == mail.Id && DateTimeOffset.UtcNow < _grokbotCopiedUntil;
        var copy = GrokbotActionButton(copied ? "Copied" : "Copy", false);
        var reply = mail.Reply ?? "";
        var id = mail.Id;
        copy.Click += (_, _) => CopyGrokbotReply(reply, id);
        AutomationName(copy, "Copy Grokbot reply");
        var like = GrokbotActionButton("Like", mail.Liked);
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
            Children = { copy, like }
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

    private void CopyGrokbotReply(string reply, string id)
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

    private static StackPanel GrokbotBubbleColumn(string text, string footer, bool mine)
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
        var bubble = new Border
        {
            Background = mine ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 36, 36, 42)) : DeskTheme.Surface,
            BorderBrush = DeskTheme.Divider,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(DeskTheme.Space12, DeskTheme.Space8, DeskTheme.Space12, DeskTheme.Space8),
            CornerRadius = new CornerRadius(DeskTheme.Radius10),
            HorizontalAlignment = align,
            MaxWidth = GrokbotBubbleMaxWidth,
            Child = body
        };
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
            Children = { bubble, meta }
        };
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
            note = (await GrokbotWake.DeliverAsync(handler, url, key, call.MessageId, call.Act, preview, ct, swarmId: call.SwarmId, taskId: call.TaskId, title: call.Title, details: call.Details).ConfigureAwait(false)).Note;
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
}
