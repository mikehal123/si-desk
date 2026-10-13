using System.Text.Json;
using Sovereign.Desktop.Client;

namespace Sovereign.Desktop;

public sealed partial class MainWindow
{
    public async Task RunNativeSubagentsVerificationAsync(string output)
    {
        ValidateCodexQaProfile(_vm.ProfileDirectory);
        Directory.CreateDirectory(output);
        try
        {
            Title = _vm.AppTitle + " · native subagents verification";
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1550, 950));
            var reopenId = Environment.GetEnvironmentVariable("SOVEREIGN_QA_SUBAGENTS_REOPEN_CHAT_ID");
            if (Environment.GetEnvironmentVariable("SOVEREIGN_QA_SUBAGENTS_RECAPTURE") == "1" && string.IsNullOrWhiteSpace(reopenId))
                throw new IOException("Provider-free recapture requires the actual saved chat identity and successful original reference.");
            if (!string.IsNullOrWhiteSpace(reopenId))
            {
                if (!Guid.TryParseExact(reopenId, "N", out _)) throw new IOException("Invalid saved chat identity.");
                var restored = _conversations.SingleOrDefault(chat => chat.Id == reopenId)
                    ?? throw new IOException("Requested QA conversation was not loaded from this profile.");
                SelectChat(restored, false);
            }
            OpenChat();
            var result = await _currentChat.VerifyNativeSubagentsAsync(async name => {
                await Task.Delay(200);
                await CapturePng(Path.Combine(output, name + ".png"));
            }, output);
            File.WriteAllText(Path.Combine(output, "receipt.json"), JsonSerializer.Serialize(new { passed = true, result }));
        }
        catch (Exception ex)
        {
            try
            {
                _currentChat?.SaveNativeSubagentObservations(output, "failure", ex.ToString());
                await CapturePng(Path.Combine(output, "subagents-actual-failure.png"));
            }
            catch (Exception observationError) { File.WriteAllText(Path.Combine(output, "observation-error.txt"), observationError.ToString()); }
            File.WriteAllText(Path.Combine(output, "receipt.json"), JsonSerializer.Serialize(new { passed = false, error = ex.ToString() }));
        }
        finally { Close(); }
    }
}

internal sealed partial class ChatConversation
{
    internal async Task<object> VerifyNativeSubagentsAsync(Func<string, Task> capture, string output)
    {
        var teamMode = Environment.GetEnvironmentVariable("SOVEREIGN_QA_TEAM_MODE");
        if (teamMode == "1") return await VerifyTeamWorkflowAsync(capture, output);
        if (teamMode == "reopen") return await VerifyTeamReopenAsync(capture, output);
        var reopenId = Environment.GetEnvironmentVariable("SOVEREIGN_QA_SUBAGENTS_REOPEN_CHAT_ID");
        if (!string.IsNullOrWhiteSpace(reopenId)) return await VerifyTinySubagentReopenAsync(reopenId, capture, output);
        if (Environment.GetEnvironmentVariable("SOVEREIGN_QA_SUBAGENTS_TINY") == "1") return await VerifyTinyNativeSubagentAsync(capture, output);
        var codex = Environment.GetEnvironmentVariable("SOVEREIGN_QA_SUBAGENTS_PROVIDER") == "codex";
        var plan = Environment.GetEnvironmentVariable("SOVEREIGN_QA_SUBAGENTS_MODE") == "plan";
        var fixture = Path.Combine(_profileDirectory, "subagent-project");
        Directory.CreateDirectory(fixture);
        var files = new Dictionary<string, string> {
            ["backend.md"] = "Backend verification marker: BACKEND-582.\nRead this file and report the marker.\n",
            ["interface.md"] = "Interface verification marker: INTERFACE-731.\nRead this file and report the marker.\n",
            ["qa.md"] = "QA verification marker: QA-946.\nRead this file and report the marker.\n"
        };
        foreach (var (name, text) in files) File.WriteAllText(Path.Combine(fixture, name), text);
        _restoring = true;
        _saved = ChatArchive.New(DeskChatConnections.CursorStored, fixture) with {
            ProjectMode = plan ? CursorProjectMode.Plan : CursorProjectMode.Agent,
            Permissions = plan ? CursorPermissionPolicy.AlwaysAsk : CursorPermissionPolicy.AlwaysAllow
        };
        _projectFolder = fixture;
        _chatProvider.SelectedIndex = DeskChatConnections.Cursor;
        RestoreProjectBehavior();
        ResetChat();
        _restoring = false;
        string modelId;
        if (codex)
        {
            // Use an existing explicitly selected subscription profile in isolated QA,
            // without copying its authentication or operating on its project files.
            var subscriptionProfile = Environment.GetEnvironmentVariable("SOVEREIGN_QA_SUBSCRIPTION_PROFILE") ?? _profileDirectory;
            var epoch = ++_projectSessionEpoch;
            _codexChat = new CodexChatClient(fixture, new CursorProjectOptions {
                Mode = _saved.ProjectMode, Permissions = _saved.Permissions, EnableSubagents = true,
                RequestPermission = RequestProjectPermissionAsync
            }, activity => { if (epoch == _projectSessionEpoch) { _activeChatTurn?.Observe(activity); _partialActivity.Enqueue(activity); } }, profile: subscriptionProfile);
            using var connecting = new CancellationTokenSource(TimeSpan.FromSeconds(50));
            if (!await _codexChat.IsSubscriptionAsync(connecting.Token)) throw new IOException("Sign in to the ChatGPT connection before native subscription verification.");
            var models = await _codexChat.ModelsAsync(connecting.Token);
            if (!models.Contains("gpt-6.1-sol", StringComparer.Ordinal)) throw new IOException("GPT-6.1 Sol is not available in the connected subscription catalog.");
            modelId = "gpt-6.1-sol";
            BindChatModels(models.Select(m => (m, (string?)m)).ToArray(), false);
            _chatReady = true;
            UpdateProjectHeader();
            SetChatBusy(false);
        }
        else
        {
            await ConnectChatAsync();
            if (!_chatReady || _cursorChat is null) throw new IOException("Cursor connection failed: " + _chatStatus.Text);
            modelId = (await _cursorChat.ModelsAsync(default)).FirstOrDefault(m => m.Id.StartsWith("grok-4.7[", StringComparison.Ordinal)
                && m.Id.Contains("fast=true", StringComparison.Ordinal))?.Id ?? throw new IOException("Grok 4.7 unavailable.");
        }
        if (!SelectChatModel(modelId)) throw new IOException("Cannot select the requested model.");
        var helperType = codex ? "" : "generalPurpose ";
        _chatInput.Text = "Verify native parallel delegation in this disposable project. Launch exactly three direct " + helperType + "subagents concurrently before waiting for results. "
            + "Name the tasks Backend, Interface and QA. Backend reads only backend.md and reports its marker. Interface reads only interface.md and reports its marker. QA reads only qa.md and reports its marker. "
            + (codex ? "Each helper also produces a detailed twelve-step read-only validation checklist for its component, explaining what each check would verify and what evidence it needs. " : "")
            + "You must not read these files yourself. "
            + (codex ? "Helpers may use read-only file commands to inspect their assigned file. Do not edit files, run other commands, use external accounts or launch nested helpers. "
                : "Do not edit files, run commands, use external accounts or launch nested helpers. ")
            + "If a helper fails, report the failure without retrying. "
            + "After all three finish, summarize their markers in your final answer and stop.";
        var sending = SendChatAsync();
        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
        var maximumActive = 0;
        var captured = false;
        var capturedWork = false;
        while (!sending.IsCompleted && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(300);
            DrainSavedTurn();
            var active = _subagentRows.Values.Count(a => a.State == "running");
            maximumActive = Math.Max(maximumActive, active);
            if (active >= 3 && !captured)
            {
                if (!_changesOpen || !_showAgents) throw new IOException("Native helper spawn did not open the Subagents tab.");
                await capture("subagents-running");
                captured = true;
            }
            if (active >= 3 && !capturedWork && _subagentRows.Values.Any(c => c.CurrentWork != "Working"))
            {
                await capture("subagents-working");
                capturedWork = true;
            }
        }
        if (!sending.IsCompleted) StopChatResponse();
        await sending;
        DrainSavedTurn();
        File.WriteAllText(Path.Combine(output, "native-activity.json"), JsonSerializer.Serialize(_saved.Turns.LastOrDefault()?.Activity ?? []));
        File.WriteAllText(Path.Combine(output, "native-observations.json"), JsonSerializer.Serialize(new {
            maximum_active = maximumActive, captured_running = captured, children = _subagentRows.Values,
            outcome = _saved.Turns.LastOrDefault()?.Outcome, status = _chatStatus.Text
        }));
        if (_saved.Turns.LastOrDefault()?.Outcome != "completed") throw new IOException("Native run did not complete: " + _chatStatus.Text);
        if (_subagentRows.Count != 3 || _subagentRows.Values.Any(c => c.State != "completed"))
            throw new IOException("Expected three completed native helpers.");
        if (files.Any(p => File.ReadAllText(Path.Combine(fixture, p.Key)) != p.Value)) throw new IOException("Fixture was modified.");
        if (maximumActive < 3 || !captured) throw new IOException("Three simultaneously active native helpers were not observed.");
        foreach (var marker in new[] { "BACKEND-582", "INTERFACE-731", "QA-946" })
            if (!LastReply.Contains(marker, StringComparison.Ordinal)) throw new IOException("Missing delegated result: " + marker);
        await capture("subagents-completed");
        return new { model = modelId, permission_mode = plan ? "Plan" : "Always allow", child_count = _subagentRows.Count,
            maximum_active = maximumActive, captured_running = captured, fixture_unchanged = true, markers_received = true,
            children = _subagentRows.Values.Select(c => new { c.Name, c.State, c.StartedAt, c.CompletedAt }) };
    }

    internal void SaveNativeSubagentObservations(string output, string phase, string? error = null)
    {
        DrainSavedTurn();
        if (error is not null && _subagentRows.Count > 0)
        {
            ShowAgents(true); SetChangesOpen(true);
            foreach (var row in _subagentViews.Values) row.Root.IsExpanded = true;
        }
        Save();
        File.WriteAllText(Path.Combine(output, "native-activity.json"), JsonSerializer.Serialize(_saved.Turns.LastOrDefault()?.Activity ?? []));
        File.WriteAllText(Path.Combine(output, "native-observations.json"), JsonSerializer.Serialize(new {
            phase, error, process_id = Environment.ProcessId, chat_id = Id, parent_outcome = _saved.Turns.LastOrDefault()?.Outcome,
            parent_failure = _saved.Turns.LastOrDefault()?.FailureReason, parent_status = _chatStatus.Text,
            children = _subagentRows.Values, display = NativeSubagentDisplay(), parent_reply = LastReply
        }));
    }

    private object[] NativeSubagentDisplay() => _subagentRows.Select(pair => {
        var row = _subagentViews[pair.Key];
        return (object)new { id = pair.Key, provider = row.Provider.Text, model = row.Model.Text, requested = row.Requested.Text,
            status = row.State.Text, elapsed = row.Elapsed.Text, details = row.Details.Text, result = row.Result.Text,
            failure = row.Failure.Text, expanded = row.Root.IsExpanded };
    }).ToArray();

    private void CheckNativeSubagentDisplay(CursorSubagent child, Action<bool, string> check)
    {
        var row = _subagentViews[child.SessionId];
        var provider = SubagentModelPresentation.ProviderLabel(child);
        var model = SubagentModelPresentation.Display(child);
        check(row.Provider.Text == "Provider: " + provider && row.Model.Text == model,
            "Collapsed provider and active model match the actual saved child record, including unknown values.");
        check(row.Details.Text.Contains("Provider: " + provider, StringComparison.Ordinal)
            && row.Details.Text.Contains("Model: " + model, StringComparison.Ordinal)
            && row.Details.Text.Contains("Runtime: " + row.Elapsed.Text, StringComparison.Ordinal)
            && row.Details.Text.Contains("Status: " + row.State.Text, StringComparison.Ordinal),
            "Expanded provider, model, runtime and status match the recorded child.");
        if (SubagentModelPresentation.HasRequestedModel(child))
            check(row.Requested.Text == SubagentModelPresentation.RequestedCaption(child) + ": "
                + SubagentModelPresentation.RequestedProviderLabel(child) + " · " + SubagentModelPresentation.RequestedDisplay(child)
                && row.Details.Text.Contains(row.Requested.Text, StringComparison.Ordinal),
                "Configured or requested identity is separate from active runtime identity.");
        else check(row.Requested.Visibility == Microsoft.UI.Xaml.Visibility.Collapsed, "No requested identity is invented when absent.");
        var start = child.StartedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture) ?? "Not recorded";
        check(row.Details.Text.Contains("Started: " + start, StringComparison.Ordinal), "Expanded start time comes from the saved child record.");
        check(child.Output.Length == 0 || row.Result.Text == child.Output, "Expanded result retains the actual helper output.");
        check(string.IsNullOrWhiteSpace(child.Failure) || row.Failure.Text == child.Failure, "Expanded failure retains the actual reported failure.");
    }

    private async Task SettleExpandedNativeSubagentCaptureAsync(CursorSubagent child, Action<bool, string> check, string output, string stage)
    {
        var row = _subagentViews[child.SessionId];
        ShowAgents(true); SetChangesOpen(true); row.Root.IsExpanded = true;
        _chatPage.UpdateLayout();
        row.Root.StartBringIntoView(new Microsoft.UI.Xaml.BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
        // Expander reveals its content from the bottom. Capturing during that transition can hide
        // the first metadata lines even when all TextBlock strings have already been populated.
        await Task.Delay(900);
        var scroll = _changeFilesScroll ?? throw new IOException("Subagent viewport missing.");
        scroll.ChangeView(null, 0, null, disableAnimation: true);
        _chatPage.UpdateLayout();
        await Task.Delay(250);
        _chatPage.UpdateLayout();
        Windows.Foundation.Rect Bounds(Microsoft.UI.Xaml.FrameworkElement element) => element.TransformToVisual(scroll)
            .TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
        var header = (Microsoft.UI.Xaml.FrameworkElement)row.Root.Header;
        Microsoft.UI.Xaml.DependencyObject? ancestor = header;
        while (ancestor is not null && ancestor != row.Root)
        {
            if (ancestor is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton button) { header = button; break; }
            ancestor = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(ancestor);
        }
        var headerBounds = Bounds(header);
        var detailsBounds = Bounds(row.Details);
        var resultBounds = Bounds(row.Result);
        bool Visible(Windows.Foundation.Rect rect) => rect.Width > 0 && rect.Height > 0 && rect.X >= -2 && rect.Y >= -2
            && rect.X + rect.Width <= scroll.ViewportWidth + 2 && rect.Y + rect.Height <= scroll.ViewportHeight + 2;
        File.WriteAllText(Path.Combine(output, stage + "-readability.json"), JsonSerializer.Serialize(new {
            viewport_width = scroll.ViewportWidth, viewport_height = scroll.ViewportHeight, vertical_offset = scroll.VerticalOffset,
            header = headerBounds, identity_and_times = detailsBounds, returned_result = resultBounds,
            details_text = row.Details.Text, result_text = row.Result.Text, animation_settle_milliseconds = 900
        }));
        check(row.Root.IsExpanded && Visible(detailsBounds) && detailsBounds.Y >= headerBounds.Y + headerBounds.Height - 2,
            "The entire expanded identity/start/runtime/status block is visibly below its header and inside the viewport.");
        check(Visible(resultBounds) && resultBounds.Y >= detailsBounds.Y + detailsBounds.Height - 2,
            "The returned helper result is visibly inside the expanded viewport below the metadata.");
    }

    private async Task<object> VerifyTinyNativeSubagentAsync(Func<string, Task> capture, string output)
    {
        var checks = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition) { SaveNativeSubagentObservations(output, "assertion failed", message); throw new IOException(message); }
            checks.Add(message);
        }
        Check(_subagentRows.Count == 0, "Tiny native verification requires a fresh chat without previous helpers.");
        var codex = Environment.GetEnvironmentVariable("SOVEREIGN_QA_SUBAGENTS_PROVIDER") == "codex";
        var plan = Environment.GetEnvironmentVariable("SOVEREIGN_QA_SUBAGENTS_MODE") == "plan";
        var fixture = Path.Combine(_profileDirectory, "subagent-project");
        Directory.CreateDirectory(fixture);
        var marker = (codex ? "GPT" : "GROK") + "-TINY-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var content = "Delegated verification marker: " + marker + "\n";
        var markerPath = Path.Combine(fixture, "marker.txt");
        File.WriteAllText(markerPath, content);
        _restoring = true;
        _saved = ChatArchive.New(DeskChatConnections.CursorStored, fixture) with {
            ProjectMode = plan ? CursorProjectMode.Plan : CursorProjectMode.Agent,
            Permissions = plan ? CursorPermissionPolicy.AlwaysAsk : CursorPermissionPolicy.AlwaysAllow,
            EnableSubagents = true, SubagentPreferenceVersion = 1
        };
        _projectFolder = fixture; _chatProvider.SelectedIndex = DeskChatConnections.Cursor;
        RestoreProjectBehavior(); ResetChat(); _restoring = false;
        string modelId;
        if (codex)
        {
            var epoch = ++_projectSessionEpoch;
            _codexChat = new CodexChatClient(fixture, new CursorProjectOptions {
                Mode = _saved.ProjectMode, Permissions = _saved.Permissions, EnableSubagents = true,
                RequestPermission = RequestProjectPermissionAsync
            }, activity => { if (epoch == _projectSessionEpoch) { _activeChatTurn?.Observe(activity); _partialActivity.Enqueue(activity); } }, profile: _profileDirectory);
            using var connecting = new CancellationTokenSource(TimeSpan.FromSeconds(50));
            Check(await _codexChat.IsSubscriptionAsync(connecting.Token), "The isolated QA profile has an authenticated ChatGPT subscription.");
            var models = await _codexChat.ModelsAsync(connecting.Token);
            modelId = "gpt-6.1-sol";
            Check(models.Contains(modelId, StringComparer.Ordinal), "The requested GPT model is advertised by the isolated QA connection.");
            BindChatModels(models.Select(m => (m, (string?)m)).ToArray(), false);
            _chatReady = true; UpdateProjectHeader(); SetChatBusy(false);
        }
        else
        {
            await ConnectChatAsync();
            Check(_chatReady && _cursorChat is not null, "The Cursor connection is ready: " + _chatStatus.Text);
            modelId = (await _cursorChat!.ModelsAsync(default)).FirstOrDefault(m => m.Id.StartsWith("grok-4.7[", StringComparison.Ordinal)
                && m.Id.Contains("fast=true", StringComparison.Ordinal))?.Id ?? throw new IOException("Grok 4.7 unavailable.");
        }
        Check(SelectChatModel(modelId), "The parent uses the selected advertised routing ID.");
        _chatInput.Text = "This is one tiny native delegation check in a disposable project. Launch exactly ONE direct " + (codex ? "" : "generalPurpose ")
            + "helper named Read marker. Its sole task is to read marker.txt once, report the marker it actually contains, and finish. "
            + "You, the parent, must not read marker.txt yourself. Do not launch other or nested helpers, retry, edit files, look up sources, or use external accounts. "
            + (codex ? "The helper may use one read-only file command. It has explicit user authorization to report the result to its parent with send_message targeting /root, then finish with the same marker in its final answer. "
                : "The helper may use the native read tool; no shell commands are needed. Return the read marker in the helper result. ")
            + "After this single helper reports and finishes, validate its returned marker and continue your own final answer with PARENT-CONTINUED-TINY-0347 followed by that marker. Stop after that short answer.";
        var sending = SendChatAsync();
        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
        var maximumActive = 0;
        var capturedRunning = false;
        while (!sending.IsCompleted && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100); DrainSavedTurn();
            var active = _subagentRows.Values.Count(SubagentActive);
            maximumActive = Math.Max(maximumActive, active);
            if (active > 0 && !capturedRunning)
            {
                ShowAgents(true); SetChangesOpen(true); await capture("subagents-running"); capturedRunning = true;
            }
        }
        if (!sending.IsCompleted) StopChatResponse();
        await sending; await Task.Delay(150); DrainSavedTurn(); Save();
        SaveNativeSubagentObservations(output, "tiny provider finished");
        Check(_saved.Turns.LastOrDefault()?.Outcome == "completed", "Tiny parent completed without a provider/protocol failure: " + _chatStatus.Text);
        Check(_subagentRows.Count == 1 && maximumActive == 1 && capturedRunning, "Exactly one direct helper was observed running.");
        var child = _subagentRows.Values.Single();
        var parentRelayObserved = _saved.Turns.Last().Activity.Any(activity => activity.Subagent?.SessionId == child.SessionId
            && activity.Subagent.CurrentWork == "Reporting to parent");
        Check(child.State == "completed", "The single native helper completed: " + child.State + " " + child.Failure);
        Check(child.Output.Contains(marker, StringComparison.Ordinal), "The actual helper result contains the file's random marker.");
        Check(LastReply.Contains(marker, StringComparison.Ordinal) && LastReply.Contains("PARENT-CONTINUED-TINY-0347", StringComparison.Ordinal),
            "The parent received the helper marker and continued its final answer.");
        Check(File.ReadAllText(markerPath) == content, "The tiny fixture file is unchanged.");
        Check((child.Tools ?? []).Any(tool => tool.Input.Contains("marker.txt", StringComparison.OrdinalIgnoreCase)
            || tool.Output.Contains(marker, StringComparison.Ordinal)), "The child has recorded tool evidence for its marker-file read.");
        Check(!(_saved.Turns.Last().Activity).Any(activity => activity.Subagent is null && activity.Tool is { } tool
            && !tool.Id.StartsWith(child.SessionId + ":", StringComparison.Ordinal)
            && tool.Input.Contains("marker.txt", StringComparison.OrdinalIgnoreCase)
            && (tool.Kind == "read" || (tool.Kind is "execute" or "command")
                && (tool.Input.Contains("\"command\"", StringComparison.Ordinal) || tool.Input.Contains("\"cmd\"", StringComparison.Ordinal)
                    || tool.Input.TrimStart().StartsWith("Get-Content", StringComparison.OrdinalIgnoreCase)
                    || tool.Input.TrimStart().StartsWith("cat ", StringComparison.Ordinal)))), "The parent has no recorded direct marker-file read.");
        ShowAgents(true); SetChangesOpen(true); RefreshSubagentElapsed();
        CheckNativeSubagentDisplay(child, Check);
        await capture("subagents-completed");
        var row = _subagentViews[child.SessionId]; row.Root.IsExpanded = true;
        await SettleExpandedNativeSubagentCaptureAsync(child, Check, output, "subagents-expanded");
        await capture("subagents-expanded");
        var frozenElapsed = row.Elapsed.Text;
        await Task.Delay(1200); RefreshSubagentElapsed();
        Check(row.Elapsed.Text == frozenElapsed, "The terminal helper timer remains frozen after waiting.");
        Save();
        var savedActual = _archive.Load().SingleOrDefault(chat => chat.Id == Id);
        Check(savedActual is not null && JsonSerializer.Serialize(savedActual.Turns) == JsonSerializer.Serialize(_saved.Turns),
            "The actual chat archive contains the complete parent and helper records.");
        File.WriteAllText(Path.Combine(output, "native-chat-reference.json"), JsonSerializer.Serialize(_saved));
        SaveNativeSubagentObservations(output, "tiny checks complete");
        return new { mode = "tiny", chat_id = Id, process_id = Environment.ProcessId, profile = _profileDirectory, parent_model = modelId, provider_calls = true,
            permission_mode = plan ? "Plan" : "Always allow", child_count = 1, maximum_active = maximumActive, captured_running = capturedRunning,
            marker, fixture_unchanged = true, parent_continued = true, parent_relay_observed = parentRelayObserved,
            relay_proof_scope = codex ? (parentRelayObserved ? "Owned child interacted event addressed to parent" : "Not captured; final output is not relay proof") : "Native helper result",
            terminal_timer_frozen = true, checks, children = _subagentRows.Values,
            display = NativeSubagentDisplay(), reopen_reference = Path.Combine(output, "native-chat-reference.json") };
    }

    private async Task<object> VerifyTinySubagentReopenAsync(string chatId, Func<string, Task> capture, string output)
    {
        var checks = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition) { SaveNativeSubagentObservations(output, "reopen assertion failed", message); throw new IOException(message); }
            checks.Add(message);
        }
        Check(Id == chatId && _codexChat is null && _cursorChat is null && _planChat is null && !_chatReady && _chatCancellation is null,
            "Fresh-process reopen restores the selected chat without creating a provider connection.");
        var referencePath = Environment.GetEnvironmentVariable("SOVEREIGN_QA_SUBAGENTS_REOPEN_REFERENCE")
            ?? throw new IOException("A successful tiny-run saved-chat reference is required.");
        var expected = JsonSerializer.Deserialize<SavedChat>(File.ReadAllText(referencePath)) ?? throw new IOException("Invalid saved-chat reference.");
        using var originalReceipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(referencePath))!, "receipt.json")));
        Check(originalReceipt.RootElement.GetProperty("passed").GetBoolean()
            && originalReceipt.RootElement.GetProperty("result").GetProperty("process_id").GetInt32() != Environment.ProcessId,
            "A passed original tiny receipt is reopened by a different desktop process.");
        Check(expected.Folder == Path.Combine(_profileDirectory, "subagent-project"), "The saved tiny project belongs to this isolated QA profile.");
        Check(expected.Id == chatId && expected.Turns.LastOrDefault()?.Outcome == "completed", "The reference is the completed tiny chat with the exact saved identity.");
        Check(JsonSerializer.Serialize(expected.Turns) == JsonSerializer.Serialize(_saved.Turns), "Saved parent answer and every child event/model/result/failure/timestamp survive fresh-process restore.");
        Check(_subagentRows.Count == 1 && _subagentRows.Values.Single().State == "completed", "One completed native helper is restored from actual saved events.");
        var child = _subagentRows.Values.Single();
        ShowAgents(true); SetChangesOpen(true); RefreshSubagentElapsed();
        var row = _subagentViews[child.SessionId]; row.Root.IsExpanded = true;
        CheckNativeSubagentDisplay(child, Check);
        var frozenElapsed = row.Elapsed.Text;
        await SettleExpandedNativeSubagentCaptureAsync(child, Check, output, "subagents-reopened-expanded");
        await capture("subagents-reopened-expanded");
        await Task.Delay(1200); RefreshSubagentElapsed();
        Check(row.Elapsed.Text == frozenElapsed && _codexChat is null && _cursorChat is null && _planChat is null,
            "Reopened terminal timer stays frozen and no provider client is created.");
        SaveNativeSubagentObservations(output, "provider-free reopen complete");
        return new { mode = Environment.GetEnvironmentVariable("SOVEREIGN_QA_SUBAGENTS_RECAPTURE") == "1" ? "provider-free-recapture" : "provider-free-reopen", chat_id = Id, process_id = Environment.ProcessId, provider_calls = false,
            child_count = 1, terminal_timer_frozen = true, saved_events_results_and_models_unchanged = true, checks,
            children = _subagentRows.Values, display = NativeSubagentDisplay() };
    }
}
