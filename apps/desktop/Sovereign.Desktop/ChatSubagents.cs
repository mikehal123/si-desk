using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Sovereign.Desktop.Client;
using Sovereign.Desktop.Connector;
using static Sovereign.Desktop.MainWindow;

namespace Sovereign.Desktop;

internal sealed partial class ChatConversation
{
    private readonly CheckBox _subagentOption = new() { Content = "Allow subagents" };
    private readonly CheckBox _swarmOption = new() { Content = "Plan big goals as a swarm (uses subagents)" };
    private readonly StackPanel _subagentOptionRow = new() { Orientation = Orientation.Horizontal, Spacing = DeskTheme.Space8 };
    private readonly Button _subagentsToggle = new() { Content = "Subagents · 0" };
    private readonly StackPanel _subagentList = new() { Spacing = DeskTheme.Space8, Visibility = Visibility.Collapsed };
    private readonly TextBlock _subagentSummary = SubagentCaption("Active · 0");
    private readonly TextBlock _subagentEmpty = SubagentCaption("No helpers yet.");
    private readonly Dictionary<string, CursorSubagent> _subagentRows = [];
    private readonly Dictionary<string, SubagentRowView> _subagentViews = [];
    private bool _showAgents;
    private bool _subagentsShownForTurn;

    private UIElement BuildSubagentOption()
    {
        _subagentOption.IsChecked = _saved.SubagentsEnabled;
        AutomationName(_subagentOption, "Allow subagents in this project chat");
        ToolTipService.SetToolTip(_subagentOption, "Allow up to three direct helpers at once. Helpers inherit this chat's mode: Plan is read-only, Always ask requests approval, and Always allow allows project tools. Stop response stops the main agent and its helpers. Uses the Cursor subscription or Grok Build connection selected in this chat.");
        _subagentOption.Click += async (_, _) =>
        {
            if (_restoring || _chatBusy) return;
            var allow = _subagentOption.IsChecked == true;
            if (!allow) _swarmOption.IsChecked = false;
            _saved = _saved with { EnableSubagents = allow, SwarmMode = allow && _saved.SwarmEnabled, SubagentPreferenceVersion = 1 };
            UpdateSubagentOption();
            await ProjectPreferencesChangedAsync();
        };
        _swarmOption.IsChecked = _saved.SwarmEnabled;
        AutomationName(_swarmOption, "Plan big goals as a swarm (uses subagents)");
        ToolTipService.SetToolTip(_swarmOption, "Uses subagents to split a big goal into tasks. Greyed out while Allow subagents is off. Each goal gets its own run board.");
        _swarmOption.Click += async (_, _) =>
        {
            if (_restoring || _chatBusy) return;
            if (_swarmOption.IsChecked == true)
            {
                _subagentOption.IsChecked = true;
                _saved = _saved with { SwarmMode = true, EnableSubagents = true, SubagentPreferenceVersion = 1 };
            }
            else _saved = _saved with { SwarmMode = false };
            await ProjectPreferencesChangedAsync();
        };
        _subagentOptionRow.Children.Clear();
        _subagentOptionRow.Children.Add(_subagentOption);
        _subagentOptionRow.Children.Add(_swarmOption);
        return _subagentOptionRow;
    }

    private void UpdateSubagentOption()
    {
        var show = (CursorChat || GrokBuildChat) && _projectFolder is not null ? Visibility.Visible : Visibility.Collapsed;
        _subagentOption.Visibility = show;
        _swarmOption.Visibility = show;
        _subagentOptionRow.Visibility = show;
        _subagentOption.IsEnabled = !_chatBusy;
        _swarmOption.IsEnabled = !_chatBusy && _saved.SubagentsEnabled;
        _swarmOption.Opacity = _saved.SubagentsEnabled ? 1 : 0.45;
        _subagentOption.IsChecked = _saved.SubagentsEnabled;
        _swarmOption.IsChecked = _saved.SubagentsEnabled && _saved.SwarmEnabled;
    }

    private void ShowAgents(bool show)
    {
        _showSwarm = false;
        _showAgents = show;
        ApplyRightPane();
    }

    private void ToggleSubagentsPanel()
    {
        if (_changesOpen && _showAgents) SetChangesOpen(false);
        else { ShowAgents(true); SetChangesOpen(true); }
    }

    private void BeginSubagentTurn() => _subagentsShownForTurn = false;

    private static bool SubagentActive(CursorSubagent child) => child.State is "running" or "pending" or "queued" or "waiting";

    private void ObserveSubagent(CursorActivityEvent activity)
    {
        if (activity.Subagent is not { } child) return;
        if (_respawnedHelpers.Contains(child.SessionId) && SubagentActive(child))
            child = child with { State = "interrupted", Failure = "Respawn requested for this helper only.", CurrentWork = "Respawn requested" };
        if (_subagentRows.Count >= 64 && !_subagentRows.ContainsKey(child.SessionId))
        {
            var retired = _subagentRows.Where(pair => !SubagentActive(pair.Value))
                .OrderBy(pair => pair.Value.StartedAt ?? DateTimeOffset.MinValue).FirstOrDefault();
            if (retired.Key is null) return;
            if (_subagentViews.TryGetValue(retired.Key, out var retiredView))
            {
                retiredView.Retire();
                Detach(retiredView.Root);
            }
            if (_openSubagentPanels.ContainsKey(retired.Key)) CloseSubagentPanel(retired.Key);
            _subagentViews.Remove(retired.Key);
            _subagentRows.Remove(retired.Key);
        }
        var launched = !_subagentRows.TryGetValue(child.SessionId, out var previous) || !SubagentActive(previous) && SubagentActive(child);
        var panelOpen = _openSubagentPanels.ContainsKey(child.SessionId);
        _subagentRows[child.SessionId] = child;
        var swarmWorker = SwarmSection.IsSwarmWorker(child.SessionId, child.Name, child.Task, SwarmSection.TryLoad(_saved.Id));
        if (!_subagentViews.TryGetValue(child.SessionId, out var view))
        {
            view = new SubagentRowView(_subagentViews.Count, () => OpenSubagentPanel(child.SessionId), () => CloseSubagentCard(child.SessionId));
            view.SetOpensPanel(_saved.SubagentChatPanelsEnabled);
            _subagentViews.Add(child.SessionId, view);
            if (!swarmWorker) _subagentList.Children.Insert(SubagentCardInsertIndex, view.Root);
        }
        else if (swarmWorker) Detach(view.Root);
        else if (view.Root.Parent != _subagentList)
        {
            Detach(view.Root);
            _subagentList.Children.Insert(Math.Min(SubagentCardInsertIndex, _subagentList.Children.Count), view.Root);
        }
        view.BindRespawn(() => RespawnSwarmHelper(child.SessionId));
        view.SetSwarmCard(swarmWorker);
        view.Update(child);
        var closed = IsSubagentCardClosed(child.SessionId);
        view.SetClosed(closed);
        if (!_restoring && launched && SubagentActive(child))
            _skillCaptions[child.SessionId] = _turnSkillCaption;
        var skillCaption = _skillCaptions.GetValueOrDefault(child.SessionId) ?? "";
        view.SetSkills(skillCaption);
        if (_openSubagentPanels.TryGetValue(child.SessionId, out var skillPanel)) skillPanel.SetSkills(skillCaption);
        if (!_restoring && child.State == "completed" && previous?.State != "completed") RememberSkill(child);
        TrackTeamHelper(child);
        if (!_restoring && !_respawnedHelpers.Contains(child.SessionId) && previous is not null && SubagentActive(previous) && !SubagentActive(child))
            ApplySwarmInterrupt(child);
        UpdateSubagentCounts();
        if (swarmWorker)
        {
            view.SetClosed(true);
            MountSwarmWorker(previous, child);
        }
        else if (!closed && _saved.SubagentChatPanelsEnabled && launched && SubagentActive(child) && !_restoring && !_suppressAgentReveal)
            OpenSubagentPanel(child.SessionId);
        if (panelOpen && !swarmWorker) StreamOpenSubagentPanel(previous, child);
        // A launch opens the tab once per response. Later updates never undo a user's Close or tab choice.
        if (!swarmWorker && !closed && launched && SubagentActive(child) && !_restoring && !_subagentsShownForTurn && !_suppressAgentReveal)
        {
            _subagentsShownForTurn = true;
            ShowAgents(true);
            SetChangesOpen(true);
        }
    }

    private void RestoreSubagents()
    {
        foreach (var item in _saved.Turns.SelectMany(t => t.Activity).Where(a => a.Subagent is not null))
            ObserveSubagent(item);
        FinishSubagents("restored");
        RestoreOpenSubagentPanels();
    }

    private void FinishSubagents(string outcome)
    {
        foreach (var pair in _subagentRows.ToArray())
            if (SubagentActive(pair.Value))
            {
                var previous = pair.Value;
                var child = previous with { State = outcome == "stopped" ? "stop requested" : "disconnected",
                    CompletedAt = previous.CompletedAt ?? (outcome == "restored" ? previous.UpdatedAt ?? previous.StartedAt : DateTimeOffset.UtcNow) };
                _subagentRows[pair.Key] = child;
                _subagentViews[pair.Key].Update(child);
                StreamOpenSubagentPanel(previous, child);
            }
        SyncTeamTerminal();
        if (outcome != "stopped")
            foreach (var pair in _subagentRows)
                if (pair.Value.State is "disconnected" or "interrupted" or "unconfirmed")
                    ApplySwarmInterrupt(pair.Value);
        UpdateSubagentCounts();
    }

    private bool IsSwarmHelper(string sessionId)
    {
        if (_swarmMounted.Contains(sessionId) || sessionId.StartsWith("swarm-", StringComparison.Ordinal) || _respawnedHelpers.Contains(sessionId)) return true;
        _subagentRows.TryGetValue(sessionId, out var child);
        return SwarmSection.IsSwarmWorker(sessionId, child?.Name, child?.Task, SwarmSection.TryLoad(_saved.Id));
    }

    private void RespawnSwarmHelper(string sessionId)
    {
        var board = SwarmSection.TryLoad(_saved.Id);
        _subagentRows.TryGetValue(sessionId, out var child);
        var taskId = SwarmSection.MatchingTaskId(board, sessionId, child?.Name, child?.Task);
        RespawnSwarmTask(board, taskId, sessionId);
    }

    private void RespawnSwarmTask(SwarmBoard? board, string? taskId, string? sessionId)
    {
        board ??= SwarmSection.TryLoad(_saved.Id);
        if (board is null || string.IsNullOrWhiteSpace(taskId)) return;
        const string reason = "Respawn requested for this helper only.";
        try { board.Respawn(taskId, reason); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or JsonException) { return; }
        sessionId ??= SessionForTask(board, taskId);
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            _respawnedHelpers.Add(sessionId);
            if (_subagentRows.TryGetValue(sessionId, out var child))
            {
                var stopped = child with
                {
                    State = "interrupted", Failure = reason, CurrentWork = "Respawn requested",
                    CompletedAt = child.CompletedAt ?? DateTimeOffset.UtcNow
                };
                _subagentRows[sessionId] = stopped;
                if (_subagentViews.TryGetValue(sessionId, out var view))
                {
                    view.SetSwarmCard(true);
                    view.Update(stopped);
                }
                if (_openSubagentPanels.TryGetValue(sessionId, out var panel)) panel.Stream(child, stopped);
            }
        }
        try { BindSwarmBoard(board); }
        catch (Exception) { }
    }

    private string? SessionForTask(SwarmBoard board, string taskId)
    {
        var task = board.Tasks.FirstOrDefault(item => item.Id == taskId);
        if (task is not null && !string.IsNullOrWhiteSpace(task.Assignee) && _subagentRows.ContainsKey(task.Assignee))
            return task.Assignee;
        var synthetic = SwarmSection.WorkerSessionId(board.Id, taskId);
        if (_subagentRows.ContainsKey(synthetic)) return synthetic;
        foreach (var pair in _subagentRows)
            if (SwarmSection.MatchingTaskId(board, pair.Key, pair.Value.Name, pair.Value.Task) == taskId)
                return pair.Key;
        return null;
    }

    private void ApplySwarmInterrupt(CursorSubagent child)
    {
        if (child.State is not ("interrupted" or "disconnected" or "unconfirmed" or "failed")) return;
        var board = SwarmSection.TryLoad(_saved.Id);
        var taskId = SwarmSection.MatchingTaskId(board, child.SessionId, child.Name, child.Task);
        if (board is null || taskId is null) return;
        var task = board.Tasks.FirstOrDefault(item => item.Id == taskId);
        if (task is null || task.Status != SwarmStatus.Doing) return;
        var reason = string.IsNullOrWhiteSpace(child.Failure) ? "Helper " + child.State + "." : child.Failure;
        try { board.Interrupt(taskId, reason); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or JsonException) { }
    }

    private void UpdateSubagentCounts()
    {
        var listed = ListedHelpers();
        var active = listed.Count(SubagentActive);
        var outcomes = new[] { ("Completed", "completed"), ("Interrupted", "interrupted"), ("Failed", "failed"),
            ("Stopped", "stopped"), ("Blocked", "blocked"), ("Stop requested", "stop requested"), ("Status unknown", "unknown") };
        string Outcome(CursorSubagent child) => child.State switch {
            "completed" => "completed", "disconnected" or "interrupted" or "unconfirmed" => "interrupted",
            "failed" => "failed", "cancelled" or "stopped" => "stopped", "blocked" => "blocked",
            "stop requested" => "stop requested", _ => "unknown" };
        var summary = new List<string> { $"Active · {active}" };
        foreach (var (label, outcome) in outcomes)
        {
            var count = listed.Count(child => !SubagentActive(child) && Outcome(child) == outcome);
            if (count > 0) summary.Add($"{label} · {count}");
        }
        _subagentsToggle.Content = active > 0 ? $"Subagents · {active} working" : $"Subagents · {listed.Count}";
        _subagentSummary.Text = string.Join("   ", summary);
        UpdateClosedAgentCount();
        AutomationName(_subagentsToggle, "Show or hide subagents panel. " + string.Join(", ", summary) + $", {listed.Count} retained helpers.");
        RefreshChatHeaderTabs();
    }

    private List<CursorSubagent> ListedHelpers()
    {
        var board = SwarmSection.TryLoad(_saved.Id);
        return _subagentRows.Values.Where(child => !SwarmSection.IsSwarmWorker(child.SessionId, child.Name, child.Task, board)).ToList();
    }

    private void RefreshSubagentElapsed()
    {
        RefreshOpenSubagentPanels();
        if (!_changesOpen || !_showAgents) return;
        foreach (var pair in _subagentRows) _subagentViews[pair.Key].UpdateElapsed(pair.Value);
    }

    private static Border SubagentHeaderChrome(UIElement header)
    {
        var chrome = new Border
        {
            CornerRadius = new CornerRadius(DeskTheme.Radius6), Padding = new Thickness(0),
            Background = DeskTheme.Transparent, Child = header,
            BackgroundTransition = new BrushTransition { Duration = TimeSpan.FromMilliseconds(DeskTheme.MotionMilliseconds) }
        };
        chrome.PointerEntered += (_, _) => chrome.Background = DeskTheme.Hover;
        chrome.PointerExited += (_, _) => chrome.Background = DeskTheme.Transparent;
        return chrome;
    }

    private static TextBlock SubagentCaption(string text) => new()
    {
        Text = text, FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeCaption, LineHeight = DeskTheme.Space16,
        Foreground = DeskTheme.Muted, TextWrapping = TextWrapping.Wrap
    };

    private static string SubagentStatusLabel(string? state) => state switch
    {
        "running" => "Running",
        "completed" => "Finished",
        "failed" => "Failed",
        "cancelled" or "stopped" => "Stopped",
        "pending" => "Pending",
        "queued" => "Queued",
        "waiting" => "Waiting",
        "stop requested" => "Stop requested",
        "disconnected" => "Disconnected",
        "interrupted" => "Interrupted",
        "unconfirmed" => "Unconfirmed",
        "blocked" => "Blocked",
        null or "" or "unknown" => "Status unknown",
        _ => string.IsNullOrWhiteSpace(state) ? "Status unknown" : state.Trim()
    };

    private static Border SubagentStatusChip(TextBlock label) => new()
    {
        Child = label, Background = DeskTheme.Raised, CornerRadius = new CornerRadius(DeskTheme.Radius6),
        Padding = new Thickness(DeskTheme.Space8, DeskTheme.Space4, DeskTheme.Space8, DeskTheme.Space4),
        HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 144
    };

    private static Expander SubagentExpander(Border chrome, UIElement content, bool expanded)
    {
        var expander = new Expander
        {
            Header = chrome, IsExpanded = expanded, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, BorderThickness = new Thickness(1), Padding = new Thickness(0),
            BorderBrush = DeskTheme.Divider, CornerRadius = new CornerRadius(DeskTheme.Radius10),
            Background = DeskTheme.Surface, Foreground = DeskTheme.Text, FontFamily = DeskTheme.UiFont,
            FontSize = DeskTheme.SizeBody, Content = content
        };
        return expander;
    }

    private static ToggleButton? FindHeaderToggle(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ToggleButton toggle) return toggle;
            if (FindHeaderToggle(child) is { } nested) return nested;
        }
        return null;
    }

    internal static string DescribeExpander(DependencyObject root)
    {
        var lines = new List<string>();
        void Walk(DependencyObject node, int depth)
        {
            if (depth > 8 || lines.Count > 80) return;
            var name = node is FrameworkElement element ? element.Name : "";
            var transform = node is UIElement ui ? ui.RenderTransform?.GetType().Name : "";
            lines.Add(new string(' ', depth * 2) + node.GetType().Name + " " + name + " " + transform);
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++) Walk(VisualTreeHelper.GetChild(node, i), depth + 1);
        }
        Walk(root, 0);
        return string.Join(" | ", lines);
    }

    internal static string ExpanderChevronMark(DependencyObject root)
    {
        string mark = "";
        void Walk(DependencyObject node, int depth)
        {
            if (depth > 12 || mark.Length > 0) return;
            if (node is FrameworkElement element && element.Name == "ExpandCollapseChevron")
            {
                var transform = element.RenderTransform;
                mark = transform switch
                {
                    ScaleTransform scale => scale.ScaleX.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "," + scale.ScaleY.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    RotateTransform rotate => rotate.Angle.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    CompositeTransform composite => composite.Rotation.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "/" + composite.ScaleY.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    _ => transform?.GetType().Name ?? "none"
                };
                if (element is FrameworkElement host)
                {
                    foreach (var group in VisualStateManager.GetVisualStateGroups(host))
                        if (group.CurrentState is { Name: { Length: > 0 } name }) mark += ":" + name;
                }
                return;
            }
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++) Walk(VisualTreeHelper.GetChild(node, i), depth + 1);
        }
        Walk(root, 0);
        return mark.Length == 0 ? "missing" : mark;
    }

    private sealed class SubagentRowView
    {
        internal readonly Expander Root;
        internal readonly TextBlock Name = new() { FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeBody, LineHeight = 20, Foreground = DeskTheme.Text, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
        internal readonly TextBlock State = new() { FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeCaption, LineHeight = DeskTheme.Space16, MaxLines = 1, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
        internal readonly TextBlock Model = new() { FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeCaption, LineHeight = DeskTheme.Space16, MaxLines = 1, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = DeskTheme.Muted };
        internal readonly TextBlock Provider = Note("");
        internal readonly TextBlock Requested = new() { FontSize = DeskTheme.SizeCaption, Opacity = 1, MaxLines = 2, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, Visibility = Visibility.Collapsed };
        internal readonly TextBlock Work = new() { FontSize = DeskTheme.SizeCaption, Opacity = 1, MaxLines = 2, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis };
        internal readonly TextBlock Elapsed = SubagentCaption("");
        internal readonly TextBlock Details = SubagentCaption("");
        internal readonly TextBlock Result = DetailText();
        internal readonly TextBlock Skills = SubagentCaption("");
        internal readonly TextBlock Failure = DetailText();
        internal readonly Image Avatar = new() { Width = 36, Height = 36, Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
        internal readonly TextBlock Initial = new() { Visibility = Visibility.Collapsed };
        internal string? AvatarAssetName { get; private set; }
        internal bool AvatarLoaded { get; private set; }
        internal bool AvatarFailed { get; private set; }
        private readonly TextBlock _task = DetailText();
        private readonly TextBlock _taskLabel = DetailLabel("Task");
        private readonly TextBlock _resultLabel = DetailLabel("Result");
        private readonly TextBlock _diagnostics = DetailText();
        private readonly TextBlock _failureLabel = DetailLabel("Failure");
        private readonly TextBlock _reasoning = DetailText();
        private readonly TextBlock _tool = DetailText();
        private readonly TextBlock _toolLabel = DetailLabel("Latest tool");
        private readonly ChatDisclosure _diagnosticsPanel;
        private readonly ChatDisclosure _logPanel;
        private readonly ChatDisclosure _thinkingPanel;
        private readonly Border _statusChip;
        private readonly SubagentBlobFace _face = new();
        private readonly Action _openPanel;
        private Action? _respawn;
        private bool _opensPanel;
        private string _lifeState = "idle";
        internal readonly Button CloseCard = new() { Content = "Hide", MinWidth = 64, MinHeight = 32, FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeCaption,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Padding = new Thickness(DeskTheme.Space8, DeskTheme.Space4, DeskTheme.Space8, DeskTheme.Space4),
            BorderThickness = new Thickness(1), BorderBrush = DeskTheme.Divider, VerticalAlignment = VerticalAlignment.Center,
            Background = DeskTheme.Raised, Foreground = DeskTheme.Muted, CornerRadius = new CornerRadius(DeskTheme.Radius6) };
        internal readonly Button Respawn = new() { Content = "Respawn", MinWidth = 88, MinHeight = 32, FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeBody,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Padding = new Thickness(DeskTheme.Space12, DeskTheme.Space4, DeskTheme.Space12, DeskTheme.Space4),
            BorderThickness = new Thickness(1), BorderBrush = DeskTheme.Text, VerticalAlignment = VerticalAlignment.Center,
            Background = DeskTheme.Raised, Foreground = DeskTheme.Text, CornerRadius = new CornerRadius(DeskTheme.Radius6),
            Visibility = Visibility.Collapsed };

        internal SubagentRowView(int _, Action openPanel, Action closeCard)
        {
            _openPanel = openPanel;
            var face = new Grid { Width = 36, Height = 36, VerticalAlignment = VerticalAlignment.Top, Children = { _face.Root, Avatar, Initial } };
            var text = new StackPanel { Spacing = DeskTheme.Space4, VerticalAlignment = VerticalAlignment.Top, Children = { Name, Model, Provider, Requested, Work } };
            _statusChip = SubagentStatusChip(State);
            Elapsed.HorizontalAlignment = HorizontalAlignment.Right;
            var meta = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Children = { _statusChip } };
            var header = new Grid { ColumnSpacing = DeskTheme.Space8, HorizontalAlignment = HorizontalAlignment.Stretch };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(face); Grid.SetColumn(text, 1); header.Children.Add(text);
            Grid.SetColumn(meta, 2); header.Children.Add(meta);
            Grid.SetColumn(CloseCard, 3); header.Children.Add(CloseCard);
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(Respawn, 4); header.Children.Add(Respawn);
            ToolTipService.SetToolTip(CloseCard, "Hide this card. Does not stop the agent.");
            ToolTipService.SetToolTip(Respawn, "Stops only this helper and restarts its task once with a narrower scope. A second use fails this task. The main agent and other workers keep running.");
            CloseCard.Click += (_, _) => closeCard();
            Respawn.Click += (_, _) => _respawn?.Invoke();
            var chrome = SubagentHeaderChrome(header);
            _diagnosticsPanel = new ChatDisclosure("Diagnostics", _diagnostics) { Visibility = Visibility.Collapsed };
            _logPanel = new ChatDisclosure("Log", new StackPanel { Spacing = DeskTheme.Space8, Children = { Details, _toolLabel, _tool, _diagnosticsPanel } });
            _thinkingPanel = new ChatDisclosure("Thinking", _reasoning) { Visibility = Visibility.Collapsed };
            _diagnostics.FontFamily = _tool.FontFamily = DeskTheme.MonoFont;
            _diagnostics.FontSize = _tool.FontSize = DeskTheme.SizeCode;
            Details.IsTextSelectionEnabled = true;
            Failure.Foreground = _failureLabel.Foreground = DeskTheme.Error;
            Root = SubagentExpander(chrome, new StackPanel
            {
                Spacing = DeskTheme.Space8, Margin = new Thickness(DeskTheme.Space12, DeskTheme.Space4, DeskTheme.Space12, DeskTheme.Space12),
                Children = { _taskLabel, _task, Skills, _resultLabel, Result, _failureLabel, Failure, _logPanel, _thinkingPanel }
            }, false);
            Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
            {
                for (var element = e.OriginalSource as DependencyObject; element is not null && element != Root;
                    element = VisualTreeHelper.GetParent(element))
                    if (ReferenceEquals(element, CloseCard) || ReferenceEquals(element, Respawn)) return;
                if (_opensPanel) _openPanel();
            }), true);
            Root.Expanding += (_, _) => { if (_opensPanel) _openPanel(); };
            Skills.Visibility = Visibility.Collapsed;
            Root.Loaded += (_, _) => _face.SetLife(_lifeState);
        }

        internal void SetOpensPanel(bool opens) => _opensPanel = opens;

        internal void BindRespawn(Action respawn) => _respawn = respawn;

        internal void SetSwarmCard(bool swarm)
        {
            CloseCard.Visibility = swarm ? Visibility.Collapsed : Visibility.Visible;
            Respawn.Visibility = swarm ? Visibility.Visible : Visibility.Collapsed;
        }

        internal void SetClosed(bool closed)
        {
            Root.Visibility = closed ? Visibility.Collapsed : Visibility.Visible;
            if (closed) { Root.IsExpanded = false; _face.SetLife("idle"); }
            else _face.SetLife(_lifeState);
        }

        internal void SetSkills(string caption)
        {
            Skills.Text = caption;
            Skills.Visibility = caption.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        internal void Update(CursorSubagent child)
        {
            var role = SubagentCardText.RoleLine(child);
            Name.Text = string.IsNullOrWhiteSpace(child.Name) ? "Subagent" : child.Name.Trim();
            AutomationName(CloseCard, "Hide agent card " + Name.Text);
            AutomationName(Respawn, "Respawn swarm worker " + Name.Text);
            ToolTipService.SetToolTip(Name, role == "Role not reported" ? Name.Text : Name.Text + " · " + role);
            _face.Apply(child.SessionId, child.Name);
            _face.SetLife(_lifeState = child.State);
            AvatarAssetName = _face.Key;
            AvatarLoaded = true;
            AvatarFailed = false;
            Avatar.Visibility = Visibility.Collapsed;
            Initial.Visibility = Visibility.Collapsed;
            Model.Text = SubagentCardText.IdentityLine(child);
            Provider.Visibility = Visibility.Collapsed;
            Requested.Visibility = Visibility.Collapsed;
            ToolTipService.SetToolTip(Model, SubagentCardText.IdentityDetail(child));
            State.Text = SubagentStatusLabel(child.State);
            State.Foreground = DeskTheme.StatusForeground(child.State);
            _statusChip.Background = DeskTheme.StatusSurface(child.State);
            ToolTipService.SetToolTip(_statusChip, "Status: " + child.State + " · " + SubagentCardText.LifecycleLine(child));
            ToolTipService.SetToolTip(_face.Root, SubagentCardText.LifecycleLine(child));
            Work.Visibility = Visibility.Collapsed;
            _task.Text = DisplayText(SubagentCardText.TaskLine(child));
            _taskLabel.Visibility = _task.Visibility = Visibility.Visible;
            Result.Text = child.Output.Length == 0 ? (SubagentActive(child) ? "No response yet." : "No response recorded.") : DisplayText(child.Output);
            Result.Foreground = child.Output.Length == 0 ? DeskTheme.Muted : DeskTheme.Text;
            _resultLabel.Visibility = Result.Visibility = Visibility.Visible;
            Failure.Text = !string.IsNullOrWhiteSpace(child.Failure) ? DisplayText(child.Failure) : child.State == "failed" ? "No failure details were reported." : "";
            Failure.Visibility = _failureLabel.Visibility = Failure.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            _reasoning.Text = DisplayText(child.Reasoning);
            _thinkingPanel.Visibility = child.Reasoning.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (child.Tool is { } tool)
            {
                var readable = tool.Title + " · " + tool.Status;
                if (!string.IsNullOrWhiteSpace(tool.Output)) readable += "\n" + tool.Output;
                else if (!string.IsNullOrWhiteSpace(tool.Input) && !tool.Input.TrimStart().StartsWith('{')) readable += "\n" + tool.Input;
                _tool.Text = DisplayText(readable);
                _diagnostics.Text = DisplayText(string.IsNullOrWhiteSpace(tool.Diagnostics) ? "" : tool.Diagnostics);
            }
            else { _tool.Text = ""; _diagnostics.Text = ""; }
            _tool.Visibility = _toolLabel.Visibility = _tool.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            _diagnosticsPanel.Visibility = _diagnostics.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            UpdateElapsed(child);
            AutomationName(Root, $"Subagent {Name.Text}, {Model.Text}, {State.Text}. Expand for task, result and diagnostics.");
        }

        internal void UpdateElapsed(CursorSubagent child)
        {
            var started = child.StartedAt;
            var end = child.CompletedAt ?? (SubagentActive(child) ? DateTimeOffset.UtcNow : child.UpdatedAt);
            if (started is null || end is null) Elapsed.Text = "Not recorded";
            else
            {
                var duration = end.Value - started.Value;
                if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
                Elapsed.Text = duration.TotalHours >= 1 ? $"{(int)duration.TotalHours}h {duration.Minutes}m"
                    : duration.TotalMinutes >= 1 ? $"{(int)duration.TotalMinutes}m {duration.Seconds}s" : $"{duration.Seconds}s";
            }
            var startText = started?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture) ?? "Not recorded";
            Details.Text = SubagentCardText.DetailBlock(child, Elapsed.Text, startText) + "\nStatus: " + SubagentCardText.LifecycleLine(child);
            State.Text = SubagentStatusLabel(child.State) + " · " + Elapsed.Text;
            if (child.ToolSummary is { } activity)
                Details.Text += (activity.Compacted > 0 ? $"\n{activity.Compacted} earlier steps compacted; recent details and recorded file changes are retained." : "")
                    + (activity.ChangeDetailsTruncated ? "\nSome file details exceeded the retention limit; no missing before/after text is inferred." : "")
                    + (activity.CountsIncomplete ? "\nActivity counts are incomplete." : "")
                    + (activity.StorageError is { Length: > 0 } error ? "\nActivity details could not be retained: " + error : "");
        }

        internal void Retire()
        {
            _lifeState = "idle";
            _face.SetLife("idle");
        }

        private static TextBlock DetailText() => new() { FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeBody, LineHeight = 20, Foreground = DeskTheme.Text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        private static TextBlock DetailLabel(string text) => new() { Text = text, FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeCaption, Foreground = DeskTheme.Muted, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        private static string DisplayText(string text) => text.Length > 100000 ? text[..100000] + "\n[Display truncated]" : text;
    }
}
