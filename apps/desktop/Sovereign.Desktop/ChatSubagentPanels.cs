using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Sovereign.Desktop.Client;
using static Sovereign.Desktop.MainWindow;

namespace Sovereign.Desktop;

internal sealed partial class ChatConversation
{
    private readonly Dictionary<string, HelperChatPanel> _openSubagentPanels = [];
    private readonly HashSet<string> _swarmMounted = new(StringComparer.Ordinal);
    private readonly HashSet<string> _respawnedHelpers = new(StringComparer.Ordinal);
    private readonly Dictionary<StackPanel, FrameworkElement> _swarmAttachPending = new();
    private readonly SubagentCardFold _helperCardFold = new();
    private Grid? _helperDetailHost;
    internal string[] HelperBreadcrumbLabels { get; private set; } = [];
    internal string?[] HelperBreadcrumbIds { get; private set; } = [];

    internal bool HelperCardOpen(string id, string? state) => _helperCardFold.Expanded(id, state);
    internal void RememberHelperCard(string id, bool expanded) => _helperCardFold.Remember(id, expanded);

    private void ApplySubagentChatPanelMode()
    {
        var enabled = _saved.SubagentChatPanelsEnabled;
        foreach (var view in _subagentViews.Values) view.SetOpensPanel(enabled);
        if (enabled) RestoreOpenSubagentPanels();
        else CloseAllSubagentPanels();
    }

    private void HostSwarmChat(CursorSubagent? previous, CursorSubagent child, StackPanel host)
    {
        var created = !_openSubagentPanels.TryGetValue(child.SessionId, out var panel);
        if (created || panel is null)
        {
            panel = new HelperChatPanel(this, child.SessionId);
            _openSubagentPanels[child.SessionId] = panel;
            panel.Show(child);
        }
        else if (previous is not null) panel.Stream(previous, child);
        panel.FitInsideTask();
        AttachSwarmChat(panel.Root, host, waited: false);
        _swarmMounted.Add(child.SessionId);
    }

    private void AttachSwarmChat(FrameworkElement root, StackPanel host, bool waited)
    {
        if (_disposed || ReferenceEquals(root.Parent, host)) return;
        if (!host.IsLoaded || host.XamlRoot is null)
        {
            if (!waited) WaitForSwarmHost(root, host);
            return;
        }
        try
        {
            Detach(root);
            if (root.Parent is not null || host.Children.Contains(root)) return;
            host.Children.Add(root);
        }
        catch (System.Runtime.InteropServices.COMException) when (!waited)
        {
            // A collapsed Swarm tab or task card is not in the live tree yet.
            // Add raises 0x800F1000 ("No installed components were detected") and was escaping the save timer.
            WaitForSwarmHost(root, host);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
        }
    }

    private void WaitForSwarmHost(FrameworkElement root, StackPanel host)
    {
        _swarmAttachPending[host] = root;
        host.Loaded -= SwarmHostLoaded;
        host.Loaded += SwarmHostLoaded;
        host.DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed || !_swarmAttachPending.ContainsKey(host)) return;
            if (!host.IsLoaded || host.XamlRoot is null) return;
            SwarmHostLoaded(host, new RoutedEventArgs());
        });
    }

    private void SwarmHostLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not StackPanel host || !_swarmAttachPending.Remove(host, out var root)) return;
        host.Loaded -= SwarmHostLoaded;
        AttachSwarmChat(root, host, waited: true);
    }

    private void CloseAllSubagentPanels()
    {
        foreach (var id in _openSubagentPanels.Keys.Where(key => !_swarmMounted.Contains(key)).ToArray())
        {
            _openSubagentPanels[id].Release();
            _openSubagentPanels.Remove(id);
        }
        _saved = _saved with { OpenSubagentPanelIds = null };
        if (!_restoring) Save();
        ArrangeSubagentDock();
    }

    private void RestoreOpenSubagentPanels()
    {
        if (!_saved.SubagentChatPanelsEnabled) return;
        foreach (var id in _saved.OpenSubagentPanelIds ?? [])
        {
            if (IsSubagentCardClosed(id) || !_subagentRows.TryGetValue(id, out var child) || _openSubagentPanels.ContainsKey(id)) continue;
            var panel = new HelperChatPanel(this, id) { ReadOnly = true };
            _openSubagentPanels[id] = panel;
            panel.Show(child);
        }
        ArrangeSubagentDock();
    }

    internal void OpenSubagentPanel(string id) => OpenSubagentPanel(id, readOnly: false);

    private void OpenSubagentPanel(string id, bool readOnly)
    {
        if (!_saved.SubagentChatPanelsEnabled || string.IsNullOrWhiteSpace(id) || IsSubagentCardClosed(id) || !_subagentRows.TryGetValue(id, out var child)) return;
        var created = !_openSubagentPanels.TryGetValue(id, out var panel);
        if (created)
        {
            panel = new HelperChatPanel(this, id);
            _openSubagentPanels[id] = panel;
        }
        panel!.ReadOnly = readOnly;
        var ids = SubagentChatPanels.Open(_saved.OpenSubagentPanelIds, id);
        foreach (var extra in _openSubagentPanels.Keys.Where(key => !_swarmMounted.Contains(key) && !ids.Contains(key)).ToArray())
        {
            _openSubagentPanels[extra].Release();
            _openSubagentPanels.Remove(extra);
        }
        _saved = _saved with { OpenSubagentPanelIds = ids.Length == 0 ? null : ids };
        if (!_restoring) Save();
        if (!_openSubagentPanels.TryGetValue(id, out panel))
        {
            ArrangeSubagentDock();
            return;
        }
        if (created) panel.Show(child);
        else panel.RefreshChrome(child);
        ArrangeSubagentDock();
    }

    private void CloseSubagentPanel(string id)
    {
        if (_openSubagentPanels.Remove(id, out var panel)) panel.Release();
        var ids = SubagentChatPanels.Close(_saved.OpenSubagentPanelIds, id);
        _saved = _saved with { OpenSubagentPanelIds = ids.Length == 0 ? null : ids };
        if (!_restoring) Save();
        ArrangeSubagentDock();
    }

    private void StreamOpenSubagentPanel(CursorSubagent? previous, CursorSubagent next)
    {
        if (_openSubagentPanels.TryGetValue(next.SessionId, out var panel)) panel.Stream(previous, next);
    }

    private void RefreshOpenSubagentPanels()
    {
        foreach (var pair in _openSubagentPanels)
            if (_subagentRows.TryGetValue(pair.Key, out var child)) pair.Value.RefreshChrome(child);
    }

    internal void ApplySubagentChrome(bool classic)
    {
        foreach (var panel in _openSubagentPanels.Values) panel.ApplyChrome(classic);
    }

    private string[] KnownOpenIds() =>
        (_saved.OpenSubagentPanelIds ?? []).Where(id => _openSubagentPanels.ContainsKey(id)).ToArray();

    private string[] VisibleSubagentIds() => SubagentChatPanels.Arrange(KnownOpenIds()).Visible;

    internal bool SubagentPanelVisible(string id) => VisibleSubagentIds().Contains(id);
    internal bool SubagentPanelReadOnly(string id) => _openSubagentPanels.TryGetValue(id, out var panel) && panel.ReadOnly;
    internal bool SubagentPanelFollowUpReadOnly(string id) => _openSubagentPanels.TryGetValue(id, out var panel) && panel.FollowUpReadOnly;
    internal string SubagentPanelBlobKey(string id) => _openSubagentPanels.TryGetValue(id, out var panel) ? panel.BlobKey : "";
    internal int SubagentPanelVisibleCount => VisibleSubagentIds().Length;
    internal int SubagentPanelTabCount => SubagentChatPanels.Arrange(KnownOpenIds()).Tabs.Length;

    private string SubagentPanelTranscript(string id)
    {
        if (!_openSubagentPanels.TryGetValue(id, out var panel)) return "";
        var lines = new List<string>();
        CollectPanelText(panel.Root, lines);
        return string.Join("\n", lines);
    }

    private static void CollectPanelText(UIElement element, List<string> lines)
    {
        switch (element)
        {
            case TextBlock block:
                if (block.Text.Length > 0) lines.Add(block.Text);
                return;
            case Panel panel:
                foreach (var child in panel.Children) CollectPanelText(child, lines);
                return;
            case Border border when border.Child is not null:
                CollectPanelText(border.Child, lines);
                return;
            case ContentControl control when control.Content is UIElement content:
                CollectPanelText(content, lines);
                return;
            case ScrollViewer scroll when scroll.Content is UIElement scrolled:
                CollectPanelText(scrolled, lines);
                return;
        }
    }

    private void ArrangeSubagentDock()
    {
        if (_helperDetailHost is null) return;
        foreach (var pair in _openSubagentPanels)
            if (!_swarmMounted.Contains(pair.Key)) Detach(pair.Value.Root);
        _helperDetailHost.Children.Clear();
        _helperDetailHost.RowDefinitions.Clear();
        var arrangement = SubagentChatPanels.Arrange(KnownOpenIds());
        if (arrangement.Visible.Length == 0)
        {
            HelperBreadcrumbLabels = [];
            HelperBreadcrumbIds = [];
            SyncHelperHost();
            return;
        }
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in _subagentRows)
            names[pair.Key] = string.IsNullOrWhiteSpace(pair.Value.Name) ? "Subagent" : pair.Value.Name.Trim();
        var crumbs = SubagentChatPanels.Breadcrumb(arrangement.Visible[0], names);
        HelperBreadcrumbIds = crumbs.Select(crumb => crumb.Id).ToArray();
        HelperBreadcrumbLabels = crumbs.Select(crumb => crumb.Label).ToArray();
        var row = 0;
        _helperDetailHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var trail = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DeskTheme.Space4, Margin = new Thickness(0, 0, 0, DeskTheme.Space8) };
        for (var i = 0; i < crumbs.Length; i++)
        {
            if (i > 0)
                trail.Children.Add(new TextBlock { Text = "›", FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeCaption, Foreground = DeskTheme.Muted, VerticalAlignment = VerticalAlignment.Center });
            var crumb = crumbs[i];
            var index = i;
            var current = i == crumbs.Length - 1;
            var button = new Button
            {
                Content = new TextBlock { Text = crumb.Label, FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeCaption, Foreground = current ? DeskTheme.Text : DeskTheme.Muted, MaxWidth = 160, TextTrimming = TextTrimming.CharacterEllipsis },
                Padding = new Thickness(DeskTheme.Space8, DeskTheme.Space4, DeskTheme.Space8, DeskTheme.Space4), BorderThickness = new Thickness(0),
                FontFamily = DeskTheme.UiFont, CornerRadius = new CornerRadius(DeskTheme.Radius6),
                Background = current ? DeskTheme.Pressed : DeskTheme.Transparent
            };
            ToolTipService.SetToolTip(button, crumb.Id is null ? "Main chat" : crumb.Label);
            AutomationName(button, crumb.Id is null ? "Root navigation" : "Open helper " + crumb.Label);
            button.Click += (_, _) => ActivateHelperBreadcrumb(index);
            trail.Children.Add(button);
        }
        var trailStrip = new ScrollViewer
        {
            Content = trail, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollMode = ScrollMode.Disabled
        };
        _helperDetailHost.Children.Add(trailStrip);
        row = 1;
        if (arrangement.Tabs.Length > 0)
        {
            _helperDetailHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DeskTheme.Space4, Margin = new Thickness(0, 0, 0, DeskTheme.Space8) };
            foreach (var id in arrangement.Tabs)
            {
                if (!_openSubagentPanels.TryGetValue(id, out var tabPanel)) continue;
                var captured = id;
                var visible = arrangement.Visible.Contains(id);
                var tab = new Button
                {
                    Content = new TextBlock { Text = tabPanel.Title, FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeCaption, Foreground = visible ? DeskTheme.Text : DeskTheme.Muted, MaxWidth = 160, TextTrimming = TextTrimming.CharacterEllipsis },
                    Padding = new Thickness(DeskTheme.Space8, DeskTheme.Space4, DeskTheme.Space8, DeskTheme.Space4), BorderThickness = new Thickness(0),
                    FontFamily = DeskTheme.UiFont, CornerRadius = new CornerRadius(DeskTheme.Radius6),
                    Background = visible ? DeskTheme.Pressed : DeskTheme.Raised
                };
                AutomationName(tab, "Show helper chat " + tabPanel.Title);
                tab.Click += (_, _) => OpenSubagentPanel(captured);
                tabs.Children.Add(tab);
            }
            var strip = new ScrollViewer
            {
                Content = tabs, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollMode = ScrollMode.Disabled
            };
            _helperDetailHost.Children.Add(strip);
            Grid.SetRow(strip, row);
            row++;
        }
        _helperDetailHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var selected = _openSubagentPanels[arrangement.Visible[0]];
        columns.Children.Add(selected.Root);
        Grid.SetRow(columns, row);
        _helperDetailHost.Children.Add(columns);
        ShowAgents(true);
        SetChangesOpen(true);
        SyncHelperHost();
    }

    internal void ActivateHelperBreadcrumb(int index)
    {
        if ((uint)index >= (uint)HelperBreadcrumbIds.Length) return;
        var id = HelperBreadcrumbIds[index];
        if (id is null) ReturnToMainChat();
        else OpenSubagentPanel(id);
    }

    private void ReturnToMainChat()
    {
        CloseAllSubagentPanels();
        _chatInput.Focus(FocusState.Programmatic);
    }

    private void SyncHelperHost()
    {
        var open = _showAgents && _helperDetailHost is { Children.Count: > 0 };
        if (_helperDetailHost is not null)
            _helperDetailHost.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (_helperSidePane is null) return;
        var split = !_showAgents || open;
        if (_changeFilesScroll is not null) Grid.SetRowSpan(_changeFilesScroll, split ? 1 : 3);
        _helperHeightSplitter.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
        _helperHeightSplitter.IsTabStop = split && _changesOpen;
        _helperSidePane.RowDefinitions[2].Height = new GridLength(split ? DeskSplit.Grip : 0);
        var fraction = ChatPanelLayout.ListFraction(HelperSplitHeight(), _saved.InspectorListFraction, open);
        _helperSidePane.RowDefinitions[1].Height = new GridLength(split ? fraction : 1, GridUnitType.Star);
        _helperSidePane.RowDefinitions[3].Height = new GridLength(split ? 1 - fraction : 0, GridUnitType.Star);
    }

    private double HelperSplitHeight() => _helperSidePane is null ? 0 : Math.Max(0,
        _helperSidePane.ActualHeight - _helperSidePane.RowDefinitions[0].ActualHeight - DeskSplit.Grip - _helperSidePane.RowSpacing * 3);

    private void ResizeHelperList(double delta)
    {
        var height = HelperSplitHeight();
        if (_helperSidePane is null || height <= 0 || !double.IsFinite(delta)) return;
        _saved = _saved with { InspectorListFraction = ChatPanelLayout.ListFraction(height,
            (_helperSidePane.RowDefinitions[1].ActualHeight + delta) / height, _showAgents) };
        SyncHelperHost();
    }

    private sealed class HelperChatPanel
    {
        private readonly ChatConversation _owner;
        private readonly string _id;
        private readonly SubagentBlobFace _face = new();
        private readonly TextBlock _name = new() { FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeBody, LineHeight = 20, Foreground = DeskTheme.Text, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
        private readonly TextBlock _model = new() { FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeCaption, LineHeight = DeskTheme.Space16, Foreground = DeskTheme.Muted, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
        private readonly TextBlock _skills = new() { FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeCaption, LineHeight = DeskTheme.Space16, Foreground = DeskTheme.Muted, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        private readonly TextBlock _status = new() { FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeCaption, LineHeight = DeskTheme.Space16, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
        private readonly TextBlock _elapsed = new() { FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeCaption, LineHeight = DeskTheme.Space16, Foreground = DeskTheme.Muted, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, HorizontalAlignment = HorizontalAlignment.Right };
        private readonly TextBlock _empty = SubagentCaption("No response yet.");
        private readonly Border _statusChip;
        private readonly Button _stop;
        private readonly Button _respawn;
        private readonly Button _close;
        private readonly TextBox _followUp;
        private readonly StackPanel _transcript = new() { Spacing = DeskTheme.Space8 };
        private ScrollViewer? _transcriptScroll;
        private readonly Border _activityHost = new();
        private readonly StackPanel _edits = new() { Spacing = DeskTheme.Space4 };
        private readonly Expander _card;
        private readonly UIElement _body;
        private ChatActivityView? _view;
        private CursorToolActivity[] _tools = [];
        private string[] _changePaths = [];
        private string _state = "";

        internal Border Root { get; }
        internal bool BodyExpanded => _card.IsExpanded;
        internal string TitleText => _name.Text;
        internal string StatusLine => _status.Text;
        internal bool ReadOnly { get; set; }
        internal bool FollowUpReadOnly => _followUp.IsReadOnly;
        internal string BlobKey => _face.Key;
        internal string Title => _name.Text.Length == 0 ? "Subagent" : _name.Text;

        internal HelperChatPanel(ChatConversation owner, string id)
        {
            _owner = owner;
            _id = id;
            _stop = new Button
            {
                Content = "\u00D7", FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeHeading,
                Width = DeskTheme.Space24, Height = DeskTheme.Space24, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
                BorderThickness = new Thickness(0), Background = DeskTheme.Transparent, Foreground = DeskTheme.Error,
                CornerRadius = new CornerRadius(DeskTheme.Radius6), Visibility = Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
            };
            _stop.Visibility = Visibility.Collapsed;
            _stop.IsHitTestVisible = false;
            AutomationName(_stop, "Hidden");
            _respawn = new Button
            {
                Content = "Respawn", FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeBody,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                MinWidth = 88, MinHeight = 32, Padding = new Thickness(DeskTheme.Space12, DeskTheme.Space4, DeskTheme.Space12, DeskTheme.Space4),
                Background = DeskTheme.Raised, Foreground = DeskTheme.Text, BorderBrush = DeskTheme.Text,
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(DeskTheme.Radius6),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            ToolTipService.SetToolTip(_respawn, "Stops only this helper and restarts its task once with a narrower scope. A second use fails this task. The main agent and other workers keep running.");
            AutomationName(_respawn, "Respawn this swarm worker");
            _respawn.Click += (_, _) => _owner.RespawnSwarmHelper(_id);
            _close = new Button
            {
                Content = "\u00D7", FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeHeading,
                Width = DeskTheme.Space24, Height = DeskTheme.Space24, Padding = new Thickness(0),
                BorderThickness = new Thickness(0), Background = DeskTheme.Transparent, Foreground = DeskTheme.Muted,
                CornerRadius = new CornerRadius(DeskTheme.Radius6), VerticalAlignment = VerticalAlignment.Top
            };
            AutomationName(_close, "Hide helper chat");
            ToolTipService.SetToolTip(_close, "Hide this helper chat. Does not stop the agent.");
            _close.Click += (_, _) => _owner.CloseSubagentPanel(_id);
            _followUp = new TextBox
            {
                IsReadOnly = !SubagentChatPanels.FollowUpEnabled,
                PlaceholderText = "Direct reply to this helper is not available.",
                TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, BorderThickness = new Thickness(0),
                Background = DeskTheme.Raised, Foreground = DeskTheme.Text,
                Padding = new Thickness(DeskTheme.Space8), FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeBody,
                CornerRadius = new CornerRadius(DeskTheme.Radius6), MinHeight = 36
            };
            AutomationName(_followUp, "Follow-up to this helper");
            var throughMain = Button("Send through main agent", () => _owner.OfferMainAgentCorrection(_name.Text, _followUp.Text));
            throughMain.FontFamily = DeskTheme.UiFont;
            throughMain.FontSize = DeskTheme.SizeCaption;
            throughMain.Foreground = DeskTheme.Text;
            throughMain.Background = DeskTheme.Raised;
            throughMain.BorderBrush = DeskTheme.Divider;
            throughMain.BorderThickness = new Thickness(1);
            throughMain.CornerRadius = new CornerRadius(DeskTheme.Radius6);
            throughMain.Padding = new Thickness(DeskTheme.Space8, DeskTheme.Space4, DeskTheme.Space8, DeskTheme.Space4);
            throughMain.HorizontalAlignment = HorizontalAlignment.Left;
            AutomationName(throughMain, "Send this correction through the main agent");
            ToolTipService.SetToolTip(throughMain, "Puts a correction in the main composer. It is not sent until you press Send, and it is not sent to this helper directly.");
            KeepClick(_stop);
            KeepClick(_respawn);
            KeepClick(_close);
            _face.Root.VerticalAlignment = VerticalAlignment.Top;
            _statusChip = SubagentStatusChip(_status);
            var titles = new StackPanel { Spacing = DeskTheme.Space4, VerticalAlignment = VerticalAlignment.Top, Children = { _name, _model, _skills } };
            var meta = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Children = { _statusChip } };
            var header = new Grid { ColumnSpacing = DeskTheme.Space8, RowSpacing = DeskTheme.Space4 };
            header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(_face.Root);
            Grid.SetColumn(titles, 1); header.Children.Add(titles);
            Grid.SetColumn(meta, 2); header.Children.Add(meta);
            Grid.SetColumn(_respawn, 3); header.Children.Add(_respawn);
            Grid.SetColumn(_close, 3); header.Children.Add(_close);
            _transcript.Children.Add(new Border());
            _transcript.Children.Add(_activityHost);
            _transcript.Children.Add(_empty);
            _transcript.Children.Add(_edits);
            _transcriptScroll = new ScrollViewer
            {
                Content = _transcript, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            var follow = new StackPanel { Spacing = DeskTheme.Space8, Children = { _followUp, throughMain } };
            var body = new Grid { RowSpacing = DeskTheme.Space8, Margin = new Thickness(DeskTheme.Space12, DeskTheme.Space4, DeskTheme.Space12, DeskTheme.Space12) };
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.Children.Add(_transcriptScroll);
            Grid.SetRow(follow, 1); body.Children.Add(follow);
            _body = body;
            _card = SubagentExpander(SubagentHeaderChrome(header), body, false);
            _card.Expanding += (_, _) => { _owner.RememberHelperCard(_id, true); NameFold(true); };
            _card.Collapsed += (_, _) => { _owner.RememberHelperCard(_id, false); NameFold(false); };
            _card.PreviewKeyDown += (_, e) =>
            {
                if (e.OriginalSource is TextBox || e.OriginalSource is Microsoft.UI.Xaml.Controls.Button) return;
                if (!ApplyKey(e.Key.ToString())) return;
                e.Handled = true;
            };
            Root = new Border
            {
                Background = DeskTheme.Surface, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(DeskTheme.Radius10),
                Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Top, Child = _card
            };
            ApplyChrome(_owner.ClassicChat);
        }

        internal string ChevronMark()
        {
            _card.ApplyTemplate();
            _card.UpdateLayout();
            return ExpanderChevronMark(_card);
        }

        internal ToggleButton HeaderControl()
        {
            _card.ApplyTemplate();
            _card.UpdateLayout();
            return FindHeaderToggle(_card) ?? throw new IOException("Helper card header toggle is missing.");
        }

        internal void ToggleHeader()
        {
            var toggle = HeaderControl();
            var next = !_card.IsExpanded;
            toggle.IsChecked = next;
            if (_card.IsExpanded != next) _card.IsExpanded = next;
        }

        internal bool ApplyKey(string key)
        {
            if (!SubagentChatPanels.IsToggleKey(key)) return false;
            _card.IsExpanded = !_card.IsExpanded;
            return true;
        }

        internal bool ShowsBody(string snippet)
        {
            var found = false;
            Walk(_body, node =>
            {
                if (node is TextBlock block && block.Text.Contains(snippet, StringComparison.Ordinal) && Shown(block)) found = true;
                if (node is ContentControl control && control.Content as string is { } text && text.Contains(snippet, StringComparison.Ordinal) && Shown(control)) found = true;
            });
            return found;
        }

        internal void InvokeClose()
        {
            var peer = new ButtonAutomationPeer(_close);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
        }

        internal void ApplyChrome(bool classic)
        {
            foreach (var block in new TextBlock[] { _name, _model, _skills, _status, _elapsed, _empty }) block.FontFamily = DeskTheme.UiFont;
            _followUp.FontFamily = DeskTheme.UiFont;
            Root.Background = DeskTheme.Surface;
            Root.BorderThickness = new Thickness(0);
            Root.CornerRadius = new CornerRadius(DeskTheme.Radius10);
        }

        internal void FitInsideTask()
        {
            _card.IsExpanded = true;
            _close.Visibility = Visibility.Collapsed;
            _stop.Visibility = Visibility.Collapsed;
            _respawn.Visibility = Visibility.Visible;
            if (_transcriptScroll is not null)
            {
                _transcriptScroll.Height = 180;
                _transcriptScroll.MaxHeight = 220;
            }
        }

        internal void Show(CursorSubagent child)
        {
            _tools = [];
            _transcript.Children.RemoveAt(0);
            _transcript.Children.Insert(0, _owner.UserMessage(SubagentCardText.TaskLine(child)));
            var previous = _view;
            _view = new ChatActivityView(SubagentCardText.CardLine(child), _ => { }, () => { }, _ => { }, () => { }, classicChat: _owner.ClassicChat);
            _view.BindLineReview(() => _owner.OpenLineReview(_view, "subagent:" + _id, -1, null), () => _owner.LineReviewOn);
            _activityHost.Child = _view.Root;
            _view.Root.Margin = new Thickness(0, DeskTheme.Space4, 0, DeskTheme.Space8);
            previous?.Dispose();
            Feed(SubagentChatPanels.Diff(null, child));
            RefreshChrome(child);
        }

        internal void Stream(CursorSubagent? previous, CursorSubagent next)
        {
            var patch = SubagentChatPanels.Diff(previous, next);
            if (_view is null || previous is null || patch.Reset)
            {
                Show(next);
                return;
            }
            Feed(patch);
            RefreshChrome(next);
        }

        internal void RefreshChrome(CursorSubagent child)
        {
            _face.Apply(child.SessionId, child.Name);
            _face.SetLife(child.State);
            _name.Text = string.IsNullOrWhiteSpace(child.Name) ? "Subagent" : child.Name.Trim();
            _model.Text = SubagentCardText.CardLine(child);
            SetSkills(_owner.SkillCaption(_id));
            _state = child.State ?? "";
            _status.Text = SubagentStatusLabel(child.State);
            _status.Foreground = DeskTheme.StatusForeground(child.State);
            _statusChip.Background = DeskTheme.StatusSurface(child.State);
            ToolTipService.SetToolTip(_statusChip, "Status: " + child.State + " · " + SubagentCardText.LifecycleLine(child));
            ToolTipService.SetToolTip(_model, SubagentCardText.IdentityDetail(child));
            _elapsed.Text = SubagentChatPanels.Elapsed(child);
            _status.Text = SubagentStatusLabel(child.State) + " · " + _elapsed.Text;
            _empty.Text = SubagentActive(child) ? "No response yet." : "No response recorded.";
            _empty.Visibility = string.IsNullOrWhiteSpace(child.Output) ? Visibility.Visible : Visibility.Collapsed;
            _stop.Visibility = Visibility.Collapsed;
            var swarmCard = _owner.IsSwarmHelper(_id);
            _respawn.Visibility = swarmCard ? Visibility.Visible : Visibility.Collapsed;
            _close.Visibility = swarmCard ? Visibility.Collapsed : Visibility.Visible;
            AutomationName(_respawn, "Respawn swarm worker " + _name.Text);
            _followUp.IsReadOnly = !SubagentChatPanels.FollowUpEnabled;
            AutomationName(Root, "Helper chat " + _name.Text);
            AutomationName(_close, "Close helper chat " + _name.Text);
            var open = _owner.HelperCardOpen(_id, _state);
            if (_card.IsExpanded != open) _card.IsExpanded = open;
            NameFold(_card.IsExpanded);
        }

        private void NameFold(bool expanded) =>
            AutomationName(_card, (expanded ? "Collapse" : "Expand") + " helper " + (_name.Text.Length == 0 ? "Subagent" : _name.Text) + ", " + _status.Text);

        private static void KeepClick(Button button) =>
            button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) => e.Handled = true), true);

        private static void Walk(DependencyObject node, Action<DependencyObject> visit)
        {
            visit(node);
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++) Walk(VisualTreeHelper.GetChild(node, i), visit);
        }

        private static bool Shown(UIElement element)
        {
            for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
                if (node is UIElement ui && ui.Visibility != Visibility.Visible) return false;
            return true;
        }

        internal void SetSkills(string caption)
        {
            _skills.Text = caption;
            _skills.Visibility = caption.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        internal void Release()
        {
            _face.SetLife("idle");
            _view?.Dispose();
            _view = null;
            if (Root.Parent is Panel parent) parent.Children.Remove(Root);
        }

        private void Feed(SubagentChatPanels.StreamPatch patch)
        {
            if (_view is null) return;
            if (patch.ReasoningAppend is { Length: > 0 } reasoning)
                _view.Observe(new CursorActivityEvent("reasoning", reasoning));
            foreach (var tool in patch.Tools)
            {
                if (_tools.Any(prior => SameTool(prior, tool))) continue;
                _view.Observe(new CursorActivityEvent("tool", Tool: tool));
            }
            _tools = patch.Tools;
            if (patch.AnswerAppend is { Length: > 0 } answer)
                _view.Append(answer);
            _view.FlushForVerification();
            _changePaths = patch.ChangePaths;
            RenderEdits();
            if (patch.Terminal) _view.Finish(patch.Outcome);
        }

        internal void ApplyLineReview()
        {
            _view?.RefreshLineReviewOffer();
            RenderEdits();
        }

        private void RenderEdits()
        {
            _edits.Children.Clear();
            foreach (var path in _changePaths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DeskTheme.Space8 };
                row.Children.Add(new TextBlock
                {
                    Text = "Edited " + path, FontFamily = DeskTheme.MonoFont, FontSize = DeskTheme.SizeCode, TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true, Foreground = DeskTheme.Muted, VerticalAlignment = VerticalAlignment.Center
                });
                if (_owner.LineReviewOn && _view is not null)
                {
                    var file = path;
                    var review = new Button
                    {
                        Content = "Review", FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeCaption,
                        Padding = new Thickness(DeskTheme.Space8, DeskTheme.Space4, DeskTheme.Space8, DeskTheme.Space4),
                        BorderThickness = new Thickness(0), Background = DeskTheme.Transparent, Foreground = DeskTheme.Muted,
                        CornerRadius = new CornerRadius(DeskTheme.Radius6)
                    };
                    AutomationName(review, "Review file " + file);
                    review.Click += (_, _) => _owner.OpenLineReview(_view, "subagent:" + _id, -1, file);
                    row.Children.Add(review);
                }
                _edits.Children.Add(row);
            }
        }

        private static bool SameTool(CursorToolActivity left, CursorToolActivity right) =>
            left.Id == right.Id && left.Title == right.Title && left.Kind == right.Kind && left.Status == right.Status
            && left.Input == right.Input && left.Output == right.Output && left.Diagnostics == right.Diagnostics
            && (left.Changes ?? []).SequenceEqual(right.Changes ?? []);
    }
}
