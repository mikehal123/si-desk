using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Sovereign.Desktop.Client;
using FontWeights = Microsoft.UI.Text.FontWeights;
using Windows.UI.Text;

namespace Sovereign.Desktop;

/// <summary>A turn owns its activity and response; provider callbacks only enqueue data.</summary>
internal sealed class ChatActivityView
{
    private readonly ConcurrentQueue<CursorActivityEvent> _pending = new();
    private readonly CursorTurnActivity _state;
    private readonly string? _trustedActivityRoot;
    private string? _changeArchiveWarning;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly TextBlock _status = Text("Waiting for provider", DeskTheme.SizeCaption);
    private readonly TextBlock _failureNotice = Text("", DeskTheme.SizeBody);
    private readonly TextBlock _executionNotice = Text("", DeskTheme.SizeBody);
    private readonly TextBlock _reasoningText = Text("", DeskTheme.SizeBody);
    private readonly TextBlock _reasoningPreview = Text("", DeskTheme.SizeBody);
    private readonly ChatDisclosure _fullReasoning;
    private readonly ChatDisclosure _reasoning;
    private readonly StackPanel _details = new() { Spacing = DeskTheme.Space8 };
    private readonly Dictionary<string, StackPanel> _groupPanels = [];
    private readonly Dictionary<string, ChatDisclosure> _groups = [];
    private TimeSpan _reasoningTime;
    private TimeSpan? _thinkingSince;
    private readonly ChatDisclosure _activity;
    private readonly ChatDisclosure _work;
    private readonly StackPanel _earlierText = new() { Spacing = DeskTheme.Space12, Visibility = Visibility.Collapsed };
    private readonly ChatDisclosure _planSummary;
    private readonly string _model;
    private readonly StackPanel _tools = new() { Spacing = DeskTheme.Space4 };
    private readonly TextBlock _activityTotals = Text("", DeskTheme.SizeCaption);
    private readonly StackPanel _plan = new() { Spacing = DeskTheme.Space4, Visibility = Visibility.Collapsed };
    private readonly StackPanel _responses = new() { Spacing = DeskTheme.Space12 };
    private readonly Dictionary<string, ChatDisclosure> _rows = [];
    private readonly Dictionary<string, StackPanel> _rowOwners = [];
    private readonly Dictionary<string, CursorToolActivity> _rendered = [];
    private readonly Action<ChatActivityView> _changesChanged;
    public IReadOnlyCollection<CursorPlanItem> Plan => _state.Plan;
    public IReadOnlyCollection<CursorFileChange> Changes
    {
        get
        {
            if (_state.ChangeArchive is { } archive && _trustedActivityRoot is not null)
            {
                try { return CursorActivityRetention.AllChanges(archive, _trustedActivityRoot).ToArray(); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                { _changeArchiveWarning = "Saved file-change details are unavailable: " + error.Message; }
            }
            return _state.Changes;
        }
    }
    public int ReportedChangedFiles => _state.ChangedFiles;
    public bool ChangeDetailsTruncated => _state.ChangeDetailsTruncated || _changeArchiveWarning is not null;
    private readonly Action _updated;
    private readonly Action<string> _statusChanged;
    private readonly Action _inspect;
    private TextBlock? _segment;
    private readonly StringBuilder _segmentText = new();
    private readonly StringBuilder _responseText = new();
    private bool _finished;
    private readonly Button _copyResponse;
    private readonly Button _copyReasoning;
    private readonly Button _clearReasoning;
    private readonly Button _lineReview;
    private Action? _openLineReview;
    private Func<bool>? _lineReviewAllowed;
    private bool _lineReviewBound;
    private bool _tui;
    private bool _thinkingOpen;
    private readonly HashSet<string> _openToolLines = new(StringComparer.Ordinal);
    private readonly StackPanel _tuiTools = new() { Spacing = 0, Visibility = Visibility.Collapsed };
    private readonly Button _showThinking;
    private readonly TextBlock _worked;
    private readonly Dictionary<TextBlock, RichTextBlock> _markdown = new();
    private string _toolStamp = "";
    public StackPanel Root { get; } = new() { Spacing = DeskTheme.Space8, Margin = new Thickness(0, DeskTheme.Space4, 0, DeskTheme.Space24) };
    internal int DisplayTokenEstimate => Math.Max(0, (_responseText.Length + _state.Reasoning.Length) / 4);

    public ChatActivityView(string model, Action<ChatActivityView> changesChanged, Action updated, Action<string> statusChanged, Action inspect, string? trustedActivityRoot = null, bool classicChat = true)
    {
        _tui = !classicChat;
        _trustedActivityRoot = trustedActivityRoot;
        _state = new("Waiting for provider", trustedActivityRoot);
        _changesChanged = changesChanged; _updated = updated; _statusChanged = statusChanged; _inspect = inspect;
        _model = model;
        _planSummary = Disclosure("Plan", _plan); _planSummary.Visibility = Visibility.Collapsed;
        _planSummary.Expanding += (_, _) => _inspect();
        _status.Visibility = Visibility.Collapsed; _status.Foreground = DeskTheme.Muted; _status.MaxLines = 1; _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _reasoningText.Foreground = _reasoningPreview.Foreground = DeskTheme.Muted;
        _failureNotice.Foreground = DeskTheme.Error;
        _executionNotice.Foreground = DeskTheme.Warning;
        _reasoningPreview.MaxLines = 4;
        _reasoningPreview.TextTrimming = TextTrimming.CharacterEllipsis;
        _fullReasoning = Disclosure("Full reasoning", _reasoningText);
        _fullReasoning.Expanding += (_, _) => _inspect();
        _reasoning = Disclosure("Reasoning", new StackPanel { Spacing = DeskTheme.Space8, Children = { _reasoningPreview, _fullReasoning } });
        _activityTotals.Foreground = DeskTheme.Muted; _tools.Children.Add(_activityTotals);
        _activity = Disclosure("Activity", _tools);
        _reasoning.Expanding += (_, _) => _inspect();
        _activity.Expanding += (_, _) => _inspect();
        _reasoning.Visibility = _activity.Visibility = Visibility.Collapsed;
        Button Copy(string label, Func<string> value)
        {
            var button = new Button { Content = label };
            StyleQuietButton(button);
            button.Click += async (_, _) =>
            {
                try
                {
                    Flush(false);
                    await ChatClipboard.CopyAsync(button.DispatcherQueue, value());
                    if (Root.XamlRoot is null) return;
                    button.Content = "Copied";
                    await Task.Delay(1500);
                    if (Root.XamlRoot is not null) button.Content = label;
                }
                catch { if (Root.XamlRoot is not null) button.Content = "Copy failed — retry"; }
            };
            return button;
        }
        _copyResponse = Copy("Copy response", () => DisplayReply);
        _copyReasoning = Copy("Copy reasoning", () => _state.Reasoning);
        _clearReasoning = new Button { Content = "Clear", Visibility = Visibility.Collapsed };
        StyleQuietButton(_clearReasoning);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_clearReasoning, "Clear reasoning beside this response");
        _clearReasoning.Click += (_, _) => ClearDisplayedReasoning();
        _copyResponse.Visibility = _copyReasoning.Visibility = Visibility.Collapsed;
        _lineReview = new Button { Content = "Review", Visibility = Visibility.Collapsed };
        StyleQuietButton(_lineReview);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_lineReview, "Review this plan or diff");
        var copyActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DeskTheme.Space4, Children = {
            _lineReview, _copyResponse, _copyReasoning, _clearReasoning } };
        _details.Children.Add(_planSummary);
        _details.Children.Add(_reasoning); _details.Children.Add(_activity);
        // Keep a stable visual tree. The completed chip toggles the existing details panel.
        _work = Disclosure("Worked", new Border());
        _work.Visibility = Visibility.Collapsed;
        _work.Expanding += (_, _) => { _details.Visibility = Visibility.Visible; _inspect(); };
        _work.Collapsing += (_, _) => { if (_finished) _details.Visibility = Visibility.Collapsed; };
        _executionNotice.Visibility = Visibility.Collapsed;
        _failureNotice.Visibility = Visibility.Collapsed; Root.Children.Add(_failureNotice);
        _showThinking = new Button
        {
            Content = "Reasoning", HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed
        };
        StyleQuietButton(_showThinking);
        _showThinking.Padding = new Thickness(0, DeskTheme.Space4, 0, DeskTheme.Space4);
        _showThinking.Click += (_, _) => { _thinkingOpen = !_thinkingOpen; PresentTui(false); };
        _worked = Text("", DeskTheme.SizeCaption);
        _worked.Foreground = DeskTheme.Muted;
        _worked.FontFamily = DeskTheme.MonoFont;
        _worked.Visibility = Visibility.Collapsed;
        Root.Children.Add(_executionNotice); Root.Children.Add(_earlierText); Root.Children.Add(_work); Root.Children.Add(_details);
        Root.Children.Add(_showThinking); Root.Children.Add(_tuiTools); Root.Children.Add(_responses); Root.Children.Add(_worked); Root.Children.Add(copyActions);
        _timer.Tick += (_, _) => Flush(); _timer.Start();
    }

    internal void ApplyClassicChrome(bool classic)
    {
        _tui = !classic;
        if (!_tui)
        {
            _showThinking.Visibility = Visibility.Collapsed;
            _clearReasoning.Visibility = Visibility.Collapsed;
            _tuiTools.Visibility = Visibility.Collapsed;
            _worked.Visibility = Visibility.Collapsed;
            foreach (var block in _responses.Children.OfType<TextBlock>().ToArray()) PaintMarkdown(block);
            foreach (var block in _earlierText.Children.OfType<TextBlock>().ToArray()) PaintMarkdown(block, _earlierText);
            _reasoningPreview.Visibility = Visibility.Visible;
            _reasoning.Visibility = _state.HasDisplayedReasoning ? Visibility.Visible : Visibility.Collapsed;
            _activity.Visibility = _state.TotalTools == 0 ? Visibility.Collapsed : Visibility.Visible;
            _work.Visibility = _finished ? Visibility.Visible : Visibility.Collapsed;
            _details.Visibility = !_finished || _work.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        PresentTui(true);
    }

    public void ShowFailure(string? reason)
    {
        _failureNotice.Text = reason is null ? "" : "Run failed: " + reason;
        _failureNotice.Visibility = string.IsNullOrEmpty(reason) ? Visibility.Collapsed : Visibility.Visible;
    }

    public void ShowUsageFailure(string exact)
    {
        _failureNotice.Text = exact;
        _failureNotice.Visibility = string.IsNullOrEmpty(exact) ? Visibility.Collapsed : Visibility.Visible;
    }

    public string[] ProgressUpdates => _earlierText.Children.OfType<TextBlock>().Select(t => t.Text).ToArray();
    public string DisplayReply => string.Join("\n\n", _responses.Children.OfType<TextBlock>().Select(t => t.Text));
    public void RestoreProgress(string[] updates)
    {
        foreach (var text in updates)
        {
            var block = Text(text, DeskTheme.SizeBody);
            _earlierText.Children.Add(block);
            PaintMarkdown(block, _earlierText);
        }
        _earlierText.Visibility = ProgressUpdates.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_tui) PresentTui(false);
    }
    internal Button CopyResponseButton => _copyResponse;
    public event Action? ReasoningDisplayChanged;
    public bool HasDisplayedReasoning => _state.HasDisplayedReasoning;
    internal string DisplayedReasoning => _reasoningText.Text;
    internal bool ReasoningDisplayLimitReached => _state.ReasoningDisplayLimitReached;
    internal void ShowReasoningForVerification(bool expanded)
    {
        foreach (var disclosure in new[] { _work, _reasoning, _fullReasoning })
        {
            if (disclosure.IsExpanded == expanded) continue;
            var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(disclosure.Children.OfType<Button>().Single());
            ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
        }
    }
    public void ClearDisplayedReasoning()
    {
        // Consume already-arrived updates first, so they cannot reappear after the click.
        Flush(false);
        _state.ClearReasoningDisplay();
        _reasoningText.Text = _reasoningPreview.Text = "";
        _reasoningTime = TimeSpan.Zero; _thinkingSince = null;
        _reasoning.SetExpanded(false); _fullReasoning.SetExpanded(false);
        _reasoning.Visibility = _copyReasoning.Visibility = _clearReasoning.Visibility = Visibility.Collapsed;
        if (!_finished && _state.TotalTools == 0 && _state.Plan.Count == 0 && _earlierText.Children.Count == 0)
            _work.Visibility = Visibility.Collapsed;
        if (_tui) { _thinkingOpen = false; PresentTui(false); }
        ReasoningDisplayChanged?.Invoke();
    }
    internal int RenderedToolRows => _rows.Count;
    internal int RenderedToolSnapshots => _rendered.Count;
    internal int VisibleToolRows => _groupPanels.Values.Sum(panel => panel.Children.Count(child => child.Visibility == Visibility.Visible));
    internal int ActivityGroupCount => _groups.Count;
    internal string ReasoningHeader => _reasoning.Header.ToString() ?? "";
    internal string ReasoningPreview => _reasoningPreview.Text;
    internal bool FullReasoningExpanded => _fullReasoning.IsExpanded;
    internal bool CompletedDetailsCollapsed => _details.Visibility == Visibility.Collapsed;
    internal bool ProgressInlineVisible => _earlierText.Visibility == Visibility.Visible &&
        _earlierText.Children.OfType<RichTextBlock>().Any(block => block.Visibility == Visibility.Visible);
    internal string WorkHeader => _work.Header.ToString() ?? "";
    internal string VisibleExecutionNotice => _executionNotice.Visibility == Visibility.Visible ? _executionNotice.Text : "";
    internal void ShowReasoningPreviewForVerification() => _reasoning.SetExpanded(true);
    internal void ShowActivityForVerification()
    {
        if (_finished) _work.SetExpanded(true);
        _activity.SetExpanded(true);
    }

    /// <summary>Keep a helper chat's reasoning and tool rows open. The main transcript still uses its own disclosure state.</summary>
    internal void RevealHelperStream()
    {
        if (_state.Reasoning.Length > 0)
        {
            _reasoning.Visibility = Visibility.Visible;
            _reasoning.SetExpanded(true);
        }
        if (_state.TotalTools > 0)
        {
            _activity.Visibility = Visibility.Visible;
            _activity.SetExpanded(true);
        }
        if (_finished && (_state.Reasoning.Length > 0 || _state.TotalTools > 0))
        {
            _details.Visibility = Visibility.Visible;
            _work.Visibility = Visibility.Visible;
            _work.SetExpanded(true);
        }
    }
    internal int RetainedToolSteps => _state.Tools.Count;
    internal long ObservedToolSteps => _state.TotalTools;
    internal string ActivityHeader => _activity.Header.ToString() ?? "";
    internal void FlushForVerification() => Flush(false);
    internal bool DetailsCollapsed => !_work.IsExpanded && !_reasoning.IsExpanded && !_fullReasoning.IsExpanded && !_activity.IsExpanded && !_planSummary.IsExpanded && _groups.Values.All(group => !group.IsExpanded);
    public void Observe(CursorActivityEvent update) => _pending.Enqueue(update);
    public void Append(string text) => _pending.Enqueue(new("delta", text));

    public void Finish(string outcome, bool restored = false)
    {
        if (_finished) return;
        Flush(false); _finished = true; _clock.Stop(); _timer.Stop(); _state.Finish(outcome);
        RenderTools();
        _work.Visibility = Visibility.Visible;
        EndThinking();
        _reasoning.Header = restored ? "Reasoning" : "Reasoning · " + ChatActivityPresentation.Duration(_reasoningTime);
        _work.Header = ChatActivityPresentation.Completion(_clock.Elapsed, _state.TotalTools, outcome, restored);
        foreach (var disclosure in new[] { _work, _reasoning, _fullReasoning, _activity, _planSummary }.Concat(_groups.Values))
            disclosure.SetExpanded(false);
        _details.Visibility = Visibility.Collapsed;
        RefreshLineReviewOffer();
        if (_tui) PresentTui(true);
        _updated();
    }

    internal void BindLineReview(Action open, Func<bool> allowed)
    {
        _openLineReview = open;
        _lineReviewAllowed = allowed;
        if (!_lineReviewBound)
        {
            _lineReviewBound = true;
            _lineReview.Click += (_, _) => _openLineReview?.Invoke();
        }
        RefreshLineReviewOffer();
    }

    internal void RefreshLineReviewOffer()
    {
        var settingOn = _lineReviewAllowed?.Invoke() == true;
        var hasEdits = ReportedChangedFiles > 0 || Changes.Count > 0;
        _lineReview.Visibility = LineReview.ShowButton(settingOn, _state.Plan.Count > 0, hasEdits) ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Dispose() { _finished = true; _timer.Stop(); _clock.Stop(); }

    private void Flush(bool reportStatus = true)
    {
        if (_finished) return;
        var changed = false; var toolsChanged = false; var planChanged = false; var reasoningChanged = false;
        while (_pending.TryDequeue(out var update))
        {
            changed = true;
            if (update.Kind == "reasoning") _thinkingSince ??= _clock.Elapsed;
            else if (update.Kind is "delta" or "tool" or "plan" or "permission") EndThinking();
            if (update.Kind == "delta")
            {
                _state.Apply(new("message"), _clock.Elapsed);
                if (_segment is null)
                {
                    _segment = Text("", DeskTheme.SizeBody); _segmentText.Clear();
                    if (_tui) { _segment.Visibility = Visibility.Collapsed; _segment.FontFamily = DeskTheme.MonoFont; }
                    _responses.Children.Add(_segment);
                }
                _segmentText.Append(update.Text); _responseText.Append(update.Text);
                continue;
            }
            // Text before a tool is commentary. Subsequent text starts its own response paragraph.
            if (update.Kind is "tool" or "reasoning" or "plan")
            {
                if (_segment is not null)
                {
                    _segment.Text = _segmentText.ToString();
                }
                _segment = null;
            }
            if (update.Kind == "tool")
            {
                foreach (var prior in _responses.Children.OfType<TextBlock>().Where(t => !KeepInline(t.Text)).ToArray())
                {
                    if (_markdown.TryGetValue(prior, out var rich)) _responses.Children.Remove(rich);
                    _responses.Children.Remove(prior); _earlierText.Children.Add(prior);
                    if (rich is not null) _earlierText.Children.Add(rich);
                    PaintMarkdown(prior, _earlierText);
                }
                _earlierText.Visibility = ProgressUpdates.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            _state.Apply(update, _clock.Elapsed);
            toolsChanged |= update.Kind is "tool" or "summary" || update.Subagent is { } child && (update.Tool is not null || child.Tools is { Length: > 0 } || child.ToolSummary is not null);
            planChanged |= update.Kind == "plan";
            reasoningChanged |= update.Kind == "reasoning";
        }
        _status.Text = _state.Status(_clock.Elapsed);
        if (reportStatus) _statusChanged(_state.ComposerFlowStatus(_clock.Elapsed));
        if (_segment is not null && changed) _segment.Text = _segmentText.ToString();
        if (toolsChanged) RenderTools();
        else if (changed) UpdateExecutionNotice();
        if (reasoningChanged && _state.Reasoning.Length > 0)
        {
            _reasoning.Visibility = _copyReasoning.Visibility = Visibility.Visible;
            _reasoningText.Text = _state.Reasoning;
            _reasoningPreview.Text = ChatActivityPresentation.ReasoningPreview(_state.Reasoning);
            ReasoningDisplayChanged?.Invoke();
        }
        if (planChanged)
        {
            _plan.Children.Clear(); _plan.Visibility = Visibility.Visible;
            _planSummary.Visibility = _state.Plan.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            _planSummary.Header = $"Plan · {_state.Plan.Count} steps";
            foreach (var item in _state.Plan)
            {
                var line = Text(Mark(item.Status) + "  " + item.Content, DeskTheme.SizeBody);
                line.Foreground = DeskTheme.StatusForeground(item.Status);
                _plan.Children.Add(line);
            }
        }
        if (_state.HasDisplayedReasoning)
            _reasoning.Header = "Reasoning · " + ChatActivityPresentation.Duration(_reasoningTime + (_thinkingSince is { } since ? _clock.Elapsed - since : TimeSpan.Zero));
        if (planChanged || toolsChanged) RefreshLineReviewOffer();
        if (changed) _work.Visibility = Visibility.Collapsed;
        if (_tui) PresentTui(toolsChanged);
        else if (changed)
            foreach (var block in _responses.Children.OfType<TextBlock>().ToArray()) PaintMarkdown(block);
        if (changed) _updated();
    }

    private void RenderTools()
    {
        _copyResponse.Visibility = _responseText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _copyReasoning.Visibility = _state.Reasoning.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateExecutionNotice();
        _activity.Visibility = _state.TotalTools == 0 ? Visibility.Collapsed : Visibility.Visible;
        _activity.Header = $"Activity · {_state.TotalTools} " + (_state.TotalTools == 1 ? "step" : "steps")
            + (_state.CompactedTools > 0 ? $" · {_state.CompactedTools} earlier steps compacted" : "");
        _activityTotals.Text = $"{_state.CompletedTools} completed · {_state.ActiveTools} active · {_state.FailedTools} failed · {_state.BlockedTools} blocked · {_state.UnconfirmedTools} unconfirmed"
            + (_state.CancelledTools > 0 ? $" · {_state.CancelledTools} stopped" : "")
            + (_state.CompactedTools > 0 ? "\nGroups show recent retained steps; earlier details were compacted." : "");
        var retainedIds = _state.Tools.Select(tool => tool.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _rows.Keys.Where(id => !retainedIds.Contains(id)).ToArray())
        {
            _rowOwners[id].Children.Remove(_rows[id]); _rowOwners.Remove(id);
            _rows.Remove(id); _rendered.Remove(id);
        }
        foreach (var tool in _state.Tools)
        {
            if (_rendered.TryGetValue(tool.Id, out var last) && last == tool) continue;
            _rendered[tool.Id] = tool;
            if (!_rows.TryGetValue(tool.Id, out var row))
            { row = Disclosure("", new StackPanel()); row.Expanding += (_, _) => _inspect(); _rows[tool.Id] = row; }
            var category = ChatActivityPresentation.Category(tool.Kind);
            if (!_groupPanels.TryGetValue(category, out var panel))
            {
                panel = new StackPanel { Spacing = DeskTheme.Space4 };
                var group = Disclosure(category, panel); group.Expanding += (_, _) => _inspect();
                _groupPanels[category] = panel; _groups[category] = group; _tools.Children.Add(group);
            }
            if (!_rowOwners.TryGetValue(tool.Id, out var owner) || owner != panel)
            {
                owner?.Children.Remove(row);
                panel.Children.Add(row); _rowOwners[tool.Id] = panel;
            }
            var title = ToolDisplayTitle(tool);
            row.Visibility = title is null ? Visibility.Collapsed : Visibility.Visible;
            var heading = Text(Mark(tool.Status) + "  " + title + " · " + Label(tool.Status), DeskTheme.SizeCaption);
            heading.FontFamily = DeskTheme.MonoFont;
            heading.Foreground = DeskTheme.StatusForeground(tool.Status);
            row.Header = heading;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row, title + " · " + Label(tool.Status));
            var details = new StackPanel { Spacing = DeskTheme.Space8 };
            if (!string.IsNullOrWhiteSpace(tool.Output)) details.Children.Add(TerminalText(Limit(tool.Output)));
            if (!string.IsNullOrWhiteSpace(tool.Diagnostics)) details.Children.Add(new ChatDisclosure("Diagnostics", TerminalText(Limit(tool.Diagnostics))));
            if (!string.IsNullOrWhiteSpace(tool.Input) && tool.Input != "{}") details.Children.Add(TerminalText("Input\n" + Limit(tool.Input)));
            if (details.Children.Count == 0) details.Children.Add(Text("No additional details supplied.", DeskTheme.SizeCaption));
            row.Content = details;
        }
        foreach (var (category, panel) in _groupPanels)
        {
            var visible = panel.Children.Count(child => child.Visibility == Visibility.Visible);
            _groups[category].Header = ChatActivityPresentation.GroupLabel(category, visible);
            _groups[category].Visibility = visible == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_activity, (string)_activity.Header);
        _changesChanged(this);
        if (_tui) PresentTui(true);
    }
    private void UpdateExecutionNotice()
    {
        var startup = _state.Tools.FirstOrDefault(tool => tool.Title.StartsWith("Windows could not start this command", StringComparison.Ordinal));
        _executionNotice.Text = _state.Phase == "Waiting for your approval" ? "Waiting for your approval. The requested action has not been authorized."
            : _state.ExecutionNotice.Length > 0 ? _state.ExecutionNotice
            : startup is not null ? startup.Title
            : _state.FailedTools > 0 ? $"{_state.FailedTools} tool(s) failed. Expand Activity for details." : "";
        if (_state.ChangeDetailsTruncated)
            _executionNotice.Text += (_executionNotice.Text.Length > 0 ? "\n" : "")
                + "Some file-change details exceeded the retention limit. The reported file count is preserved; missing file snapshots are unavailable.";
        if (_changeArchiveWarning is not null)
            _executionNotice.Text += (_executionNotice.Text.Length > 0 ? "\n" : "") + _changeArchiveWarning;
        _executionNotice.Visibility = _executionNotice.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void EndThinking()
    {
        if (_thinkingSince is not { } since) return;
        _reasoningTime += _clock.Elapsed - since; _thinkingSince = null;
    }
    private static bool KeepInline(string text) => text.Contains('?') || new[] {
        "please", "need you", "need your", "approval", "choose", "confirm", "provide", "attach"
    }.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
    private void PresentTui(bool rebuildTools)
    {
        if (!_tui) return;
        var showThinking = _thinkingOpen && _state.HasDisplayedReasoning;
        _activity.Visibility = Visibility.Collapsed;
        _work.Visibility = Visibility.Collapsed;
        _showThinking.FontFamily = DeskTheme.UiFont;
        _showThinking.Visibility = _state.HasDisplayedReasoning ? Visibility.Visible : Visibility.Collapsed;
        _clearReasoning.Visibility = _state.HasDisplayedReasoning ? Visibility.Visible : Visibility.Collapsed;
        _showThinking.Content = _thinkingOpen ? "Hide reasoning" : "Reasoning";
        if (showThinking)
        {
            _details.Visibility = Visibility.Visible;
            _reasoning.Visibility = Visibility.Visible;
            _reasoningPreview.Visibility = Visibility.Collapsed;
            _reasoning.SetExpanded(true);
            _fullReasoning.SetExpanded(true);
        }
        else
        {
            _reasoningPreview.Visibility = Visibility.Visible;
            if (_fullReasoning.IsExpanded) _fullReasoning.SetExpanded(false);
            if (_reasoning.IsExpanded) _reasoning.SetExpanded(false);
            _reasoning.Visibility = Visibility.Collapsed;
            _details.Visibility = Visibility.Collapsed;
        }
        if (rebuildTools) RebuildTuiTools();
        _tuiTools.Visibility = _tuiTools.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var block in _responses.Children.OfType<TextBlock>().ToArray())
        {
            block.Visibility = Visibility.Collapsed;
            block.FontFamily = DeskTheme.MonoFont;
            PaintMarkdown(block);
        }
        foreach (var block in _earlierText.Children.OfType<TextBlock>().ToArray()) PaintMarkdown(block, _earlierText);
        if (_finished)
        {
            _worked.Text = GrokTuiPresentation.WorkedFor(_clock.Elapsed);
            _worked.FontFamily = DeskTheme.MonoFont;
            _worked.Visibility = Visibility.Visible;
        }
        else _worked.Visibility = Visibility.Collapsed;
    }

    private void RebuildTuiTools()
    {
        var stamp = string.Join("\u001e", _state.Tools.Select(tool =>
            string.Join("\u001f", tool.Id, tool.Status, tool.Kind, tool.Title, tool.Output, tool.Input, tool.Diagnostics, tool.Changes.Length.ToString())));
        if (stamp == _toolStamp) return;
        _toolStamp = stamp;
        var alive = _state.Tools.Select(tool => tool.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in _openToolLines.Where(id => !alive.Contains(id)).ToArray()) _openToolLines.Remove(gone);
        _tuiTools.Children.Clear();
        foreach (var tool in _state.Tools)
        {
            var title = ToolDisplayTitle(tool);
            if (title is null) continue;
            var id = tool.Id;
            var line = new TextBlock
            {
                Text = GrokTuiPresentation.ToolLine(title, tool.Status, tool.Kind),
                FontSize = DeskTheme.SizeCaption, Foreground = DeskTheme.StatusForeground(tool.Status), FontFamily = DeskTheme.MonoFont,
                TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
                LineHeight = DeskTheme.SizeCaption * 1.5
            };
            var detail = new TextBlock
            {
                Text = TuiToolDetail(tool), FontSize = DeskTheme.SizeCode, Foreground = DeskTheme.Muted,
                FontFamily = DeskTheme.MonoFont, TextWrapping = TextWrapping.Wrap, LineHeight = DeskTheme.SizeCode * 1.5,
                IsTextSelectionEnabled = true, Margin = new Thickness(DeskTheme.Space16, DeskTheme.Space4, 0, DeskTheme.Space8),
                Visibility = _openToolLines.Contains(id) ? Visibility.Visible : Visibility.Collapsed
            };
            var button = new Button
            {
                Content = line, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            StyleQuietButton(button);
            button.FontFamily = DeskTheme.MonoFont;
            button.Padding = new Thickness(0, DeskTheme.Space4, 0, DeskTheme.Space4);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, line.Text);
            button.Click += (_, _) =>
            {
                if (!_openToolLines.Add(id)) _openToolLines.Remove(id);
                detail.Visibility = _openToolLines.Contains(id) ? Visibility.Visible : Visibility.Collapsed;
                _inspect();
            };
            var row = new StackPanel();
            row.Children.Add(button);
            row.Children.Add(detail);
            _tuiTools.Children.Add(row);
        }
    }

    private static string TuiToolDetail(CursorToolActivity tool)
    {
        var parts = new List<string>();
        // A compact search summary must not discard the original tool title.
        if (!IsIncompleteRecipientLabel(tool.Title)
            && !GrokTuiPresentation.ToolLine(tool.Title, tool.Status, tool.Kind).Contains(tool.Title, StringComparison.Ordinal))
            parts.Add(tool.Title);
        if (!string.IsNullOrWhiteSpace(tool.Output)) parts.Add(Limit(tool.Output));
        if (!string.IsNullOrWhiteSpace(tool.Input) && tool.Input != "{}") parts.Add("Input\n" + Limit(tool.Input));
        if (!string.IsNullOrWhiteSpace(tool.Diagnostics)) parts.Add(Limit(tool.Diagnostics));
        return parts.Count == 0 ? "No additional details supplied." : string.Join("\n\n", parts);
    }

    private void PaintMarkdown(TextBlock block, Panel? owner = null)
    {
        // Raw TextBlocks remain the source for response copying and progress retention.
        owner ??= _responses;
        block.Visibility = Visibility.Collapsed;
        var font = _tui ? DeskTheme.MonoFont : DeskTheme.UiFont;
        if (!_markdown.TryGetValue(block, out var rich))
        {
            rich = new RichTextBlock
            {
                FontFamily = font, FontSize = DeskTheme.SizeBody, Foreground = DeskTheme.Text,
                FontWeight = FontWeights.Normal,
                LineHeight = DeskTheme.SizeBody * 1.55, TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            };
            rich.SizeChanged += (_, _) => FitCodeWidth(rich);
            _markdown[block] = rich;
            var index = owner.Children.IndexOf(block);
            if (index >= 0) owner.Children.Insert(index + 1, rich);
        }
        rich.Visibility = Visibility.Visible;
        rich.FontFamily = font;
        var signature = (_tui ? "terminal\n" : "classic\n") + block.Text;
        if (rich.Tag as string == signature) return;
        rich.Tag = signature;
        rich.Blocks.Clear();
        foreach (var item in GrokTuiPresentation.Markdown(block.Text ?? ""))
        {
            if (item.Style == GrokTuiStyle.CodeBlock)
            {
                var codeText = string.Concat(item.Runs.Select(run => run.Text));
                var code = new RichTextBlock
                {
                    FontFamily = DeskTheme.MonoFont, FontSize = DeskTheme.SizeCode, Foreground = DeskTheme.Text,
                    LineHeight = DeskTheme.SizeCode * 1.55, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true
                };
                var body = new Paragraph();
                body.Inlines.Add(new Run { Text = codeText, FontFamily = DeskTheme.MonoFont });
                code.Blocks.Add(body);
                var copyCode = CodeCopyButton(codeText);
                var contents = new StackPanel { Spacing = DeskTheme.Space8, Children = { copyCode, code } };
                var box = new Border
                {
                    Background = DeskTheme.Surface, BorderBrush = DeskTheme.Divider, BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(DeskTheme.Radius6), Padding = new Thickness(DeskTheme.Space12, DeskTheme.Space8, DeskTheme.Space12, DeskTheme.Space12),
                    Margin = new Thickness(0, DeskTheme.Space4, 0, DeskTheme.Space4),
                    HorizontalAlignment = HorizontalAlignment.Stretch, Child = contents
                };
                var holder = new Paragraph { Margin = new Thickness(0, DeskTheme.Space4, 0, DeskTheme.Space4) };
                holder.Inlines.Add(new InlineUIContainer { Child = box });
                rich.Blocks.Add(holder);
                continue;
            }
            var paragraph = new Paragraph
            {
                FontWeight = FontWeights.Normal,
                Margin = new Thickness(item.Style == GrokTuiStyle.Bullet ? DeskTheme.Space8 : 0,
                    item.Style == GrokTuiStyle.Heading ? DeskTheme.Space8 : 0, 0, DeskTheme.Space4)
            };
            if (item.Style == GrokTuiStyle.Heading)
            {
                paragraph.FontSize = DeskTheme.SizeHeading;
                paragraph.FontWeight = FontWeights.SemiBold;
                paragraph.Foreground = DeskTheme.Text;
            }
            if (item.Style == GrokTuiStyle.Bullet) paragraph.Inlines.Add(new Run { Text = "• ", FontFamily = font });
            foreach (var run in item.Runs)
            {
                var inline = new Run { Text = run.Text, FontWeight = FontWeights.Normal, FontFamily = run.Style == GrokTuiStyle.InlineCode ? DeskTheme.MonoFont : font };
                if (run.Style == GrokTuiStyle.Bold) inline.FontWeight = FontWeights.SemiBold;
                if (run.Style == GrokTuiStyle.Heading || item.Style == GrokTuiStyle.Heading)
                {
                    inline.FontWeight = FontWeights.SemiBold;
                    inline.Foreground = DeskTheme.Text;
                }
                paragraph.Inlines.Add(inline);
            }
            rich.Blocks.Add(paragraph);
        }
        FitCodeWidth(rich);
    }

    private Button CodeCopyButton(string code)
    {
        var button = new Button { Content = "Copy code", HorizontalAlignment = HorizontalAlignment.Right, IsEnabled = code.Length > 0 };
        StyleQuietButton(button);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, "Copy code block");
        button.Click += async (_, _) =>
        {
            try
            {
                await ChatClipboard.CopyAsync(button.DispatcherQueue, code);
                if (button.XamlRoot is null) return;
                button.Content = "Copied";
                await Task.Delay(1500);
                if (button.XamlRoot is not null) button.Content = "Copy code";
            }
            catch { if (button.XamlRoot is not null) button.Content = "Copy failed — retry"; }
        };
        return button;
    }

    private static void FitCodeWidth(RichTextBlock target)
    {
        var width = target.ActualWidth - DeskTheme.Space4;
        if (width <= DeskTheme.Space24) return;
        foreach (var paragraph in target.Blocks.OfType<Paragraph>())
            foreach (var inline in paragraph.Inlines.OfType<InlineUIContainer>())
                if (inline.Child is FrameworkElement child && Math.Abs(child.MaxWidth - width) > 1)
                {
                    child.MaxWidth = width;
                    child.Width = width;
                }
    }

    private static bool IsIncompleteRecipientLabel(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return true;
        // Match only empty recipient captions and their punctuation-only streaming variants.
        // A real recipient, even one followed by an ellipsis, remains visible.
        var caption = title.Trim().TrimEnd(':', '.', '…', ' ', '\t', '\r', '\n');
        return caption.Equals("Message to", StringComparison.OrdinalIgnoreCase)
            || caption.Equals("Message t", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ToolDisplayTitle(CursorToolActivity tool)
    {
        if (!IsIncompleteRecipientLabel(tool.Title)) return tool.Title;
        var hasDetails = !string.IsNullOrWhiteSpace(tool.Output) || !string.IsNullOrWhiteSpace(tool.Diagnostics)
            || (!string.IsNullOrWhiteSpace(tool.Input) && tool.Input.Trim() != "{}") || tool.Changes.Length > 0;
        // Hide an empty placeholder only; substantive content and exceptional states remain inspectable.
        return hasDetails || tool.Status is "failed" or "error" or "blocked" or "unconfirmed" or "cancelled" ? "Tool details" : null;
    }

    private static TextBlock TerminalText(string text)
    {
        var block = Text(text, DeskTheme.SizeCode);
        block.FontFamily = DeskTheme.MonoFont;
        return block;
    }

    internal static void StyleQuietButton(Button button)
    {
        var transparent = DeskTheme.Transparent;
        button.FontFamily = DeskTheme.UiFont;
        button.FontSize = DeskTheme.SizeCaption;
        button.Foreground = DeskTheme.Muted;
        button.Background = transparent;
        button.BorderBrush = transparent;
        button.BorderThickness = new Thickness(0);
        button.CornerRadius = new CornerRadius(DeskTheme.Radius6);
        button.Padding = new Thickness(DeskTheme.Space8, DeskTheme.Space4, DeskTheme.Space8, DeskTheme.Space4);
        button.MinHeight = DeskTheme.Space16 + DeskTheme.Space12;
        button.Resources["ButtonBackgroundPointerOver"] = DeskTheme.Hover;
        button.Resources["ButtonBackgroundPressed"] = DeskTheme.Pressed;
        button.Resources["ButtonForegroundPointerOver"] = DeskTheme.Text;
        button.Resources["ButtonForegroundPressed"] = DeskTheme.Text;
        button.Resources["ButtonBorderBrushPointerOver"] = transparent;
        button.Resources["ButtonBorderBrushPressed"] = transparent;
    }

    private static string Limit(string text) => text.Length <= 10000 ? text : text[..10000] + "\n[Display truncated]";
    private static string Mark(string status) => status switch { "completed" => "✓", "failed" or "blocked" => "!", "in_progress" => "◌", "cancelled" or "unconfirmed" => "−", _ => "·" };
    private static string Label(string status) => status switch { "blocked" => "blocked · did not run", "in_progress" => "running", "unconfirmed" => "completion unconfirmed", _ => status };
    private static TextBlock Text(string text, double size) => new() { Text = text, FontFamily = DeskTheme.UiFont, FontSize = size, Foreground = DeskTheme.Text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, LineHeight = size * 1.5 };
    private static ChatDisclosure Disclosure(string title, UIElement content) => new(title, content);
}

/// <summary>Inline disclosure: details participate in the conversation's single scroll surface.</summary>
internal sealed class ChatDisclosure : StackPanel
{
    private readonly Button _toggle = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
        Padding = new Thickness(0, DeskTheme.Space4, 0, DeskTheme.Space4)
    };
    private readonly TextBlock _chevron = new()
    {
        Text = "›", FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeBody,
        Foreground = DeskTheme.Muted, VerticalAlignment = VerticalAlignment.Center
    };
    private readonly ContentControl _label = new()
    {
        FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeCaption,
        Foreground = DeskTheme.Muted, HorizontalContentAlignment = HorizontalAlignment.Stretch
    };
    private readonly Border _body = new()
    {
        Visibility = Visibility.Collapsed, Margin = new Thickness(DeskTheme.Space12, DeskTheme.Space4, 0, DeskTheme.Space12)
    };
    private object _header = "";
    private bool _expanded;
    public bool IsExpanded => _expanded;
    public event EventHandler? Expanding;
    public event EventHandler? Collapsing;
    public object Header
    {
        get => _header;
        set
        {
            _header = value;
            if (value is TextBlock headingText) headingText.IsTextSelectionEnabled = false;
            _label.Content = value is string caption ? new TextBlock
            {
                Text = caption, FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeCaption,
                Foreground = DeskTheme.Muted, TextWrapping = TextWrapping.Wrap
            } : value;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_toggle,
                (_expanded ? "Collapse " : "Expand ") + (value is TextBlock text ? text.Text : value.ToString() ?? "Details"));
        }
    }
    public UIElement? Content { get => _body.Child; set => _body.Child = value; }
    public ChatDisclosure(string title, UIElement content)
    {
        ChatActivityView.StyleQuietButton(_toggle);
        _toggle.Padding = new Thickness(0, DeskTheme.Space4, 0, DeskTheme.Space4);
        var line = new Grid { ColumnSpacing = DeskTheme.Space8 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition());
        line.Children.Add(_chevron); Grid.SetColumn(_label, 1); line.Children.Add(_label);
        _toggle.Content = line; Header = title; Content = content;
        _toggle.Click += (_, _) => SetExpanded(!_expanded);
        Children.Add(_toggle); Children.Add(_body);
    }
    public void SetExpanded(bool expanded)
    {
        if (_expanded == expanded) return;
        _expanded = expanded;
        _body.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        if (expanded)
        {
            var fade = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = .9, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(DeskTheme.MotionMilliseconds)),
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut }
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fade, _body);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fade, "Opacity");
            var animation = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            animation.Children.Add(fade); animation.Begin();
        }
        _chevron.Text = expanded ? "⌄" : "›"; Header = _header;
        if (expanded) Expanding?.Invoke(this, EventArgs.Empty);
        else Collapsing?.Invoke(this, EventArgs.Empty);
    }
}
