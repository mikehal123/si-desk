using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
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

    internal GrokbotMailbox GrokbotMail => _grokbotMail;

    internal static Grid GrokbotNavMark(double size = 28)
    {
        var blob = new Ellipse { Width = size * 0.78, Height = size * 0.78, Fill = Ink(0x4C, 0x8D, 0xFF), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        var badge = new Grid { Width = size * 0.42, Height = size * 0.42, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
        badge.Children.Add(new Ellipse { Fill = Ink(0xFB, 0x92, 0x3C) });
        badge.Children.Add(new TextBlock { Text = "\u2605", FontSize = size * 0.26, Foreground = Ink(255, 255, 255), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        var grid = new Grid { Width = size, Height = size, Children = { blob, badge } };
        AutomationName(grid, "Grokbot");
        return grid;
    }

    private void BuildGrokbotPage()
    {
        AutomationName(_grokbotInput, "Grokbot message");
        AutomationName(_grokbotAsk, "Ask Grokbot to act");
        AutomationName(_grokbotLink, "Grokbot connection");
        AutomationName(_grokbotControlBanner, "Grokbot is controlling the app");
        _grokbotPage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _grokbotPage.RowDefinitions.Add(new RowDefinition());
        _grokbotPage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _grokbotPage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DeskTheme.Space12 };
        header.Children.Add(GrokbotNavMark(36));
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
            Header = "Grokbot audit",
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = _grokbotAudit
        };
        AutomationName(audit, "Grokbot audit");
        Grid.SetRow(audit, 2);
        _grokbotPage.Children.Add(audit);
        _grokbotStop = DeskButton("Stop Grokbot");
        _grokbotStop.MinHeight = DeskTheme.Space24 + DeskTheme.Space24;
        _grokbotStop.HorizontalAlignment = HorizontalAlignment.Stretch;
        _grokbotStop.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _grokbotStop.Click += (_, _) => ToggleGrokbotStop();
        AutomationName(_grokbotStop, "Stop Grokbot");
        var send = Button("Send", SendGrokbot);
        AutomationName(send, "Send to Grokbot");
        _grokbotInput.PreviewKeyDown += GrokbotInputKeyDown;
        var ask = new StackPanel { Spacing = DeskTheme.Space4, Children = { Note("Ask Grokbot to act"), _grokbotAsk } };
        var composer = new StackPanel { Spacing = DeskTheme.Space8, Children = { _grokbotHint, _grokbotInput, Note("Enter to send · Shift+Enter for a new line"), ask, send, _grokbotStop } };
        Grid.SetRow(composer, 3);
        _grokbotPage.Children.Add(composer);
        _grokbotTimer.Tick += (_, _) =>
        {
            if (_grokbotPage.Visibility == Visibility.Visible) PaintGrokbot();
        };
        _grokbotTimer.Start();
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
        if (_grokbotPage.Visibility == Visibility.Visible) PaintGrokbot();
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
        _grokbotMessages.Children.Clear();
        var messages = _grokbotMail.Messages;
        if (messages.Count == 0)
            _grokbotMessages.Children.Add(Note("No messages yet. Send one here. Grokbot picks it up through the connector."));
        foreach (var mail in messages)
        {
            var status = mail.Status switch
            {
                GrokbotMailStatus.Queued => "Queued",
                GrokbotMailStatus.PickedUp => "Picked up",
                _ => "Answered"
            };
            var bar = GrokTuiPresentation.UserBar(mail.Text, mail.Created.ToLocalTime());
            var caption = status + (mail.AskToAct ? " · Act on" : " · Reply only") + " · " + bar.Time;
            _grokbotMessages.Children.Add(GrokbotBubble(bar.Text, caption, true, mail.WakeNote));
            if (!string.IsNullOrWhiteSpace(mail.Reply))
            {
                var when = mail.AnsweredAt?.ToLocalTime() ?? mail.Created.ToLocalTime();
                var reply = GrokTuiPresentation.UserBar(mail.Reply, when);
                _grokbotMessages.Children.Add(GrokbotBubble(reply.Text, "Grokbot · " + reply.Time, false));
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
            var stopped = _grokbotMail.PreferencesCopy().Stopped;
            _grokbotStop.Content = stopped ? "Resume Grokbot" : "Stop Grokbot";
            AutomationName(_grokbotStop, stopped ? "Resume Grokbot" : "Stop Grokbot");
        }
        if (_grokbotMail.Controlling) _grokbotControlBanner.Visibility = Visibility.Visible;
    }

    private static Border GrokbotBubble(string text, string caption, bool mine, string? note = null)
    {
        var body = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            FontSize = DeskTheme.SizeBody,
            FontFamily = DeskTheme.MonoFont,
            Foreground = DeskTheme.Text
        };
        var meta = new TextBlock { Text = caption, FontSize = DeskTheme.SizeCaption, FontFamily = DeskTheme.UiFont, Foreground = DeskTheme.Muted };
        var stack = new StackPanel { Spacing = DeskTheme.Space4, Children = { meta, body } };
        if (!string.IsNullOrWhiteSpace(note))
            stack.Children.Add(new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, FontSize = DeskTheme.SizeCaption, FontFamily = DeskTheme.UiFont, Foreground = DeskTheme.Muted });
        return new Border
        {
            Background = mine ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 36, 36, 42)) : DeskTheme.Surface,
            Padding = new Thickness(DeskTheme.Space12, DeskTheme.Space8, DeskTheme.Space12, DeskTheme.Space8),
            CornerRadius = new CornerRadius(DeskTheme.Radius6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = stack
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
