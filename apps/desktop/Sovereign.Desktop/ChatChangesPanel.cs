using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Sovereign.Desktop.Client;
using static Sovereign.Desktop.MainWindow;

namespace Sovereign.Desktop;

internal sealed partial class ChatConversation
{
    private readonly Button _changesToggle = new() { Content = "Changes · 0" };
    private readonly Border _changesPanel = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel _changeFiles = new() { Spacing = DeskTheme.Space4 };
    private readonly StackPanel _changeDetail = new() { Spacing = DeskTheme.Space8 };
    private readonly List<ChatActivityView> _changeTurns = [];
    private readonly Dictionary<ChatActivityView, CursorFileChange[]> _changeSnapshots = [];
    private readonly Dictionary<ChatActivityView, (int Count, bool Truncated)> _changeReportedCounts = [];
    private readonly ColumnDefinition _changesColumn = new() { Width = new GridLength(0) };
    private readonly ColumnDefinition _splitterColumn = new() { Width = new GridLength(0) };
    private readonly SplitterBar _rightSplitter = new();
    private double _rightWidth = DeskSplit.RightDefault;
    private Grid? _helperSidePane;
    private readonly SplitterBar _helperHeightSplitter = new(horizontal: true) { Visibility = Visibility.Collapsed };
    private readonly Button _changesPanelTab = new() { Content = "Changes" };
    private readonly Button _agentsPanelTab = new() { Content = "Subagents" };
    private bool _changesOpen;
    private ScrollViewer? _changeFilesScroll, _changeDetailScroll;

    private void BuildChangesPanel()
    {
        ConfigureTeamOption();
        _rightWidth = DeskLayoutStore.Load(_profileDirectory).RightPanel;
        _chatPage.ColumnDefinitions.Add(new ColumnDefinition());
        _chatPage.ColumnDefinitions.Add(_splitterColumn);
        _chatPage.ColumnDefinitions.Add(_changesColumn);
        AutomationName(_rightSplitter, "Resize changes and subagents panel");
        _rightSplitter.DragDelta += delta =>
        {
            var current = _changesColumn.Width.Value > 0 ? _changesColumn.Width.Value : _rightWidth;
            _rightWidth = DeskSplit.DragRight(current, delta, _chatPage.ActualWidth);
            ShowRightColumn(_rightWidth);
        };
        _rightSplitter.DragCompleted += () => DeskLayoutStore.SaveRight(_profileDirectory, _rightWidth);
        _rightSplitter.ResetWidth += () =>
        {
            _rightWidth = DeskSplit.RightDefault;
            ShowRightColumn(DeskSplit.ClampRight(_rightWidth, _chatPage.ActualWidth));
            DeskLayoutStore.SaveRight(_profileDirectory, _rightWidth);
        };
        _rightSplitter.Nudge += arrow =>
        {
            var current = _changesColumn.Width.Value > 0 ? _changesColumn.Width.Value : _rightWidth;
            _rightWidth = DeskSplit.NudgeRight(current, arrow, _chatPage.ActualWidth);
            ShowRightColumn(_rightWidth);
            DeskLayoutStore.SaveRight(_profileDirectory, _rightWidth);
        };
        Grid.SetColumn(_rightSplitter, 1);
        Grid.SetRowSpan(_rightSplitter, _chatPage.RowDefinitions.Count);
        _chatPage.Children.Add(_rightSplitter);
        var pane = _helperSidePane = new Grid { RowSpacing = DeskTheme.Space12, Padding = new Thickness(DeskTheme.Space16, 0, 0, 0) };
        pane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        pane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });
        pane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        var title = new Grid();
        title.ColumnDefinitions.Add(new ColumnDefinition());
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _changesPanelTab.Click += (_, _) => ShowAgents(false);
        _agentsPanelTab.Click += (_, _) => ShowAgents(true);
        _swarmPanelTab.Click += (_, _) => ShowSwarmTab();
        AutomationName(_swarmPanelTab, "Swarm panel");
        title.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = DeskTheme.Space8, Children = { _changesPanelTab, _agentsPanelTab, _swarmPanelTab } });
        var close = Button("Close", () => SetChangesOpen(false));
        AutomationName(close, "Close changes or subagents panel");
        Grid.SetColumn(close, 1); title.Children.Add(close); pane.Children.Add(title);
        _changeFiles.Children.Add(Note("No completed file edits reported in this conversation."));
        var files = _changeFilesScroll = new ScrollViewer { Content = new StackPanel { Children = { _changeFiles, _subagentList } },
            HorizontalContentAlignment = HorizontalAlignment.Stretch, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled };
        Grid.SetRow(files, 1); pane.Children.Add(files);
        var swarmHost = BuildSwarmHost();
        Grid.SetRow(swarmHost, 1); pane.Children.Add(swarmHost);
        _changeDetail.Children.Add(Note("Select a file to inspect its reported before and after text."));
        var detail = _changeDetailScroll = new ScrollViewer { Content = _changeDetail, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled };
        Grid.SetRow(detail, 3); pane.Children.Add(detail);
        _helperDetailHost = new Grid { Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Stretch };
        Grid.SetRow(_helperDetailHost, 3); pane.Children.Add(_helperDetailHost);
        AutomationName(_helperHeightSplitter, "Resize helper chat and file detail height");
        ToolTipService.SetToolTip(_helperHeightSplitter, "Drag up or down. Arrow keys resize; double-click resets.");
        Grid.SetRow(_helperHeightSplitter, 2); pane.Children.Add(_helperHeightSplitter);
        _helperHeightSplitter.DragDelta += delta => ResizeHelperList(delta);
        _helperHeightSplitter.DragCompleted += () => Save();
        _helperHeightSplitter.Nudge += arrow => { ResizeHelperList(arrow * DeskSplit.Nudge); Save(); };
        _helperHeightSplitter.ResetWidth += () => { _saved = _saved with { InspectorListFraction = null }; SyncHelperHost(); Save(); };
        pane.SizeChanged += (_, _) => SyncHelperHost();
        _subagentList.Children.Add(BuildSubagentHeader());
        _subagentList.Children.Add(_subagentEmpty);
        RestoreSubagents();
        AttachSwarmWatch();
        _changesPanel.Child = pane;
        _changesPanel.BorderBrush = DeskTheme.Hover; _changesPanel.BorderThickness = new Thickness(1, 0, 0, 0);
        _changesPanel.Margin = new Thickness(DeskTheme.Space16, 0, 0, 0);
        Grid.SetColumn(_changesPanel, 2); Grid.SetRowSpan(_changesPanel, _chatPage.RowDefinitions.Count); _chatPage.Children.Add(_changesPanel);
        _changesToggle.Click += (_, _) => {
            if (_changesOpen && !_showAgents) SetChangesOpen(false);
            else { ShowAgents(false); SetChangesOpen(true); }
        };
        _subagentsToggle.Click += (_, _) => ToggleSubagentsPanel();
        AutomationName(_changesToggle, "Show or hide changes panel");
        UpdateSubagentCounts();
        if (_helperDetailHost is { Children.Count: > 0 })
        {
            ShowAgents(true);
            SetChangesOpen(true);
        }
        else ShowAgents(false);
        _chatPage.SizeChanged += (_, _) => ResizeChangesPanel();
    }

    private void SetChangesOpen(bool open)
    {
        if (!open && _showAgents) _subagentsShownForTurn = true;
        _changesOpen = open;
        _changesPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        RefreshSubagentElapsed();
        ResizeChangesPanel();
    }

    private void ResizeChangesPanel()
    {
        // Keep both panes usable; a narrow inspector never covers the main conversation.
        var width = Math.Max(0, _chatPage.ActualWidth - _chatPage.Padding.Left - _chatPage.Padding.Right);
        _chatPage.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        _chatPage.ColumnDefinitions[0].MinWidth = 0;
        ShowRightColumn(_changesOpen ? ChatPanelLayout.Width(width, _rightWidth) : 0);
        Grid.SetColumn(_changesPanel, 2); Grid.SetColumnSpan(_changesPanel, 1);
        Grid.SetRow(_changesPanel, 0); Grid.SetRowSpan(_changesPanel, _chatPage.RowDefinitions.Count);
        _changesPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
        _changesPanel.VerticalAlignment = VerticalAlignment.Stretch;
        _changesPanel.ClearValue(FrameworkElement.WidthProperty);
        _changesPanel.Margin = new Thickness(0);
        _changesPanel.Background = DeskTheme.Background;
        _chatTitleActions.Orientation = Orientation.Horizontal;
        RefreshChatHeaderTabs();
    }

    private void ShowRightColumn(double width)
    {
        var open = width > 0;
        _changesColumn.Width = new GridLength(open ? width : 0);
        _splitterColumn.Width = new GridLength(open ? DeskSplit.Grip : 0);
        _rightSplitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        _rightSplitter.IsTabStop = open;
    }

    private void UpdateChangesPanel(ChatActivityView turn)
    {
        var changes = turn.Changes.ToArray();
        var reported = (Count: turn.ReportedChangedFiles, Truncated: turn.ChangeDetailsTruncated);
        if (_changeSnapshots.TryGetValue(turn, out var previous) && previous.SequenceEqual(changes)
            && _changeReportedCounts.GetValueOrDefault(turn) == reported) return;
        if (!_changeSnapshots.ContainsKey(turn)) _changeTurns.Add(turn);
        _changeSnapshots[turn] = changes;
        _changeReportedCounts[turn] = reported;
        var count = _changeReportedCounts.Values.Sum(item => item.Count);
        _changesToggle.Content = $"Changes · {count}";
        RefreshChatHeaderTabs();
        RenderChangeFiles();
    }

    private void RenderChangeFiles()
    {
        var count = _changeReportedCounts.Values.Sum(item => item.Count);
        _changeFiles.Children.Clear();
        if (count == 0) _changeFiles.Children.Add(Note("No completed file edits reported in this conversation."));
        else if (_changeReportedCounts.Values.Any(item => item.Truncated))
            _changeFiles.Children.Add(Note($"{_changeSnapshots.Values.Sum(items => items.Length)} of {count} reported file details are available. Some before/after snapshots were not retained."));
        for (var i = _changeTurns.Count - 1; i >= 0; i--)
        {
            var turn = _changeTurns[i];
            var entries = _changeSnapshots[turn];
            if (entries.Length == 0) continue;
            _changeFiles.Children.Add(Note($"Agent turn {i + 1} · {entries.Length} files"));
            foreach (var change in entries)
            {
                var row = new Grid { ColumnSpacing = DeskTheme.Space8 };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var file = Button(Path.GetFileName(change.Path), () => ShowChangeInPanel(change));
                file.HorizontalAlignment = HorizontalAlignment.Stretch;
                file.HorizontalContentAlignment = HorizontalAlignment.Left;
                ToolTipService.SetToolTip(file, change.Path);
                AutomationName(file, "Review change " + change.Path);
                row.Children.Add(file);
                if (_lineReviewOn)
                {
                    var path = change.Path;
                    var review = Button("Review", () => OpenLineReview(turn, "main", _chatTurns.IndexOf(turn), path));
                    AutomationName(review, "Review file " + path);
                    ToolTipService.SetToolTip(review, "Comment on this diff line by line");
                    Grid.SetColumn(review, 1);
                    row.Children.Add(review);
                }
                _changeFiles.Children.Add(row);
            }
        }
    }

    private void ShowChangeInPanel(CursorFileChange change)
    {
        PauseFollow();
        _changeDetail.Children.Clear();
        _changeDetail.Children.Add(new TextBlock { Text = change.Path, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        _changeDetail.Children.Add(Note(change.UnifiedDiff is null ? "Reported edit snapshot. Later edits may have changed this file again."
            : "Reported file change patch. Later edits may have changed this file again."));
        void AddText(string label, string text)
        {
            _changeDetail.Children.Add(Head(label));
            _changeDetail.Children.Add(new TextBlock { Text = text.Length > 100000 ? text[..100000] + "\n[Display truncated]" : text,
                FontFamily = DeskTheme.MonoFont, FontSize = DeskTheme.SizeCaption, TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true, Margin = new Thickness(0, 0, 0, DeskTheme.Space12) });
        }
        if (change.UnifiedDiff is { } patch) AddText("Changes", patch);
        else
        {
            AddText("Before", change.Before ?? "[New file]");
            AddText("After", change.After);
        }
    }
}
