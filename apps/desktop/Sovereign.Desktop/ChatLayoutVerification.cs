using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sovereign.Desktop.Client;
using System.Text.Json;
using static Sovereign.Desktop.MainWindow;

namespace Sovereign.Desktop;

public sealed partial class MainWindow
{
    public async Task RunChatLayoutVerificationAsync(string output)
    {
        ValidateCodexQaProfile(_vm.ProfileDirectory);
        Directory.CreateDirectory(output);
        if (Environment.GetEnvironmentVariable("SOVEREIGN_QA_UI_POLISH") is "before" or "after")
        {
            await RunUiPolishCaptureAsync(output);
            return;
        }
        if (Environment.GetEnvironmentVariable("SOVEREIGN_QA_PROJECT_DIALOG") == "1")
        {
            await RunProjectDialogVerificationAsync(output);
            return;
        }
        if (Environment.GetEnvironmentVariable("SOVEREIGN_QA_HELPER_PANELS_REOPEN") == "1")
        {
            await RunHelperPanelsReopenVerificationAsync(output);
            return;
        }
        if (Environment.GetEnvironmentVariable("SOVEREIGN_QA_HELPER_CARD_FOLD") == "1")
        {
            await RunHelperCardFoldVerificationAsync(output);
            return;
        }
        try
        {
            var displayOnly = Environment.GetEnvironmentVariable("SOVEREIGN_QA_SUBAGENT_DISPLAY") == "1";
            if (displayOnly && Environment.GetEnvironmentVariable("SOVEREIGN_QA_PROJECT_LIVE") == "1")
                throw new IOException("Subagent display QA does not allow the live-provider environment.");
            Title = _vm.AppTitle + " · synthetic chat UI QA";
            await Task.Delay(400);
            OpenChat();
            var checks = await _currentChat.VerifyLayoutAsync(async name => { await Task.Delay(250); await CapturePng(Path.Combine(output, name + ".png")); },
                width => AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 850)), output);
            File.WriteAllText(Path.Combine(output, "receipt.json"), JsonSerializer.Serialize(new { passed = true, checks,
                mode = displayOnly ? "subagent-display-only" : "chat-layout", provider_calls_allowed = !displayOnly && Environment.GetEnvironmentVariable("SOVEREIGN_QA_PROJECT_LIVE") == "1",
                clipboard_actions_allowed = !displayOnly,
                live_project_providers = !displayOnly && Environment.GetEnvironmentVariable("SOVEREIGN_QA_PROJECT_LIVE") == "1" ? new[] { "glm", "moonshot" } : Array.Empty<string>() }));
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(output, "receipt.json"), JsonSerializer.Serialize(new { passed = false, error = ex.ToString() })); }
        finally { Close(); }
    }
}

internal sealed partial class ChatConversation
{
    internal async Task<List<string>> VerifyLayoutAsync(Func<string, Task> capture, Action<int> resize, string? evidenceDirectory = null)
    {
        var checks = new List<string>();
        _classicChat = true;
        ApplyChatChrome();
        _saved = _saved with { OpenSubagentChatPanels = false };
        CloseAllSubagentPanels();
        var displayOnly = Environment.GetEnvironmentVariable("SOVEREIGN_QA_SUBAGENT_DISPLAY") == "1";
        var progress = _composerProgress ?? throw new IOException("Missing composer progress");
        void Check(bool condition, string name) { if (!condition) throw new IOException(name); checks.Add(name); }
        var composer = ComposerHost(_chatInput);
        double Left(FrameworkElement e) => e.TransformToVisual(_chatPage).TransformPoint(new Windows.Foundation.Point()).X;
        const string highFast = "grok-4.7[context=256k,reasoning_effort=high,fast=true]";
        const string highFastWide = "grok-4.7[context=300k,reasoning_effort=high,fast=true]";
        const string unknown = "nonesuch-model-v2[context=8k]";
        BindChatModels([
            (highFast, "grok-4.7"),
            (highFastWide, "Grok 4.7 \u00C2\u00B7 grok-4.7[context=300k,effort=high,fast=true]"),
            (unknown, null)
        ], false);
        _saved = _saved with { Model = highFastWide };
        RestoreModelSelection();
        Check(SelectedChatModel == highFastWide, "saved model restores by provider id");
        Check(SelectedChatLabel == "Grok 4.7 High Fast 300k", "restored label stays compact and distinct");
        Check(!SelectedChatLabel.Contains('\u00C2') && !SelectedChatLabel.Contains('['), "restored label has no mojibake or raw options");
        Check(SelectChatModel(highFast) && SelectedChatModel == highFast && SelectedChatLabel == "Grok 4.7 High Fast 256k", "selection keeps the original routing id");
        Check(_chatModel.Items.OfType<ComboBoxItem>().Select(item => (string)item.Content).Distinct().Count() == 3, "duplicate names stay distinct variants");
        Check(_chatModel.Items.OfType<ComboBoxItem>().Any(item => (string)item.Tag == unknown && (string)item.Content == "Nonesuch Model V2"), "unknown ids use a readable fallback");
        Check(_chatModel.Items.OfType<ComboBoxItem>().All(item => (ToolTipService.GetToolTip(item) as string)!.Contains("Provider ID: " + (string)item.Tag!)), "technical details stay in the tooltip");
        var pickerLines = _chatModel.Items.OfType<ComboBoxItem>()
            .Select(item => (string)item.Content! + "\r\n  " + (string)item.Tag!).ToArray();
        if (evidenceDirectory is not null)
            File.WriteAllText(Path.Combine(evidenceDirectory, "model-picker-items.txt"),
                "selected=" + SelectedChatLabel + "\r\nid=" + SelectedChatModel + "\r\n\r\n" + string.Join("\r\n\r\n", pickerLines));
        _chatStatus.Text = "Rendered picker labels (routing IDs stay underneath):\r\n" + string.Join("\r\n", pickerLines);
        var pickerList = new StackPanel { Spacing = 6 };
        foreach (var item in _chatModel.Items.OfType<ComboBoxItem>())
            pickerList.Children.Add(new TextBlock { Text = (string)item.Content, FontSize = 16 });
        var pickerOverlay = new Border { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 24, 160), Padding = new Thickness(16, 12, 16, 12), CornerRadius = new CornerRadius(12),
            Background = Ink(34, 34, 37), Child = pickerList };
        _chatPage.Children.Add(pickerOverlay);
        _chatModel.IsDropDownOpen = true; await Task.Delay(400); await capture("model-picker-open");
        _chatModel.IsDropDownOpen = false; _chatPage.Children.Remove(pickerOverlay); await Task.Delay(150);
        _chatModel.Items.Clear();
        _chatModel.Items.Add("Grok 4.7 High Fast"); _chatModel.SelectedIndex = 0;
        _chatMessages.Children.Clear();
        var bubble = UserBubble("Synthetic user message: checking alignment, not calling a provider.");
        _chatMessages.Children.Add(bubble);
        var turn = new ChatActivityView("GLM / Kimi / Cursor — shared layout QA", UpdateChangesPanel, () => { }, SetComposerStatus, () => { });
        TrackChatTurn(turn); _chatMessages.Children.Add(turn.Root);
        void CheckTranscriptBounds(string stage)
        {
            var viewportRight = Left(_chatScroll!) + _chatScroll!.ViewportWidth;
            Check(_chatMessages.ActualWidth <= _chatScroll.ViewportWidth + 3, stage + " transcript is measured within its viewport");
            Check(Left(turn.Root) + turn.Root.ActualWidth <= viewportRight + 3, stage + " assistant text stays within the visible transcript");
            Check(Left(bubble) + bubble.ActualWidth <= viewportRight + 3, stage + " user bubble stays within the visible transcript");
        }
        turn.Append(string.Join("\n", Enumerable.Repeat("Synthetic assistant content to exercise transcript scrolling and long responses.", 35)));
        _chatCancellation = new CancellationTokenSource(); SetChatBusy(true);
        resize(1440); await Task.Delay(400);
        Check(_composerSpinner.IsActive && progress.Visibility == Visibility.Visible, "running progress visible");
        var ringPoint = _composerSpinner.TransformToVisual(_chatModel).TransformPoint(new());
        Check(ringPoint.X >= 0 && ringPoint.Y >= 0 && ringPoint.X + _composerSpinner.ActualWidth <= _chatModel.ActualWidth && ringPoint.Y + _composerSpinner.ActualHeight <= _chatModel.ActualHeight, "spinner inside model picker");
        Check(((StackPanel)composer).Children.Count == 3, "one compact status line above composer");
        Check(_liveStatus.Visibility == Visibility.Visible && _liveStatus.Text.Contains("Grok"), "live model status visible");
        Check(Math.Abs(Left(_chatMessages) - Left(composer)) < 3, "assistant left matches composer");
        Check(Left(bubble) + bubble.ActualWidth <= Left(composer) + composer.ActualWidth + 3, "user bubble stays inside composer column");
        Check(bubble.HorizontalAlignment == HorizontalAlignment.Stretch && Math.Abs(Left(bubble) - Left(composer)) < 3
            && Math.Abs(bubble.ActualWidth - composer.ActualWidth) < 3, "prompt bar spans the full composer column");
        Check(bubble.Child is TextBlock promptText && promptText.Text == "› Synthetic user message: checking alignment, not calling a provider.",
            "prompt bar has one terminal-style marker and preserves the message text");
        CheckTranscriptBounds("closed panel");
        var savedBeforeClear = JsonSerializer.Serialize(_saved);
        var answerBeforeClear = turn.DisplayReply;
        var toolsBeforeClear = turn.ObservedToolSteps;
        var cancellationBeforeClear = _chatCancellation;
        var clearButton = _chatClearReasoning ?? throw new IOException("Missing Clear reasoning control");
        turn.Observe(new("reasoning", string.Concat(Enumerable.Repeat("Older reasoning for the display-limit check.\n", 900))));
        await Task.Delay(250);
        Check(turn.ReasoningDisplayLimitReached && clearButton.IsEnabled, "clear control available at the reasoning display limit");
        Check(ReferenceEquals(clearButton.Parent, _chatLatest!.Parent), "clear reasoning sits beside jump to latest");
        turn.ShowReasoningForVerification(true);
        await Task.Delay(200);
        _chatScroll!.ChangeView(null, _chatScroll.ScrollableHeight, null, true);
        PauseFollow();
        await capture("reasoning-limit");
        turn.Observe(new("reasoning", "An already-queued old update."));
        var clearPeer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(clearButton);
        ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)clearPeer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
        await Task.Delay(120);
        Check(turn.DisplayedReasoning == "" && !turn.ReasoningDisplayLimitReached, "clear removes old text and the display-limit marker");
        Check(_chatBusy && ReferenceEquals(_chatCancellation, cancellationBeforeClear) && !_chatCancellation!.IsCancellationRequested, "clear keeps the running task and cancellation token untouched");
        Check(turn.DisplayReply == answerBeforeClear && turn.ObservedToolSteps == toolsBeforeClear && JsonSerializer.Serialize(_saved) == savedBeforeClear, "clear leaves answers activity and saved conversation unchanged");
        turn.Observe(new("reasoning", "Fresh reasoning appears after clearing — 中文 😀"));
        await Task.Delay(180);
        Check(turn.DisplayedReasoning == "Fresh reasoning appears after clearing — 中文 😀" && !turn.ReasoningDisplayLimitReached, "new reasoning displays after limit reset");
        Check(clearButton.IsEnabled && clearButton.Visibility == Visibility.Visible, "clear control is ready for the next reasoning window");
        _chatScroll!.ChangeView(null, 0, null, true);
        PauseFollow();
        await Task.Delay(150);
        await capture("reasoning-after-clear");
        ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)clearPeer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
        turn.ShowReasoningForVerification(false);
        _chatScroll!.ChangeView(null, 0, null); await capture("running-scrolled");
        Check(progress.ActualHeight > 0 && progress.TransformToVisual(_chatPage).TransformPoint(new()).Y > 0, "progress remains outside scrolling transcript");
        SetChangesOpen(true);
        const string nativePatch = "@@ -1 +1 @@\n-before\n+after";
        ShowChangeInPanel(new("native-codex-proof.md", null, "", UnifiedDiff: nativePatch));
        Check(_changeDetail.Children.OfType<TextBlock>().Any(block => block.Text == nativePatch)
            && !_changeDetail.Children.OfType<TextBlock>().Any(block => block.Text == "Before" || block.Text == "After"), "native Codex changes render as a patch without invented file snapshots");
        await Task.Delay(250); await capture("changes-open");
        CheckTranscriptBounds("Changes open");
        SetChangesOpen(false); BeginSubagentTurn();
        var started = DateTimeOffset.UtcNow.AddMinutes(-8);
        var helper = new CursorSubagent("qa-helper", "Art QA", "Inspect the synthetic artwork fixture", "running", "Synthetic helper finding: INDIGO-582",
            CurrentWork: "Checking the artwork fixture and reporting visual issues", StartedAt: started, UpdatedAt: DateTimeOffset.UtcNow,
            ModelId: highFast, ModelName: "Grok 4.7", ModelProvider: "xai", ModelSource: "runtime model update");
        ObserveSubagent(new("subagent", Subagent: helper));
        Check(_changesOpen && _showAgents, "first helper launch opens the subagents tab automatically");
        ObserveSubagent(new("subagent", Subagent: new("qa-interface", "Interface", "Review the synthetic interface fixture", "running", "",
            CurrentWork: "Reviewing the interface layout and composer controls", StartedAt: started.AddSeconds(-8), UpdatedAt: DateTimeOffset.UtcNow,
            ModelId: "grok-4.6", ModelName: "Grok 4.6", ModelProvider: "xai", ModelSource: "runtime model update")));
        ObserveSubagent(new("subagent", Subagent: new("qa-backend", "Backend", "Review synthetic API rules", "running", "",
            CurrentWork: "Checking the API rules and response contracts", StartedAt: started.AddSeconds(-17), UpdatedAt: DateTimeOffset.UtcNow)));
        await Task.Delay(250);
        Check(_subagentList.Visibility == Visibility.Visible && _helperDetailHost!.Visibility == Visibility.Visible
            && Grid.GetRowSpan(_changeFilesScroll!) == 1, "subagent list stays on the right and the selected helper conversation uses the space below it");
        Check(_changeDetailScroll!.Visibility == Visibility.Collapsed, "file detail hidden in agents tab");
        Check(_subagentSummary.Text == "Active · 3" && _subagentsToggle.Content.ToString() == "Subagents · 3 working", "visible subagent tab and summary show three working helpers");
        Check(_subagentViews[helper.SessionId].State.Text == "Working"
            && _subagentViews[helper.SessionId].Work.Visibility == Visibility.Collapsed && _subagentViews[helper.SessionId].Elapsed.Text.StartsWith("8m"), "compact helper status shows elapsed time; current work stays in its log");
        Check(_subagentViews[helper.SessionId].Model.Text == "xAI · Grok 4.7 High Fast"
            && _subagentViews[helper.SessionId].Provider.Visibility == Visibility.Collapsed && _subagentViews[helper.SessionId].Requested.Visibility == Visibility.Collapsed,
            "collapsed helper shows its reported provider and model in one identity line");
        Check(_subagentViews["qa-interface"].Model.Text == "xAI · Grok 4.6" && _subagentViews["qa-interface"].Model.Text != _subagentViews[helper.SessionId].Model.Text,
            "recorded Grok 4.6 stays distinct from the selected parent Grok 4.7");
        Check(_subagentViews["qa-backend"].Model.Text == "Model not reported", "missing helper metadata never falls back to the parent model");
        await capture("agents-running");
        CheckTranscriptBounds("Subagents open");
        var helperView = _subagentViews[helper.SessionId];
        helperView.Root.IsExpanded = true;
        Check(helperView.Details.Text.Contains("Identity: xAI · Grok 4.7 High Fast") && helperView.Details.Text.Contains("Source: runtime model update.") && helperView.Details.Text.Contains("Started: " + started.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture))
            && helperView.Details.Text.Contains("Runtime: " + helperView.Elapsed.Text) && helperView.State.Text == "Working" && helperView.Details.Text.Contains("Checking the artwork fixture"),
            "expanded helper shows recorded identity and source, start time, live runtime and lifecycle");
        var initialElapsed = helperView.Elapsed.Text;
        ObserveSubagent(new("subagent", Subagent: helper with { CurrentWork = "Checking the second artwork fixture", Output = "Updated synthetic helper finding: INDIGO-582" }));
        Check(ReferenceEquals(helperView, _subagentViews[helper.SessionId]) && helperView.Root.IsExpanded, "live updates preserve expanded helper details");
        await Task.Delay(1250); RefreshSubagentElapsed();
        Check(helperView.Elapsed.Text != initialElapsed, "elapsed timer advances without rebuilding the helper row");
        SetChangesOpen(false);
        ObserveSubagent(new("subagent", Subagent: helper with { CurrentWork = "Finishing the fixture review" }));
        ObserveSubagent(new("subagent", Subagent: new("qa-late", "Late helper", "Inspect one more fixture", "running", "", StartedAt: DateTimeOffset.UtcNow)));
        Check(!_changesOpen, "closing the subagents panel stays closed on updates and further helpers in the same response");
        ToggleSubagentsPanel();
        Check(_changesOpen && _showAgents, "top-right subagents button reopens its tab");
        ObserveSubagent(new("subagent", Subagent: helper with { State = "completed", CurrentWork = "Review complete", CompletedAt = started.AddMinutes(8), UpdatedAt = started.AddMinutes(8) }));
        var completedElapsed = helperView.Elapsed.Text;
        await Task.Delay(1100); RefreshSubagentElapsed();
        Check(helperView.State.Text == "Finished" && helperView.Elapsed.Text == completedElapsed, "completed helper keeps its result and fixed duration");
        FinishSubagents("stopped");
        Check(_subagentRows["qa-interface"].State == "stop requested" && _subagentSummary.Text.StartsWith("Active · 0"), "stopped helpers are not shown as running");
        Check(_subagentRows["qa-helper"].State == "completed", "stopping the parent preserves already completed helpers");
        Check(_subagentSummary.Text == "Active · 0   Completed · 1   Stop requested · 3" && !_subagentSummary.Text.Contains("Finished"),
            "requested helper stops are separated from the one confirmed completion");
        const string inquiryKind = "resea" + "rch";
        foreach (var fixture in new[] { ("table", "Fix the report columns", "table.svg"), ("pdf", "Fix PDF table columns", "pdf.svg"),
            ("cleanup", "Strip report run metadata", "cleanup.svg"), (inquiryKind, "Resea" + "rch issuer documentation", inquiryKind + ".svg"), ("code", "Fix a compiler bug", "code.svg") })
        {
            var entry = helper with { SessionId = "qa-avatar-" + fixture.Item1, Name = fixture.Item1 + " helper", Task = fixture.Item2,
                State = "completed", CurrentWork = "Fixture complete", CompletedAt = started.AddMinutes(2), UpdatedAt = started.AddMinutes(2) };
            ObserveSubagent(new("subagent", Subagent: entry));
            var view = _subagentViews[entry.SessionId];
            var blob = SubagentBlobAvatar.Pick(entry.SessionId, entry.Name);
            Check(view.AvatarAssetName == blob.Key && view.Avatar.Visibility == Visibility.Collapsed && view.Initial.Visibility == Visibility.Collapsed,
                fixture.Item1 + " helper uses a stable blob avatar from its id");
        }
        var unnamed = new CursorSubagent("qa-avatar-unknown", "Agenda helper", "Plan a weekend trip", "completed");
        ObserveSubagent(new("subagent", Subagent: unnamed));
        var unnamedBlob = SubagentBlobAvatar.Pick(unnamed.SessionId, unnamed.Name);
        Check(_subagentViews[unnamed.SessionId].AvatarAssetName == unnamedBlob.Key
            && _subagentViews[unnamed.SessionId].Avatar.Visibility == Visibility.Collapsed
            && _subagentViews[unnamed.SessionId].Initial.Visibility == Visibility.Collapsed, "unmatched task uses a blob avatar from its id");
        Check(_subagentViews[unnamed.SessionId].Details.Text.Contains("Identity: Model not reported") && _subagentViews[unnamed.SessionId].Details.Text.Contains("Started: Not recorded")
            && _subagentViews[unnamed.SessionId].Details.Text.Contains("Runtime: Not recorded"), "missing model and times remain explicitly unrecorded");
        var configured = new CursorSubagent("qa-configured-only", "Configured helper", "Plan a trip", "failed",
            StartedAt: started, CompletedAt: started.AddMinutes(1), RequestedModelId: highFast, RequestedModelProvider: "xai",
            RequestedModelSource: "runtime configuration", Failure: "Synthetic provider rejected the configured model.");
        ObserveSubagent(new("subagent", Subagent: configured));
        var configuredView = _subagentViews[configured.SessionId];
        Check(configuredView.Model.Text == "xAI · Grok 4.7 High Fast · configured"
            && configuredView.Provider.Visibility == Visibility.Collapsed && configuredView.Requested.Visibility == Visibility.Collapsed
            && ToolTipService.GetToolTip(configuredView.Model) is string identityDetail && identityDetail.Contains("Execution model was not separately reported."),
            "configured identity uses one qualified line and explains the missing execution report");
        Check(configuredView.Failure.Text == configured.Failure && configuredView.Result.Text == "No result was reported.",
            "recorded helper failure is separate from an absent result");
        await Task.Delay(500);
        foreach (var kind in new[] { "table", "pdf", "cleanup", inquiryKind, "code", "unknown" })
            Check(_subagentViews["qa-avatar-" + kind].AvatarLoaded && !_subagentViews["qa-avatar-" + kind].AvatarFailed, kind + " blob avatar is drawn");
        if (displayOnly)
            foreach (var pair in _subagentViews) pair.Value.Root.Visibility = pair.Key.StartsWith("qa-avatar-", StringComparison.Ordinal)
                ? Visibility.Visible : Visibility.Collapsed;
        await capture("agents-task-pictures");
        foreach (var view in _subagentViews.Values) view.Root.Visibility = Visibility.Visible;
        ShowAgents(false);
        Check(_changeDetailScroll.Visibility == Visibility.Visible && Grid.GetRowSpan(_changeFilesScroll!) == 1, "changes tab restored");
        ShowAgents(true);
        resize(1000); await Task.Delay(300); await capture("agents-narrow");
        Check(Grid.GetRow(_changesPanel) == 2 && Grid.GetColumn(_changesPanel) == 0 && composer.Visibility == Visibility.Visible && composer.ActualHeight > 0,
            "narrow subagents panel keeps the chat composer visible");
        SetChangesOpen(false); await capture("narrow");
        Check(Math.Abs(Left(_chatMessages) - Left(composer)) < 3, "narrow alignment");
        CheckTranscriptBounds("narrow closed panel");
        BeginSubagentTurn();
        ObserveSubagent(new("subagent", Subagent: new("qa-next-turn", "Next response", "Inspect the next fixture", "running", "", StartedAt: DateTimeOffset.UtcNow)));
        Check(_changesOpen && _showAgents, "new response can open the subagents tab after a previous close");
        FinishSubagents("stopped"); SetChangesOpen(false);
        BeginSubagentTurn();
        ObserveSubagent(new("subagent", Subagent: helper with { State = "running", StartedAt = DateTimeOffset.UtcNow, CompletedAt = null, CurrentWork = "Reviewing a follow-up fixture" }));
        Check(_changesOpen && _showAgents && ReferenceEquals(helperView, _subagentViews[helper.SessionId]), "resuming a saved helper opens the new response tab and reuses its row");
        FinishSubagents("stopped"); SetChangesOpen(false);
        var savedBeforeRestore = _saved;
        _saved = _saved with { Turns = [new SavedChatTurn("Synthetic restore", "Grok", "", "stopped", [
            new("subagent", Subagent: new("qa-restored-running", "Interrupted helper", "Restore a saved running fixture", "running", "Saved intermediate finding", StartedAt: started, UpdatedAt: started.AddMinutes(2))),
            new("subagent", Subagent: new("qa-restored-complete", "Saved helper", "Fix PDF columns in a saved fixture", "completed", "Saved final finding", StartedAt: started, CompletedAt: started.AddMinutes(1),
                ModelId: "grok-4.6", ModelName: "Grok 4.6", ModelProvider: "xai", ModelSource: "runtime model update"))])] };
        _saved = JsonSerializer.Deserialize<SavedChat>(JsonSerializer.Serialize(_saved)) ?? throw new IOException("Saved helper fixture failed its JSON restore.");
        _restoring = true; RestoreSubagents(); _restoring = false;
        Check(!_changesOpen && _subagentRows["qa-restored-running"].State == "disconnected", "restoring interrupted helpers does not reopen the panel or show them as running");
        Check(_subagentRows["qa-restored-complete"].Output == "Saved final finding" && _subagentViews["qa-restored-complete"].Elapsed.Text == "1m 0s", "restoring completed helpers keeps results and duration");
        var restoredHelper = _subagentViews["qa-restored-complete"];
        restoredHelper.Root.IsExpanded = true;
        Check(restoredHelper.Model.Text == "xAI · Grok 4.6" && restoredHelper.AvatarAssetName == SubagentBlobAvatar.Pick("qa-restored-complete", "Saved helper").Key
            && restoredHelper.Details.Text.Contains("Runtime: 1m 0s") && restoredHelper.State.Text == "Finished",
            "JSON-restored helper keeps its own identity, blob avatar, runtime and lifecycle");
        Check(_subagentViews["qa-restored-running"].Model.Text == "Model not reported", "legacy JSON-restored helper keeps its identity unreported");
        var completedBeforeDisconnect = _subagentRows.Values.Count(child => child.State == "completed");
        var interruptedBeforeDisconnect = _subagentRows.Values.Count(child => child.State is "disconnected" or "interrupted" or "unconfirmed");
        ObserveSubagent(new("subagent", Subagent: new("qa-summary-disconnected", "Disconnected fixture", "Review saved fixture", "disconnected")));
        ObserveSubagent(new("subagent", Subagent: new("qa-summary-stopped", "Stopped fixture", "Review saved fixture", "cancelled")));
        ObserveSubagent(new("subagent", Subagent: new("qa-summary-blocked", "Blocked fixture", "Review saved fixture", "blocked")));
        Check(_subagentSummary.Text.Contains("Completed · " + completedBeforeDisconnect)
            && _subagentSummary.Text.Contains("Interrupted · " + (interruptedBeforeDisconnect + 1))
            && _subagentSummary.Text.Contains("Stopped · 1") && _subagentSummary.Text.Contains("Blocked · 1")
            && !_subagentSummary.Text.Contains("Finished"), "disconnected, stopped and blocked helpers retain separate truthful summary counts");
        Check(_subagentViews["qa-summary-disconnected"].State.Text == "Interrupted", "disconnected helper row never claims completion");
        await VerifySubagentChatPanelsAsync(Check, capture, resize);
        _saved = savedBeforeRestore;
        var providerBeforeSubagentCheck = _chatProvider.SelectedIndex;
        var folderBeforeSubagentCheck = _projectFolder;
        _projectFolder = _profileDirectory;
        _chatBusy = false;
        foreach (var provider in new[] { DeskChatConnections.Cursor, DeskChatConnections.GrokBuild })
        {
            _restoring = true; _chatProvider.SelectedIndex = provider; _restoring = false;
            foreach (var behavior in new[] { (CursorProjectMode.Plan, CursorPermissionPolicy.AlwaysAsk), (CursorProjectMode.Agent, CursorPermissionPolicy.AlwaysAsk), (CursorProjectMode.Agent, CursorPermissionPolicy.AlwaysAllow) })
            {
                _saved = _saved with { ProjectMode = behavior.Item1, Permissions = behavior.Item2 };
                UpdateProjectHeader();
                var connection = provider == DeskChatConnections.GrokBuild ? "Grok Build" : "Cursor";
                Check(_subagentOption.Visibility == Visibility.Visible && _subagentOption.IsEnabled && _projectBehavior.Visibility == Visibility.Visible,
                    connection + " project subagents and permissions available in " + behavior.Item1 + " / " + behavior.Item2);
                Check(!_projectDescription.Text.Contains("chat only"), connection + " project header reports project tools");
            }
        }
        _chatBusy = true; _saved = savedBeforeRestore; _projectFolder = folderBeforeSubagentCheck;
        _restoring = true; _chatProvider.SelectedIndex = providerBeforeSubagentCheck; _restoring = false; UpdateProjectHeader();
        VerifySubagentArchiveCoalescing(Check);
        VerifyActivityRetentionUi(Check);
        _chatPage.Visibility = Visibility.Collapsed; await Task.Delay(50); _chatPage.Visibility = Visibility.Visible;
        Check(_composerSpinner.IsActive, "hidden conversation retains its own progress");
        turn.Finish("stopped"); _chatCancellation.Dispose(); _chatCancellation = null; SetChatBusy(false);
        Check(!_composerSpinner.IsActive && progress.Visibility == Visibility.Collapsed, "stopped progress cleared");
        resize(1440); await capture("stopped");
        foreach (var outcome in new[] { "completed", "failed" })
        {
            _chatCancellation = new CancellationTokenSource(); SetChatBusy(true);
            _chatCancellation.Dispose(); _chatCancellation = null; SetChatBusy(false);
            Check(!_composerSpinner.IsActive && progress.Visibility == Visibility.Collapsed, outcome + " cleanup");
        }
        if (displayOnly)
        {
            Check(Environment.GetEnvironmentVariable("SOVEREIGN_QA_PROJECT_LIVE") != "1", "subagent display-only fixture prohibits live-provider requests");
            ShowAgents(true); SetChangesOpen(true);
            foreach (var pair in _subagentViews) pair.Value.Root.Visibility = pair.Key is "qa-restored-complete" or "qa-restored-running"
                ? Visibility.Visible : Visibility.Collapsed;
            await capture("agents-restored-details");
            Check(_chatCancellation is null && !_chatBusy, "subagent display-only fixture finishes synthetic activity without starting a provider");
            checks.Add("subagent display-only fixture returns before clipboard, project-send and provider actions");
            return checks;
        }
        _chatMessages.Children.Clear();
        var quiet = new ChatActivityView("Grok 4.7 High Fast", UpdateChangesPanel, () => { }, SetComposerStatus, PauseFollow);
        TrackChatTurn(quiet); _chatMessages.Children.Add(quiet.Root);
        quiet.Append("I’m inspecting the saved-case code.");
        quiet.Observe(new("reasoning", "Synthetic reasoning kept behind a disclosure."));
        quiet.Observe(new("tool", Tool: new("quiet-read", "Read a long internal path", "read", "completed", "{}", [])));
        quiet.Append("Which case should I use? Please provide the case name.");
        quiet.Observe(new("tool", Tool: new("quiet-search", "Search a long query", "search", "completed", "{}", [])));
        quiet.Append("The test result is ready. Earlier progress and technical detail stay available on demand.\n\nUnicode: café — 中文 😀\n" + string.Join("\n", Enumerable.Repeat("Final answer paragraph with preserved formatting.", 80)));
        _chatCancellation = new CancellationTokenSource(); SetChatBusy(true);
        await Task.Delay(350);
        Check(quiet.ProgressUpdates.SequenceEqual(new[] { "I’m inspecting the saved-case code." }), "earlier commentary collapsed after real tool activity");
        Check(quiet.DisplayReply.Contains("Which case") && quiet.DisplayReply.Contains("test result is ready"), "question and current reply remain visible");
        Check(quiet.DetailsCollapsed, "reasoning activity plan and earlier progress start collapsed");
        Check(quiet.ActivityGroupCount == 2 && quiet.ReasoningHeader.StartsWith("Thought for "), "reads and searches are grouped; thought uses the observed duration");
        quiet.ShowReasoningPreviewForVerification();
        Check(quiet.ReasoningPreview == "Synthetic reasoning kept behind a disclosure." && !quiet.FullReasoningExpanded,
            "opening Thought reveals only the preview, not full reasoning");
        quiet.Observe(new("reasoning", " Still closed during streaming.")); quiet.FlushForVerification();
        Check(!quiet.FullReasoningExpanded, "streaming does not auto-expand full reasoning");
        quiet.ShowReasoningForVerification(false);
        Check(_liveStatus.TransformToVisual(composer).TransformPoint(new()).Y >= 0, "live status shares composer column");
        await capture("quiet-chat");
        quiet.ShowActivityForVerification();
        quiet.Finish("completed");
        Check(quiet.DetailsCollapsed && quiet.CompletedDetailsCollapsed && quiet.WorkHeader.Contains("2 steps"),
            "completion closes expanded details into a small work chip");
        await capture("quiet-chat-complete");
        async Task CheckClipboard(ChatActivityView view, string name)
        {
            var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(view.CopyResponseButton);
            var invoke = (Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke);
            invoke.Invoke();
            var copied = "";
            for (var i = 0; i < 50; i++)
            {
                await Task.Delay(20);
                var content = (string)view.CopyResponseButton.Content;
                if (content == "Copy failed — retry") break;
                if (content != "Copied") continue;
                Windows.ApplicationModel.DataTransfer.DataPackageView? data = null;
                try
                {
                    data = await ChatClipboard.ReadAsync(view.CopyResponseButton.DispatcherQueue);
                    if (!data.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)) continue;
                    copied = await data.GetTextAsync();
                }
                finally { data = null; }
                if (copied == view.DisplayReply) break;
            }
            Check(copied == view.DisplayReply, name + " copies complete visible response exactly");
            Check(!copied.Contains("inspecting the saved-case") && !copied.Contains("Synthetic reasoning"), name + " excludes collapsed progress and reasoning");
            Check((string)view.CopyResponseButton.Content == "Copied", name + " confirms clipboard success");
        }
        await CheckClipboard(quiet, "live response button");
        var reopened = new ChatActivityView("Grok", UpdateChangesPanel, () => { }, _ => { }, PauseFollow);
        reopened.RestoreProgress(quiet.ProgressUpdates); reopened.Append(quiet.DisplayReply); reopened.Finish("completed", true);
        Check(reopened.ProgressUpdates.SequenceEqual(quiet.ProgressUpdates) && reopened.DisplayReply == quiet.DisplayReply && reopened.DetailsCollapsed, "reopened display preserves collapsed progress and full visible answer");
        _chatMessages.Children.Add(reopened.Root);
        await Task.Delay(100);
        await CheckClipboard(reopened, "reopened response button");
        Check(reopened.WorkHeader == "Saved work · 0 steps", "legacy reopen does not invent elapsed time or tool counts");
        var failed = new ChatActivityView("Synthetic", _ => { }, () => { }, _ => { }, PauseFollow);
        failed.Observe(new("tool", Tool: new("failed-command", "Synthetic command", "execute", "failed", "{}", [])));
        failed.FlushForVerification();
        Check(failed.VisibleExecutionNotice.Contains("failed") && failed.DetailsCollapsed,
            "failed tools remain inline while their logs stay collapsed");
        failed.Observe(new("permission")); failed.FlushForVerification();
        Check(failed.VisibleExecutionNotice.Contains("Waiting for your approval") && failed.DetailsCollapsed,
            "approval pauses remain inline without expanding tool or reasoning details");
        failed.Dispose();
        reopened.Dispose();
        _chatCancellation.Dispose(); _chatCancellation = null; SetChatBusy(false);
        Check(_liveStatus.Visibility == Visibility.Collapsed, "live status clears when response finishes");
        await VerifyClipboardPasteAsync(Check);
        await VerifyCompactDisplayAsync(Check, capture);
        var live = Environment.GetEnvironmentVariable("SOVEREIGN_QA_PROJECT_LIVE") == "1";
        foreach (var providerIndex in live ? new[] { 2, 5 } : new[] { 2, 3, 5 })
        {
            _restoring = true; _chatProvider.SelectedIndex = providerIndex; _restoring = false;
            _saved = ChatArchive.New(providerIndex); _ollamaHistory.Clear(); _chatMessages.Children.Clear(); ResetChat();
            _projectFolder = Path.Combine(_profileDirectory, "project-agent-" + providerIndex);
            Directory.CreateDirectory(_projectFolder);
            _saved = _saved with { ProjectMode = CursorProjectMode.Agent, Permissions = CursorPermissionPolicy.AlwaysAllow };
            _restoring = true; RestoreProjectBehavior(); _restoring = false;
            var model = "synthetic-agent";
            if (live)
            {
                var credential = new Windows.Security.Credentials.PasswordVault().Retrieve("xAIDesk.Unused", PlanProvider); credential.RetrievePassword();
                _planChat = new CodingPlanChatClient(PlanProvider, ConnectionSetup.Endpoint(PlanProvider), credential.Password);
                model = PlanProvider == "glm" ? "glm-5.3" : "kimi-k2.6";
                File.WriteAllText(Path.Combine(_projectFolder, "ui-proof.md"), "UI-PROOF-before");
            }
            else _planChat = new CodingPlanChatClient(PlanProvider, CodingPlanChatClient.Endpoints(PlanProvider)[0], "synthetic-key", new ProjectUiHandler());
            _chatModel.Items.Add(model); _chatModel.SelectedIndex = 0; _chatModel.Text = model;
            _chatReady = true; _reconnectOnSend = false;
            _chatInput.Text = live ? "In this disposable QA project, read ui-proof.md, then edit its entire text to UI-PROOF using the returned hash. Run Write-Output 'TERMINAL-OK' in terminal. Report actual results. Do not touch other files." : "Synthetic project-agent UI check.";
            SetChatBusy(false);
            Check(_projectBehavior.Visibility == Visibility.Visible, PlanProvider + " project permissions visible");
            await SendChatAsync();
            Check(File.ReadAllText(Path.Combine(_projectFolder, "ui-proof.md")) == "UI-PROOF", PlanProvider + " send executes project tool");
            Check(_chatTurns.Last().Changes.Any(c => c.Path.EndsWith("ui-proof.md")), PlanProvider + " changes panel receives completed file edit");
            if (live) Check(_saved.Turns.Last().Activity.Any(e => e.Tool is { Kind: "execute", Status: "completed" } tool && tool.Output.Contains("TERMINAL-OK")), PlanProvider + " real terminal result persisted in chat");
            Check(!_chatBusy && !_composerSpinner.IsActive && _chatReady, PlanProvider + " send completes and composer recovers");
            SetChangesOpen(true); await capture("project-" + PlanProvider);
            _planChat.Dispose(); _planChat = null;
        }
        return checks;
    }

    private async Task VerifyCompactDisplayAsync(Action<bool, string> check, Func<string, Task> capture)
    {
        SetChangesOpen(false); _chatMessages.Children.Clear();
        _chatMessages.Children.Add(UserBubble("Synthetic UI example: inspect the project and report the result."));
        var view = new ChatActivityView("Synthetic preview", UpdateChangesPanel, () => { }, SetComposerStatus, PauseFollow);
        TrackChatTurn(view); _chatMessages.Children.Add(view.Root);
        _chatCancellation = new CancellationTokenSource(); SetChatBusy(true);
        view.Observe(new("reasoning", string.Concat(Enumerable.Repeat("Provider text preview: inspect the existing code and preserve the conversation. ", 24))));
        foreach (var (kind, count) in new[] { ("read", 9), ("execute", 3), ("edit", 2) })
            for (var index = 0; index < count; index++)
                view.Observe(new("tool", Tool: new("display-" + kind + index, "Synthetic " + kind + " step", kind, "completed", "{}", [])));
        view.Observe(new("tool", Tool: new("display-reading", "Synthetic current read", "read", "in_progress", "{}", [])));
        await Task.Delay(250);
        check(view.DetailsCollapsed && view.ActivityGroupCount == 3 && _liveStatus.Text.Contains("15 steps"),
            "one live composer status carries the step count; three activity groups stay closed");
        await capture("chat-ux-running");
        view.ShowReasoningPreviewForVerification();
        check(view.ReasoningPreview.Length <= 421 && !view.FullReasoningExpanded,
            "long reasoning has a bounded preview and still requires an explicit full-text click");
        await capture("chat-ux-thought-preview");
        view.ShowActivityForVerification();
        await capture("chat-ux-grouped-activity");
        view.Observe(new("tool", Tool: new("display-reading", "Synthetic current read", "read", "completed", "{}", [])));
        view.Append("Display-only example: the answer stays readable. Reasoning and tool details are available in the work chip.\n\nNo model or live service was called for this screenshot.");
        view.Finish("completed");
        _chatCancellation.Dispose(); _chatCancellation = null; SetChatBusy(false);
        check(view.DetailsCollapsed && view.CompletedDetailsCollapsed && view.WorkHeader.EndsWith("15 steps"),
            "completion closes the open preview and activity groups without losing the total");
        await capture("chat-ux-completed");
        view.Dispose();
    }

    private void VerifySubagentArchiveCoalescing(Action<bool, string> check)
    {
        var savedBefore = _saved;
        var activityBefore = _savedActivity.ToArray();
        var replyBefore = _savedReply.ToString();
        var indexBefore = _savedTurnIndex;
        var pendingBefore = _savePending;
        try
        {
            _saved = _saved with { Turns = [new SavedChatTurn("Archive fixture", "synthetic", "", "running", [])] };
            _savedTurnIndex = 0; _savedActivity.Clear(); _savedReply.Clear();
            var progressTool = new CursorToolActivity("qa-archive-helper:edit", "Edit archive fixture", "edit", "in_progress", "{}", []);
            var child = new CursorSubagent("qa-archive-helper", "Archive helper", "Exercise helper event persistence", "running", "", Tool: progressTool, Tools: [progressTool]);
            for (var chunk = 0; chunk < 4; chunk++)
                _partialActivity.Enqueue(new("subagent", Tool: progressTool with { Output = "Progress " + chunk }, Subagent: child));
            DrainSavedTurn();
            check(_saved.Turns[0].Activity.Count(a => a.Kind == "tool" && a.Tool?.Id == progressTool.Id) == 1,
                "helper progress chunks retain one chronological tool event");
            var fullText = new string('b', 100000);
            var completedTool = progressTool with { Status = "completed", Changes = [new("archive-fixture.md", "a", fullText)], Output = "Complete" };
            var preview = completedTool with { Changes = [], Output = "Complete; large diff omitted from preview" };
            child = child with { Tool = preview, Tools = [preview] };
            _partialActivity.Enqueue(new("subagent", Tool: completedTool, Subagent: child));
            _partialActivity.Enqueue(new("tool", Tool: new("qa-parent-edit", "Parent edit", "edit", "completed", "{}", [new("archive-fixture.md", fullText, "c")])));
            _partialActivity.Enqueue(new("subagent", Subagent: child with { State = "completed", Output = "Saved result" }));
            DrainSavedTurn();
            var persisted = _saved.Turns[0].Activity;
            check(persisted.Count(a => a.Kind == "tool" && a.Tool?.Id == completedTool.Id) == 1
                && persisted.First(a => a.Tool?.Id == completedTool.Id).Tool!.Changes.Single().After == fullText,
                "completed helper edit retains its full snapshot once");
            check(persisted.Where(a => a.Subagent is not null).All(a => a.Tool is null)
                && persisted.Single(a => a.Subagent is not null).Subagent!.Tool!.Changes.Length == 0,
                "persisted helper state stores bounded previews without duplicating full live tools");
            var restored = new CursorTurnActivity();
            foreach (var activity in persisted) restored.Apply(activity, TimeSpan.Zero);
            check(restored.Changes.Single().Before == "a" && restored.Changes.Single().After == "c",
                "restored helper edit chronology preserves the later parent edit");
            check(_subagentRows[child.SessionId].Output == "Saved result", "archive coalescing keeps the helper's latest result");
            for (var batch = 0; batch < 3; batch++)
            {
                for (var step = batch * 700; step < (batch + 1) * 700; step++)
                    _partialActivity.Enqueue(new("tool", Tool: new("qa-archive-retention-" + step, "Controlled saved step", "read", "completed", "{}", [])));
                DrainSavedTurn();
            }
            var compacted = CursorActivityRetention.Compact(_saved.Turns[0].Activity, "completed", SavedTurnActivityDirectory).ToArray();
            var savedSummary = compacted.Single(activity => activity.Kind == "summary").Summary;
            check(savedSummary is { Total: 2102, Completed: 2102, ChangedFiles: 1 } && compacted.Length < 2102,
                "repeated saved-turn compaction preserves lifetime totals beyond its bounded event window");
            var roundTrip = JsonSerializer.Deserialize<CursorActivityEvent[]>(JsonSerializer.Serialize(compacted))!;
            var reopened = new CursorTurnActivity(trustedRoot: Path.Combine(_profileDirectory, "chat-activity"));
            foreach (var activity in roundTrip) reopened.Apply(activity, TimeSpan.Zero);
            check(reopened.TotalTools == 2102 && reopened.Changes.Single().Before == "a" && reopened.Changes.Single().After == "c"
                && roundTrip.Single(activity => activity.Subagent is not null).Subagent!.Output == "Saved result",
                "JSON-restored compacted activity keeps exact totals, original-before/latest-after edits and the helper result");
        }
        finally
        {
            _saved = savedBefore; _savedActivity.Clear(); _savedActivity.AddRange(activityBefore);
            _savedReply.Clear(); _savedReply.Append(replyBefore); _savedTurnIndex = indexBefore; _savePending = pendingBefore;
        }
    }

    private static void VerifyActivityRetentionUi(Action<bool, string> check)
    {
        var view = new ChatActivityView("Controlled retention fixture", _ => { }, () => { }, _ => { }, () => { });
        try
        {
            for (var batch = 0; batch < 3; batch++)
            {
                for (var index = batch * 700; index < (batch + 1) * 700; index++)
                    view.Observe(new("tool", Tool: new("qa-ui-retention-" + index, "Controlled retained step", "read", "completed", "{}", [])));
                view.FlushForVerification();
                check(view.RenderedToolRows == view.RetainedToolSteps && view.RenderedToolSnapshots == view.RetainedToolSteps
                    && view.VisibleToolRows == view.RetainedToolSteps, "native activity rows and render snapshots prune with the retained window after batch " + (batch + 1));
            }
            view.Finish("completed");
            check(view.ObservedToolSteps == 2100 && view.RenderedToolRows < 2100 && view.ActivityHeader.StartsWith("Activity · 2100 steps")
                && view.ActivityHeader.Contains("earlier steps compacted"), "native activity header preserves lifetime steps beyond its bounded recent rows");
        }
        finally { view.Dispose(); }
    }

    private async Task VerifyClipboardPasteAsync(Action<bool, string> check)
    {
        var ran = false;
        var deferred = ChatClipboard.AfterInputAsync(_chatInput.DispatcherQueue, () => Task.FromResult(ran = true));
        check(!ran, "clipboard access leaves the input event stack before running");
        await deferred;
        check(ran, "deferred clipboard action executes on the UI queue");
        const string sharedText = "Shared clipboard projection · café 😀";
        await ChatClipboard.CopyAsync(_chatInput.DispatcherQueue, sharedText);
        var retainedReader = await ChatClipboard.ReadAsync(_chatInput.DispatcherQueue);
        for (var read = 0; read < 3; read++)
        {
            _chatInput.Text = "";
            _chatInput.Select(0, 0);
            await PasteIntoComposerAsync();
            check(_chatInput.Text == sharedText && await retainedReader.GetTextAsync() == sharedText,
                "paste and repeated reads preserve another clipboard projection: " + read);
        }
        retainedReader = null;
        _chatInput.Focus(FocusState.Programmatic);
        foreach (var text in new[] { "Simple output", "café — 中文 😀", "first line\r\nsecond line", new string('x', 100000) })
        {
            await ChatClipboard.CopyAsync(_chatInput.DispatcherQueue, text);
            _chatInput.Text = "before REPLACE after"; _chatInput.Select(7, 7);
            _chatInput.PasteFromClipboard();
            check(_chatInput.Text == "before REPLACE after", "native paste event defers clipboard read");
            var patience = text.Length > 1000 ? 250 : 100;
            for (var i = 0; i < patience && _chatInput.Text == "before REPLACE after"; i++) await Task.Delay(20);
            var normalized = text.Replace("\r\n", "\r").Replace("\n", "\r");
            check(_chatInput.Text == "before " + normalized + " after", "native paste replaces selection exactly: " + text.Length);
            check(_chatInput.SelectionStart == 7 + normalized.Length && _chatInput.SelectionLength == 0,
                "paste leaves caret after normalized insertion: " + text.Length);
        }
        await ChatClipboard.CopyAsync(_chatInput.DispatcherQueue, "stale paste");
        _chatInput.Text = "original"; _chatInput.Select(8, 0);
        _chatInput.PasteFromClipboard();
        _chatInput.Text = "newer user edit";
        await Task.Delay(100);
        check(_chatInput.Text == "newer user edit", "deferred paste preserves intervening user edits");
        var filePath = Path.Combine(_profileDirectory, "clipboard-mixed.txt");
        File.WriteAllText(filePath, "Clipboard attachment fixture.");
        var storageFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(filePath);
        await ChatClipboard.AfterInputAsync(_chatInput.DispatcherQueue, async () =>
        {
            for (var attempt = 0; ; attempt++)
            {
                var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
                data.SetText("Mixed-format fallback must not replace the attachment.");
                data.SetStorageItems(new[] { storageFile });
                try { Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data); return true; }
                catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == unchecked((int)0x800401D0) && attempt < 4)
                { await Task.Delay(80); }
            }
        });
        var beforeAttachments = _draftFiles.Count;
        _chatInput.PasteFromClipboard();
        for (var i = 0; i < 100 && _draftFiles.Count == beforeAttachments; i++) await Task.Delay(20);
        check(_draftFiles.Count == beforeAttachments + 1 && _draftFiles.Last().Name == "clipboard-mixed.txt",
            "file attachment retains priority over a mixed-format text fallback");
        check(_chatInput.Text == "newer user edit", "attachment paste preserves composer text");
        for (var i = 0; i < 50 && _attachmentBusy; i++) await Task.Delay(20);
        await ChatClipboard.AfterInputAsync(_chatInput.DispatcherQueue, () => Task.FromResult(true));
        _draftFiles.Clear();
        RenderDraftFiles();
        Save(); // A leftover draft makes the following project send return before it writes ui-proof.md.
        check(!_attachmentBusy && _draftFiles.Count == 0, "mixed-format paste leaves no draft attachment");
        await VerifyCopyAllAsync(check);
        await ChatClipboard.CopyAsync(_chatInput.DispatcherQueue, "clipboard unavailable fixture");
        var fault = ChatClipboard.AfterInputAsync<bool>(_chatInput.DispatcherQueue, () => throw new System.Runtime.InteropServices.COMException("QA clipboard busy"));
        try { await fault; check(false, "clipboard failure propagates"); }
        catch (System.Runtime.InteropServices.COMException) { check(true, "clipboard failure is catchable outside input stack"); }
        _chatInput.Text = "";
    }

    private async Task VerifyCopyAllAsync(Action<bool, string> check)
    {
        const string reply = "Copy-all fixture · café — 中文 😀\r\nsecond line";
        var previous = _saved.Turns;
        _saved = _saved with { Turns = [new SavedChatTurn("Which case?", "synthetic", reply, "completed", [])] };
        var clicked = ChatClipboard.AfterInputAsync(_chatInput.DispatcherQueue, async () =>
        {
            for (var i = 0; i < 40; i++)
            {
                var copyAll = FindDialogButton("Copy all");
                if (copyAll is not null)
                {
                    var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(copyAll);
                    var invoke = (Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke);
                    invoke.Invoke();
                    return true;
                }
                await Task.Delay(50);
            }
            var close = FindDialogButton("Close");
            if (close is not null)
            {
                var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(close);
                var invoke = (Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke);
                invoke.Invoke();
            }
            return false;
        });
        await ShowChatTextAsync();
        check(await clicked, "Copy all button is invoked on the conversation dialog");
        Windows.ApplicationModel.DataTransfer.DataPackageView? data = null;
        try
        {
            data = await ChatClipboard.ReadAsync(_chatInput.DispatcherQueue);
            var copied = data.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text) ? await data.GetTextAsync() : "";
            var normalized = copied.Replace("\r\n", "\n").Replace('\r', '\n');
            var sample = normalized.Length == 0 ? "<empty>" : normalized[..Math.Min(160, normalized.Length)].Replace("\n", "\\n");
            check(normalized.Contains("You\nWhich case?") && normalized.Contains("Assistant · completed\nCopy-all fixture · café — 中文 😀\nsecond line"),
                "Copy all places the saved conversation, including Unicode and a line break, on the clipboard; saw " + sample);
        }
        finally { data = null; _saved = _saved with { Turns = previous }; }
    }

    private Button? FindDialogButton(string label)
    {
        var xamlRoot = _owner.Content.XamlRoot;
        if (xamlRoot is null) return null;
        foreach (var popup in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot))
            if (popup.Child is DependencyObject child && FindLabeledButton(child, label) is { } popupButton) return popupButton;
        return _owner.Content is DependencyObject root ? FindLabeledButton(root, label) : null;
    }

    private static Button? FindLabeledButton(DependencyObject root, string label)
    {
        if (root is Button button && (button.Content as string == label || Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(button) == label)) return button;
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindLabeledButton(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i), label);
            if (found is not null) return found;
        }
        return null;
    }

    private FrameworkElement ComposerHost(FrameworkElement start)
    {
        // TextBox template parents are not its logical layout parents.
        return _chatPage.Children.OfType<FrameworkElement>().Single(e => !ReferenceEquals(e, _changesPanel)
            && Grid.GetRow(e) == 3);
    }

    private async Task VerifySubagentChatPanelsAsync(Action<bool, string> check, Func<string, Task> capture, Action<int> resize)
    {
        var remembered = _saved;
        try
        {
            _saved = _saved with { OpenSubagentChatPanels = true };
            ApplySubagentChatPanelMode();
            resize(1440);
            ObserveSubagent(new("subagent", Subagent: new CursorSubagent("qa-panel-auto", "Panel QA", "Open beside the chat", "running", "Panel is live",
                StartedAt: DateTimeOffset.UtcNow, ModelName: "Grok 4.7", ModelProvider: "xai")));
            check(SubagentPanelVisible("qa-panel-auto")
                && _subagentViews["qa-panel-auto"].AvatarAssetName == SubagentBlobAvatar.Pick("qa-panel-auto", "Panel QA").Key
                && SubagentPanelBlobKey("qa-panel-auto") == SubagentBlobAvatar.Pick("qa-panel-auto", "Panel QA").Key,
                "a new running helper opens its own chat panel with the same blob as its list card");
            ShowAgents(true); SetChangesOpen(true);
            var livePanel = _subagentRows["qa-panel-auto"] with { Output = "First partial result", Reasoning = "Checking the retained file." };
            ObserveSubagent(new("subagent", Subagent: livePanel));
            await capture("helper-row-live-with-cards");
            check(_subagentList.Visibility == Visibility.Visible && SubagentPanelTranscript("qa-panel-auto").Contains("First partial result"),
                "a helper streams in its own row while the expandable Subagents list remains visible");
            livePanel = livePanel with { State = "disconnected" };
            ObserveSubagent(new("subagent", Subagent: livePanel));
            livePanel = livePanel with { State = "completed", Output = "First partial result and final finding" };
            ObserveSubagent(new("subagent", Subagent: livePanel));
            check(SubagentPanelTranscript("qa-panel-auto").Contains("First partial result and final finding"),
                "late real completion replaces an unconfirmed disconnect without losing final output");
            SetChangesOpen(false);
            CloseSubagentPanel("qa-panel-auto");
            OpenSubagentPanel("qa-helper");
            var helper = _subagentRows["qa-helper"];
            var transcript = SubagentPanelTranscript("qa-helper");
            check(SubagentPanelVisible("qa-helper"), "helper chat panel opens beside the transcript");
            check(SubagentPanelBlobKey("qa-helper") == SubagentBlobAvatar.Pick("qa-helper", helper.Name).Key, "helper chat panel blob matches the helper id");
            check(SubagentPanelFollowUpReadOnly("qa-helper"), "helper follow-up is read-only");
            check(transcript.Contains("› ", StringComparison.Ordinal) && (transcript.Contains(helper.Task, StringComparison.Ordinal) || transcript.Contains("INDIGO-582", StringComparison.Ordinal) || transcript.Contains("Synthetic helper", StringComparison.Ordinal)),
                "helper chat panel shows the task prompt and helper output");
            OpenSubagentPanel("qa-interface");
            check(SubagentPanelVisibleCount == 1 && SubagentPanelTabCount == 2, "one helper conversation is open and the other stays on the tab strip");
            await capture("helper-row-two-wide");
            VerifyHelperRowBounds(check);
            OpenSubagentPanel("qa-backend");
            check(SubagentPanelTabCount == 3 && SubagentPanelVisibleCount == 1, "a third helper chat stays on the tab strip");
            await capture("helper-row-three-tabs");
            VerifyHelperRowBounds(check);
            OpenSubagentPanel("qa-helper");
            check(SubagentPanelVisible("qa-helper"), "selecting an older helper tab focuses its existing chat without reattaching a parent twice");
            resize(1000);
            await capture("helper-row-narrow-cards");
            VerifyHelperRowBounds(check);
            SetChangesOpen(false);
            await capture("helper-row-narrow-transcript");
            VerifyHelperRowBounds(check);
            CloseSubagentPanel("qa-backend");
            check(!SubagentPanelVisible("qa-backend") && !(_saved.OpenSubagentPanelIds ?? []).Contains("qa-backend"), "closing a helper chat removes only that panel");
            CloseAllSubagentPanels();
            var existing = _subagentRows.ContainsKey("qa-helper") ? "qa-helper" : _subagentRows.Keys.First();
            _saved = _saved with { OpenSubagentPanelIds = new[] { existing } };
            RestoreOpenSubagentPanels();
            check(SubagentPanelVisible(existing) && SubagentPanelReadOnly(existing), "restored helper chat opens read-only");
            _saved = _saved with { OpenSubagentChatPanels = false };
            ApplySubagentChatPanelMode();
            ResizeChangesPanel();
            check(_openSubagentPanels.Count == 0 && _helperDetailHost?.Visibility == Visibility.Collapsed
                && _subagentRows.ContainsKey(existing),
                "turning helper chats off keeps the cards and does not leave an empty helper conversation");
            var started = DateTimeOffset.UtcNow.AddMinutes(-2);
            var reopenHelpers = new[]
            {
                new CursorSubagent("qa-panel-reopen-a", "Stored builder", "Review the saved file", "completed", "Saved builder result", Reasoning: "Saved reasoning", StartedAt: started, CompletedAt: started.AddMinutes(1), ModelName: "Grok 4.7", ModelProvider: "xai"),
                new CursorSubagent("qa-panel-reopen-b", "Stored reviewer", "Check the saved file", "completed", "Saved reviewer result", StartedAt: started, CompletedAt: started.AddMinutes(1)),
                new CursorSubagent("qa-panel-reopen-c", "Interrupted helper", "Inspect the last change", "running", "Saved partial finding", StartedAt: started, UpdatedAt: started.AddMinutes(1))
            };
            _archive.Save(ChatArchive.New(DeskChatConnections.CursorStored) with
            {
                Title = "QA helper row reopen", OpenSubagentChatPanels = true,
                OpenSubagentPanelIds = reopenHelpers.Select(h => h.SessionId).ToArray(),
                Turns = [new SavedChatTurn("Keep these helper results", "synthetic", "Saved parent answer", "completed",
                    reopenHelpers.Select(h => new CursorActivityEvent("subagent", Subagent: h)).ToArray())]
            });
        }
        finally
        {
            CloseAllSubagentPanels();
            _saved = remembered with { OpenSubagentChatPanels = false };
            foreach (var view in _subagentViews.Values) view.SetOpensPanel(false);
            resize(1440);
        }
    }

    private void VerifyHelperRowBounds(Action<bool, string> check)
    {
        var composer = ComposerHost(_chatInput);
        var host = _helperDetailHost ?? throw new IOException("Helper conversation host was not mounted.");
        check(host.Visibility == Visibility.Visible && composer.Visibility == Visibility.Visible && composer.ActualHeight > 0,
            "selected helper stays in the side panel and the main composer stays usable at " + Math.Round(_chatPage.ActualWidth) + " DIP");
        check(ReferenceEquals(_chatScroll?.Content, _chatMessages) && _chatScroll.Visibility == Visibility.Visible,
            "helper conversation leaves the main transcript in its original scroll host");
        check(_chatPage.ColumnDefinitions[0].Width.GridUnitType == GridUnitType.Star,
            "main chat keeps the flexible column");
        foreach (var id in VisibleSubagentIds())
        {
            var root = _openSubagentPanels[id].Root;
            check(root.ActualWidth > 0 && root.ActualHeight > 0 && IsInHelperHost(root),
                "visible helper has one conversation in the side panel: " + id);
        }
    }

    private bool IsInHelperHost(DependencyObject element)
    {
        if (_helperDetailHost is null) return false;
        for (DependencyObject? current = element; current is not null; current = current is FrameworkElement visual ? visual.Parent : Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, _helperDetailHost)) return true;
        return false;
    }

    private sealed class ProjectUiHandler : HttpMessageHandler
    {
        private int _round;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var json = ++_round == 1
                ? """{"choices":[{"finish_reason":"tool_calls","message":{"role":"assistant","tool_calls":[{"id":"ui-create","type":"function","function":{"name":"create_file","arguments":"{\"path\":\"ui-proof.md\",\"content\":\"UI-PROOF\"}"}}]}}]}"""
                : """{"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"Created ui-proof.md using the project tool. Synthetic provider response."}}]}""";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
