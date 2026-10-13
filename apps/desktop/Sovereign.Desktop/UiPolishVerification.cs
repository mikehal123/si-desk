using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Sovereign.Desktop.Client;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Sovereign.Desktop;

public sealed partial class MainWindow
{
    private async Task RunUiPolishCaptureAsync(string output)
    {
        var checks = new List<string>();
        try
        {
            ValidateCodexQaProfile(_vm.ProfileDirectory);
            Title = "SI Desk · UI sample";
            ClassicChatPreference.Save(_vm.ProfileDirectory, false);
            var time = DateTimeOffset.UtcNow.AddMinutes(-2);
            var answer = "## Project review\n\nThe app keeps the conversation separate from provider connections.\n\n- Read the source files.\n- Preserve the existing agent behavior.\n\n```python\nprint(\"Ready for review\")\n```\n\nThe changes are ready to inspect.";
            var activity = new CursorActivityEvent[] {
                new("reasoning", "I checked the existing implementation and kept the current connection and tool behavior."),
                new("tool", Tool: new CursorToolActivity("ui-read", "Read 3 source files", "read", "completed", "{}", [])),
                new("subagent", Subagent: new CursorSubagent("ui-builder", "Layout builder", "Review the layout", "completed", "Layout reviewed.", StartedAt: time, CompletedAt: time.AddMinutes(1), ModelName: "Grok 4.7 High Fast", ModelProvider: "xai")),
                new("subagent", Subagent: new CursorSubagent("ui-reviewer", "Code reviewer", "Check the patch", "completed", "The patch preserves the existing behavior.", StartedAt: time, CompletedAt: time.AddMinutes(1), ModelName: "Grok 4.7 High Fast", ModelProvider: "xai")),
                new("subagent", Subagent: new CursorSubagent("ui-stopped", "Stopped helper", "Inspect the last task", "cancelled", "Partial findings kept.", StartedAt: time, CompletedAt: time.AddMinutes(1), ModelName: "Grok 4.7 High Fast", ModelProvider: "xai")) };
            AddChat(ChatArchive.New(DeskChatConnections.CursorStored) with { Title = "UI review sample", Draft = "Ask a follow-up…", Model = "grok-4.7",
                OpenSubagentChatPanels = true, Turns = [new("Review the project and show the result.", "Grok 4.7 High Fast", answer, "completed", activity)] }, true);
            foreach (var width in new[] { 1440, 1024 })
            {
                AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 900));
                await Task.Delay(350);
                _currentChat.UiPolishShowAgents(false);
                if (Environment.GetEnvironmentVariable("SOVEREIGN_QA_READING_BUTTONS") == "1")
                {
                    foreach (var inspector in new[] { false, true })
                    {
                        _currentChat.UiPolishShowAgents(inspector);
                        await Task.Delay(100);
                        _currentChat.UiPolishReadingButtons(checks, width, inspector);
                        await CapturePng(Path.Combine(output, width + (inspector ? "-inspector.png" : "-chat.png")));
                    }
                    continue;
                }
                if (Environment.GetEnvironmentVariable("SOVEREIGN_QA_PENDING_MESSAGES") == "1")
                {
                    await _currentChat.UiPolishPendingMessagesAsync(checks, width,
                        name => CapturePng(Path.Combine(output, name)));
                    continue;
                }
                if (Environment.GetEnvironmentVariable("SOVEREIGN_QA_INLINE_PROGRESS") == "1")
                {
                    await _currentChat.UiPolishInlineProgressAsync(checks, width,
                        name => CapturePng(Path.Combine(output, name)));
                    continue;
                }
                await Task.Delay(150);
                await CapturePng(Path.Combine(output, width + "-chat.png"));
                _currentChat.UiPolishShowAgents(true);
                await Task.Delay(200);
                await CapturePng(Path.Combine(output, width + "-subagents.png"));
                if (Environment.GetEnvironmentVariable("SOVEREIGN_QA_GRAB_SEND") == "1")
                {
                    foreach (var classic in new[] { false, true })
                    {
                        _currentChat.UiPolishComposerState(classic, working: false);
                        await Task.Delay(100);
                        _currentChat.UiPolishVerifyGrabAndComposer(checks, width, working: false);
                        await CapturePng(Path.Combine(output, width + (classic ? "-classic-send.png" : "-send.png")));
                        _currentChat.ClearComposerForButtonPreview();
                        _currentChat.UiPolishComposerState(classic, working: true);
                        await Task.Delay(100);
                        _currentChat.UiPolishVerifyGrabAndComposer(checks, width, working: true);
                        await CapturePng(Path.Combine(output, width + (classic ? "-classic-stop.png" : "-stop.png")));
                    }
                    _currentChat.UiPolishComposerState(classic: false, working: false);
                    continue;
                }
                var popup = await _currentChat.UiPolishConnectionAsync();
                await CaptureElementPngAsync(popup, Path.Combine(output, width + "-connection.png"));
                _currentChat.UiPolishHideConnection();
                OpenSettings(); await Task.Delay(250);
                await CapturePng(Path.Combine(output, width + "-settings.png"));
                SelectChat(_currentChat);
                checks.Add(width + ": Chat, Subagents, Connection and Settings captured from the actual controls");
            }
            if (_currentChat._cursorChat is not null || _currentChat._codexChat is not null || _currentChat._grokBuild is not null)
                throw new IOException("UI capture unexpectedly connected a provider.");
            checks.Add("No provider connections or live calls");
            File.WriteAllText(Path.Combine(output, "receipt.json"), JsonSerializer.Serialize(new { passed = true, checks, provider_calls = 0,
                version_unchanged = true, profile = _vm.ProfileDirectory, stage = Environment.GetEnvironmentVariable("SOVEREIGN_QA_UI_POLISH") }));
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(output, "receipt.json"), JsonSerializer.Serialize(new { passed = false, error = error.ToString() })); }
        finally { Close(); }
    }

    private static async Task CaptureElementPngAsync(UIElement element, string path)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        var pixels = await bitmap.GetPixelsAsync();
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)!);
        var file = await folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, ReadBuffer(pixels));
        await encoder.FlushAsync();
    }
}

internal sealed partial class ChatConversation
{
    internal void ClearComposerForButtonPreview() => _chatInput.Text = "";

    internal void UiPolishReadingButtons(List<string> checks, int width, bool inspector)
    {
        PauseFollow(); RefreshClearReasoning(); _chatPage.UpdateLayout();
        if (_readingControls is null || _composerArea is null || _chatClearReasoning is null || _chatLatest is null)
            throw new IOException("Reading controls are missing.");
        var center = _readingControls.TransformToVisual(_composerArea).TransformPoint(new Windows.Foundation.Point(_readingControls.ActualWidth / 2, 0)).X;
        if (Math.Abs(center - _composerArea.ActualWidth / 2) > 1 || _chatClearReasoning.ActualHeight != 24 || _chatLatest.ActualHeight != 24)
            throw new IOException("Reading buttons are not centered and compact.");
        checks.Add($"{width}, inspector={inspector}: both reading buttons visible, centered over composer, 24 DIP high");
    }

    internal async Task UiPolishPendingMessagesAsync(List<string> checks, int width, Func<string, Task> capture)
    {
        _projectFolder = _profileDirectory;
        foreach (var classic in new[] { true, false })
        {
            _chatInput.Text = "";
            UiPolishComposerState(classic, working: true);
            _chatMessages.Children.Clear(); _pendingInstructionViews.Clear(); _deliveredInstructionIds.Clear();
            _steeringQueue = new(); _instructionStatus.Clear(); _instructionFailures.Clear(); _removedInstructionLabels.Clear();
            var view = new ChatActivityView("Grok 4.7 High Fast", _ => { }, () => { }, SetComposerStatus, () => { }, classicChat: classic);
            try
            {
                _activeChatTurn = view;
                _chatMessages.Children.Add(UserMessage("Find the engine files."));
                _chatMessages.Children.Add(view.Root);
                view.Append("I'll check the engine files and keep the existing pipeline."); view.FlushForVerification();
                view.Observe(new("tool", Tool: new CursorToolActivity("pending-read", "Read engine files", "read", "in_progress", "{}", []))); view.FlushForVerification();
                var first = _steeringQueue.Accept("Should we try loading it after the restart?", "grok-4.7", [], true);
                var second = _steeringQueue.Accept("Check the memory usage first.", "grok-4.7", [], true);
                ShowPendingInstruction(first); ShowPendingInstruction(second);
                var batch = _steeringQueue.BeginBatch(); RefreshPendingInstructions();
                foreach (var message in batch) RevealDeliveredInstruction(message);
                Require(_pendingInstructionViews.Count == 2 && _steeringQueue.Count == 2, "messages retained without duplication");
                foreach (var item in _pendingInstructionViews.Values)
                {
                    Require(item.Root.Parent == _chatMessages && !_composerArea!.Children.Contains(item.Root), "pending messages stay inside the transcript");
                    Require(item.Status.Text == "Waiting for acceptance" && item.Actions.Visibility == Visibility.Collapsed,
                        "working messages have one compact truthful label and no action clutter");
                    Require(item.Root.Children.OfType<Border>().Count() == 1, "each captured prompt renders once");
                }
                var name = width + (classic ? "-classic" : "-terminal");
                await Task.Delay(150);
                Require(_tuiStatusHost?.Parent == view.Root && !_composerArea!.Children.Contains(_tuiStatusHost), "live status belongs to the current response, not the composer");
                await capture(name + "-pending.png");
                var center = _chatStop!.TransformToVisual(_composerBar).TransformPoint(new Windows.Foundation.Point(0, _chatStop.ActualHeight / 2)).Y;
                foreach (var control in new FrameworkElement[] { _attachFiles!, _projectBehavior, _chatModel })
                {
                    var y = control.TransformToVisual(_composerBar).TransformPoint(new Windows.Foundation.Point(0, control.ActualHeight / 2)).Y;
                    Require(Math.Abs(y - center) <= 1, $"composer control {control.GetType().Name} center {y} must match Stop center {center}");
                }
                _steeringQueue.CompleteBatch(batch, delivered: true); RefreshPendingInstructions();
                Require(_steeringQueue.Count == 0 && _pendingInstructionViews.Values.All(item =>
                    item.Root.Visibility == Visibility.Visible && item.Status.Text == "Delivered"), "delivery retains the original message without a duplicate");
                await capture(name + "-delivered.png");
                ObserveSubagent(new CursorActivityEvent("subagent", Subagent: new CursorSubagent("ui-builder", "Audit desktop shell UI", "Read-only audit of the shell.", "running",
                    "I'll review the layout and preserve the existing behavior.", StartedAt: DateTimeOffset.UtcNow.AddSeconds(-75), ModelName: "Grok 4.7 High Fast", ModelProvider: "xai")));
                ShowAgents(true); SetChangesOpen(true); OpenSubagentPanel("ui-builder");
                await Task.Delay(150);
                Require(UiPolishNodes(_changesPanel).OfType<TextBlock>().Count(block => block.Text.StartsWith("Running · ")) >= 2,
                    "list card and helper chat combine the running state and elapsed time");
                Require(!UiPolishNodes(_changesPanel).OfType<Button>().Any(button =>
                    Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(button) == "Stop the main agent and its helpers"),
                    "a helper card does not stop the main agent");
                Require(UiPolishNodes(_changesPanel).OfType<Button>().Any(button =>
                    (Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(button) ?? "").StartsWith("Hide agent card", StringComparison.Ordinal)),
                    "a plain helper card can hide itself");
                await capture(name + "-helpers.png");
                ShowAgents(false); SetChangesOpen(false);
                UiPolishComposerState(classic, working: false);
                _chatMessages.Children.Clear(); _pendingInstructionViews.Clear(); _deliveredInstructionIds.Clear();
                _chatMessages.Children.Add(UserMessage(first.Text));
                _steeringQueue = new(new[] { first with { MayHaveBeenSent = true } });
                ShowPendingInstruction(_steeringQueue.Snapshot.Single(), representedInHistory: true);
                Require(_chatMessages.Children.OfType<Border>().Count() == 1 &&
                    _pendingInstructionViews[first.Id].Root.Children.OfType<Border>().Count() == 0, "historical pending prompt is not rendered twice");
                Require(!_steeringQueue.HasAutomaticPending && _pendingInstructionViews[first.Id].Status.Text.StartsWith("Interrupted"),
                    "uncertain restored delivery stays explicit and is never resent automatically");
                var recovery = _pendingInstructionViews[first.Id].Actions.Children.OfType<Button>().ToArray();
                Require(recovery.Select(button => button.Content as string).SequenceEqual(new[] { "Resume", "Edit", "Remove" }) &&
                    recovery.All(button => button.Height == 24 && button.FontSize == 12 && button.BorderThickness.Left == 0),
                    "interrupted-message controls stay compact and unboxed in both chat styles");
                await capture(name + "-interrupted.png");
                checks.Add(name + ": no pending footer; one prompt per id; live status inside response; composer alignment; compact waiting/delivered states; restored uncertainty preserved");
            }
            finally { view.Dispose(); _activeChatTurn = null; UiPolishComposerState(classic, working: false); }
        }
        static void Require(bool condition, string message) { if (!condition) throw new IOException(message); }
    }

    internal async Task UiPolishInlineProgressAsync(List<string> checks, int width, Func<string, Task> capture)
    {
        const string first = "I'll look through this workspace, starting with the project instructions and the GLM-related folders.";
        const string second = "I found the engine files. I'm checking how they connect before proposing a change.";
        const string answer = "The engine is present. The project entry point and its supporting files are ready to inspect.";
        foreach (var classic in new[] { true, false })
        {
            _chatInput.Text = "";
            UiPolishComposerState(classic, working: true);
            _chatMessages.Children.Clear();
            _chatMessages.Children.Add(UserMessage("Look for the GLM engine build in this folder."));
            var view = new ChatActivityView("Grok 4.7 High Fast", _ => { }, () => { }, SetComposerStatus, () => { }, classicChat: classic);
            _activeChatTurn = view;
            _chatMessages.Children.Add(view.Root);
            var name = width + (classic ? "-classic" : "-terminal");
            try
            {
                view.Append(first); view.FlushForVerification();
                view.Observe(new("tool", Tool: new CursorToolActivity("inline-read", "Read project instructions", "read", "in_progress", "{}", [])));
                view.FlushForVerification();
                Require(view.ProgressInlineVisible && view.ProgressUpdates.SequenceEqual(new[] { first }), "first update remains inline after a tool starts");
                view.Observe(new("reasoning", "Check the entry point and preserve the existing pipeline.")); view.FlushForVerification();
                await Task.Delay(160);
                var firstOpacity = _liveIndicator.Opacity;
                await Task.Delay(280);
                Require(_livePulseRunning && _liveStatusRow.Visibility == Visibility.Visible &&
                    Math.Abs(firstOpacity - _liveIndicator.Opacity) > .01, "live indicator animates while working");
                Require(!UiPolishNodes(view.Root).OfType<ChatDisclosure>().Any(item => item.Header.ToString()?.StartsWith("Progress") == true), "no Progress disclosure");
                Require(_chatSend?.Visibility == Visibility.Collapsed && _chatStop?.Visibility == Visibility.Visible, "working with an empty draft shows only Stop");
                await capture(name + "-thinking.png");
                _chatInput.Text = "Check the entry point too.";
                await Task.Delay(100);
                Require(_chatSend?.Visibility == Visibility.Visible && _chatSend.IsEnabled && _chatStop?.Visibility == Visibility.Collapsed,
                    $"typing a follow-up switches Stop to Send: Send={_chatSend?.Visibility}, enabled={_chatSend?.IsEnabled}, Stop={_chatStop?.Visibility}, available={CursorComposerAvailable}");
                Require(_chatBusy && _chatCancellation is { IsCancellationRequested: false }, "typing does not cancel the current work");
                await capture(name + "-followup.png");
                _chatInput.Text = "   ";
                await Task.Delay(100);
                Require(_chatSend?.Visibility == Visibility.Collapsed && _chatStop?.Visibility == Visibility.Visible, "clearing the draft returns to Stop");
                _chatInput.Text = "";
                view.Observe(new("tool", Tool: new CursorToolActivity("inline-read", "Read project instructions", "read", "completed", "{}", [])));
                view.FlushForVerification();
                view.Append(second); view.FlushForVerification();
                await Task.Delay(100);
                await capture(name + "-replying.png");
                view.Observe(new("tool", Tool: new CursorToolActivity("inline-search", "Find engine files", "search", "completed", "{}", [])));
                view.FlushForVerification();
                view.Append(answer); view.FlushForVerification(); view.Finish("completed");
                _activeChatTurn = null; UiPolishComposerState(classic, working: false);
                Require(view.ProgressInlineVisible && view.ProgressUpdates.SequenceEqual(new[] { first, second }), "both updates remain inline after completion");
                Require(view.DisplayReply == answer, "final answer copying remains separate");
                Require(!_livePulseRunning && _liveStatusRow.Visibility == Visibility.Collapsed, "live indicator disappears on completion");
                Require(view.CompletedDetailsCollapsed && !view.FullReasoningExpanded && view.ActivityGroupCount == 2, "Reasoning and Activity retain their existing disclosure state");
                await capture(name + "-completed.png");
                var stored = ChatArchive.New(DeskChatConnections.CursorStored) with
                {
                    Title = "Inline progress QA", Turns = [new("Look for the engine.", "Grok 4.7 High Fast", first + "\n" + second + "\n" + answer,
                        "completed", [], ProgressUpdates: view.ProgressUpdates, DisplayReply: view.DisplayReply)]
                };
                var archive = new ChatArchive(_profileDirectory);
                archive.Save(stored);
                var saved = archive.Load().Single(chat => chat.Id == stored.Id).Turns.Single();
                var reopened = new ChatActivityView("Grok 4.7 High Fast", _ => { }, () => { }, _ => { }, () => { }, classicChat: classic);
                try
                {
                    reopened.RestoreProgress(saved.ProgressUpdates ?? []); reopened.Append(saved.DisplayReply ?? saved.Reply); reopened.Finish("completed", true);
                    _chatMessages.Children.Remove(view.Root); _chatMessages.Children.Add(reopened.Root);
                    Require(reopened.ProgressInlineVisible && reopened.ProgressUpdates.SequenceEqual(new[] { first, second }) && reopened.DisplayReply == answer,
                        "saved progress and final answer reopen without a Progress tab");
                    await capture(name + "-reopened.png");
                }
                finally { reopened.Dispose(); }
                checks.Add(name + ": inline streaming updates, animated working indicator, idle indicator hidden, existing disclosures, archive reopen passed");
            }
            finally { view.Dispose(); _activeChatTurn = null; UiPolishComposerState(classic, working: false); }
        }
        static void Require(bool condition, string message) { if (!condition) throw new IOException(message); }
    }

    internal void UiPolishComposerState(bool classic, bool working)
    {
        if (_cursorChat is not null || _codexChat is not null || _grokBuild is not null)
            throw new IOException("Composer preview requires a disconnected QA chat.");
        _classicChat = classic;
        ApplyChatChrome();
        _chatCancellation?.Dispose();
        _chatCancellation = working ? new CancellationTokenSource() : null;
        _chatReady = true;
        _cursorSendLoop = working;
        SetChatBusy(working);
    }

    internal void UiPolishVerifyGrabAndComposer(List<string> checks, int width, bool working)
    {
        _chatPage.UpdateLayout();
        foreach (var (bar, horizontal) in new[] { (_rightSplitter, false), (_helperHeightSplitter, true) })
        {
            var size = horizontal ? bar.ActualHeight : bar.ActualWidth;
            if (size < 16) throw new IOException("Divider grab target is too small: " + size);
            foreach (var offset in new[] { -6.0, 6.0 })
            {
                var local = new Windows.Foundation.Point(bar.ActualWidth / 2 + (horizontal ? 0 : offset),
                    bar.ActualHeight / 2 + (horizontal ? offset : 0));
                var point = bar.TransformToVisual(XamlRootContent()).TransformPoint(local);
                var hits = Microsoft.UI.Xaml.Media.VisualTreeHelper.FindElementsInHostCoordinates(point, XamlRootContent());
                if (!hits.Contains(bar)) throw new IOException("Divider misses a grab 6px from its visible line.");
            }
        }
        if (_chatSend?.Content is not Microsoft.UI.Xaml.Shapes.Path { Data: Microsoft.UI.Xaml.Media.PathGeometry arrow } || arrow.Figures.Count != 2 ||
            _chatSend.Width != 36 || _chatSend.Height != 34 || _chatSend.CornerRadius.TopLeft != 17)
            throw new IOException("Send arrow or circle is missing.");
        if (_chatStop?.Content is not SymbolIcon { Symbol: Symbol.Stop } ||
            _chatStop.Width != 36 || _chatStop.Height != 34 || _chatStop.CornerRadius.TopLeft != 17 ||
            _chatStop.Visibility != (working && string.IsNullOrWhiteSpace(_chatInput.Text) ? Visibility.Visible : Visibility.Collapsed))
            throw new IOException("Stop square, circle or visibility is incorrect.");
        if (working && _tuiStop?.Visibility != Visibility.Collapsed)
            throw new IOException("Duplicate text Stop control is visible.");
        checks.Add($"{width}: {(_classicChat ? "classic" : "terminal")} {(working ? "working" : "idle")}; both dividers hit at ±6px; Sovereign Send/Stop icons");
        UIElement XamlRootContent() => _chatPage.XamlRoot.Content;
    }

    internal void UiPolishShowAgents(bool show)
    {
        ShowAgents(show); SetChangesOpen(show);
        if (show) OpenSubagentPanel("ui-builder", readOnly: true);
    }

    internal async Task<UIElement> UiPolishConnectionAsync()
    {
        if (_connectionFlyout?.Content is not UIElement content) throw new IOException("Connection controls are missing.");
        var button = UiPolishNodes(_chatPage).OfType<Button>()
            .First(control => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(control) == "Connection settings");
        _connectionFlyout.ShowAt(button);
        await Task.Delay(200);
        return content;
    }
    internal void UiPolishHideConnection() => _connectionFlyout?.Hide();
    private static IEnumerable<DependencyObject> UiPolishNodes(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in UiPolishNodes(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
