using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sovereign.Desktop.Client;
using static Sovereign.Desktop.MainWindow;

namespace Sovereign.Desktop;

internal sealed partial class ChatConversation
{
    private ChatSteeringQueue _steeringQueue = new();
    private bool _cursorSendLoop;
    private bool _codexSendLoop;
    private bool _acceptingCursorInstruction;
    private bool _cursorStopRequested;
    private Task<CursorTurnOutcome>? _cursorPromptTask;
    private Task? _cursorSteeringTask;
    private Task? _codexCorrectionTask;
    private Exception? _cursorSteeringFailure;
    private CodexComposerSteering? _codexComposer;
    private readonly HashSet<string> _acceptedIntoActiveTurn = [];
    private readonly Dictionary<string, string> _instructionStatus = [];
    private readonly Dictionary<string, string> _instructionFailures = [];
    private readonly List<string> _pendingSteerErrors = [];
    private readonly Dictionary<string, (StackPanel Root, TextBlock Preview, TextBlock Status, StackPanel Actions)> _pendingInstructionViews = [];
    private readonly Dictionary<string, string> _removedInstructionLabels = [];
    private readonly HashSet<string> _deliveredInstructionIds = [];
    private const string InterruptedDelivery = "Your message · Interrupted · may already have been sent. Press Resume main agent to send it again. It is not resent automatically.";
    private CodexComposerSteering CodexComposer => _codexComposer ??= new(_steeringQueue);
    private bool CursorComposerAvailable => !_chatBusy || ((CursorChat || GrokBuildChat) && _cursorSendLoop && _chatReady) || (CodexChat && _codexSendLoop && _chatReady);
    private CancellationTokenSource CreateTurnCancellation() => _projectFolder is not null
        ? new CancellationTokenSource()
        : new CancellationTokenSource(TimeSpan.FromMinutes(10));

    private async Task SendCursorChatAsync()
    {
        if (_disposed || _deleted || _acceptingCursorInstruction || _attachmentBusy || !CursorComposerAvailable) return;
        var rawText = _chatInput.Text;
        var text = rawText.Trim();
        var files = _draftFiles.ToArray();
        var model = SelectedChatModel ?? _saved.Model;
        if (text.Length == 0 && files.Length == 0) return;
        if (text.Length == 0) text = "Please examine the attached files.";
        _lastPrompt = text;
        var run = false;
        _acceptingCursorInstruction = true;
        SetAttachmentBusy(_attachmentBusy);
        try
        {
            if (!_cursorSendLoop && _reconnectOnSend)
            {
                _saved = _saved with { Model = model };
                await ConnectChatAsync();
                if (_disposed || !_chatReady || PhoneSendCancelled) return;
            }
            if (PhoneSendCancelled) return;
            if (!_chatReady || model is null || !HasChatModel(model))
            {
                _chatStatus.Text = "Connect and choose an available Cursor model before sending.";
                return;
            }
            var instruction = _steeringQueue.Accept(text, model, files, _cursorSendLoop);
            if (_saved.Turns.Length == 0 && _steeringQueue.Count == 1) _saved = _saved with { Title = ChatArchive.TitleFor(text) };
            // Clear only the captured draft. A draft edited during reconnect belongs to the user.
            if (_chatInput.Text == rawText) _chatInput.Text = "";
            _draftFiles.RemoveAll(f => files.Any(sent => sent.Id == f.Id));
            RenderDraftFiles();
            _chatFollowLatest = true;
            ShowPendingInstruction(instruction);
            Save(); FollowChatIfNeeded();
            if (_cursorSendLoop)
            {
                if (_cursorStopRequested)
                {
                    _steeringQueue.Pause();
                    _chatStatus.Text = "Your message · saved and not sent. Stop already applies to the main agent and its helpers.";
                    RefreshPendingInstructions(); Save();
                }
                else
                {
                    _chatStatus.Text = "Your message · queued for the main agent. It goes out with this session after the current response, and it is not sent twice.";
                    StartCursorSteering();
                }
            }
            else { _cursorStopRequested = false; run = true; }
        }
        finally { _acceptingCursorInstruction = false; SetAttachmentBusy(_attachmentBusy); }
        if (run) await RunCursorInstructionsAsync();
    }

    private void StopChatResponse()
    {
        if (CursorChat || GrokBuildChat || CodexChat)
        {
            _cursorStopRequested = true;
            if (CodexChat)
            {
                CodexComposer.Stop();
                foreach (var id in _acceptedIntoActiveTurn)
                {
                    _steeringQueue.MarkMayHaveBeenSent(id);
                    _instructionStatus[id] = InterruptedDelivery;
                }
            }
            else _steeringQueue.Pause();
            RefreshPendingInstructions(); Save();
            _chatStatus.Text = CodexChat
                ? "Main agent · stop requested. This also stops its helpers. Unsent messages stay here until you press Resume main agent."
                : "Main agent · stop requested. This also stops its helpers. Pending messages stay here until you press Resume main agent.";
        }
        _chatCancellation?.Cancel();
    }

    internal bool StopFromPhone()
    {
        if (_disposed || _deleted) return false;
        if (!IsWorking && _chatCancellation is null) return false;
        _phoneSubmission?.Cancel();
        StopChatResponse();
        return true;
    }

    private void StartCursorSteering()
    {
        if (_cursorSteeringTask is not null || _cursorPromptTask is null || _cursorPromptTask.IsCompleted ||
            _cursorStopRequested || !_steeringQueue.HasAutomaticPending || _chatCancellation is null) return;
        _cursorSteeringTask = RequestCursorSteeringAsync(_cursorPromptTask, _chatCancellation);
    }

    private async Task RequestCursorSteeringAsync(Task<CursorTurnOutcome> prompt, CancellationTokenSource lifetime)
    {
        try
        {
            // set_model can precede the prompt. Retry only for this exact send, never for its successor.
            while (ReferenceEquals(prompt, _cursorPromptTask) && ReferenceEquals(lifetime, _chatCancellation) &&
                !prompt.IsCompleted && !lifetime.IsCancellationRequested && !_cursorStopRequested && _steeringQueue.HasAutomaticPending)
            {
                if (await SteerActiveAgentAsync(lifetime.Token)) return;
                await Task.Delay(50, lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _cursorSteeringFailure = ex;
            _steeringQueue.Pause();
            _chatStatus.Text = "Correction could not interrupt the response: " + ex.Message + ". Pending instructions are kept for explicit review and send.";
            RefreshPendingInstructions(); Save();
        }
    }

    private async Task RunCursorInstructionsAsync()
    {
        if (_cursorSendLoop || _disposed || _deleted || !_chatReady) return;
        _cursorSendLoop = true;
        SetChatBusy(true);
        try
        {
            while (!_disposed && !_deleted && !_cursorStopRequested && _steeringQueue.HasAutomaticPending)
            {
                var batch = _steeringQueue.BeginBatch();
                if (batch.Length == 0) break;
                var model = batch[0].Model;
                var prompt = string.Join("\n\n", batch.Select(i => i.Text));
                var images = new List<ChatImage>();
                var prepared = new List<string>();
                var content = new StringBuilder();
                ChatActivityView? turn = null;
                var outcome = "failed";
                string? failureReason = null;
                _cursorSteeringFailure = null; _cursorSteeringTask = null; _cursorPromptTask = null;
                _chatCancellation = CreateTurnCancellation();
                SetChatBusy(true); RefreshPendingInstructions();
                try
                {
                    if (!HasChatModel(model)) throw new IOException("The captured Cursor model is unavailable. Edit the pending instruction to choose another model.");
                    SelectChatModel(model);
                    _chatStatus.Text = "Preparing captured instructions. Stop keeps them available for explicit send.";
                    foreach (var instruction in batch)
                    {
                        var attachmentCopy = instruction.Attachments.ToArray();
                        var preparedResult = await PrepareAttachmentsAsync(instruction.Text, attachmentCopy, _chatCancellation.Token);
                        prepared.Add(preparedResult.Text); images.AddRange(preparedResult.Images);
                    }
                    _chatCancellation.Token.ThrowIfCancellationRequested();
                    var sentText = prepared.Count == 1 ? prepared[0]
                        : "Apply these user instructions together, in submission order:\n\n" + string.Join("\n\n", prepared.Select((s, i) => $"Instruction {i + 1}:\n{s}"));
                    var restoredPrompt = WithReportedHelpers(WithTeamContext(_needsContext ? ChatArchive.RestoreContext(_saved, sentText) : sentText));
                    var label = ReadPicker(_chatModel.Items.Cast<object>().FirstOrDefault(i => ReadPicker(i).Id == model)).Label ?? model;
                    turn = new ChatActivityView(label, UpdateChangesPanel, FollowChatIfNeeded, SetComposerStatus, PauseFollow, Path.Combine(_profileDirectory, "chat-activity"), _classicChat);
                    _activeChatTurn = turn; TrackChatTurn(turn); _chatMessages.Children.Add(turn.Root);
                    batch = _steeringQueue.MarkDispatched(batch);
                    BeginSavedTurn(prompt, model, batch.SelectMany(i => i.Attachments).ToArray(), sentText, batch);
                    FollowChatIfNeeded();
                    _chatStatus.Text = "Main agent · working. Enter queues a correction for the main agent. Stop stops the main agent and its helpers. A message is not resent on its own.";
                    void Delta(string text) { content.Append(text); turn.Append(text); _partialReply.Enqueue(text); }
                    ApplySkillGuidance(restoredPrompt);
                    _cursorPromptTask = SendActiveAgentAsync(model, restoredPrompt, Delta, _chatCancellation.Token, images);
                    StartCursorSteering();
                    var result = await _cursorPromptTask;
                    if (_cursorSteeringTask is not null) await _cursorSteeringTask;
                    if (_cursorSteeringFailure is not null) throw _cursorSteeringFailure;
                    outcome = result == CursorTurnOutcome.Redirected ? "redirected" : "completed";
                    _needsContext = false;
                    _chatStatus.Text = outcome == "redirected" ? "Response redirected. Partial text and file edits are kept; accepted corrections continue in this session."
                        : "Response complete.";
                }
                catch (OperationCanceledException)
                {
                    outcome = "stopped";
                    _steeringQueue.Pause();
                    _chatStatus.Text = "Response stopped. Partial text and file edits are kept. Review pending instructions below before explicitly sending, editing or removing them.";
                    DisconnectCursorAfterFailure();
                }
                catch (Exception ex)
                {
                    if (GrokBuildChat && GrokBuildConnectionText.OfferCursorGrok47(ex))
                    {
                        failureReason = GrokBuildConnectionText.ChatFailureText(ex);
                        var shown = failureReason ?? ex.Message;
                        turn?.ShowUsageFailure(shown);
                        _chatStatus.MaxLines = 0;
                        _chatStatus.TextTrimming = TextTrimming.None;
                        _chatStatus.Text = shown;
                        ShowSwitchToCursorGrok47();
                    }
                    else
                    {
                        failureReason = ProjectAgentException.Describe(ex); turn?.ShowFailure(failureReason);
                        _chatStatus.Text = "Response failed: " + ex.Message + ". Pending instructions are kept for review; reconnect and explicitly send them when ready.";
                    }
                    _steeringQueue.Pause();
                    DisconnectCursorAfterFailure();
                }
                finally
                {
                    if (_cursorSteeringTask is not null) await _cursorSteeringTask;
                    if (turn is not null) { turn.Finish(outcome); FinishSavedTurn(content.ToString(), outcome, turn, failureReason); }
                    _steeringQueue.CompleteBatch(batch, outcome is "completed" or "redirected");
                    FinishSubagents(outcome); _activeChatTurn = null;
                    _cursorPromptTask = null; _cursorSteeringTask = null;
                    _chatCancellation.Dispose(); _chatCancellation = null;
                    RefreshPendingInstructions(); Save();
                }
                if (outcome is not ("completed" or "redirected")) break;
            }
        }
        finally
        {
            _cursorSendLoop = false;
            SetChatBusy(false); RefreshPendingInstructions(); Save();
        }
    }

    private void ShowSwitchToCursorGrok47()
    {
        if (_switchCursorGrok is null) return;
        _switchCursorGrok.Visibility = Visibility.Visible;
    }

    private void SwitchToCursorGrok47()
    {
        if (IsWorking)
        {
            _chatStatus.Text = "Finish or stop the current response before changing provider.";
            return;
        }
        _chatProvider.SelectedIndex = DeskChatConnections.Cursor;
        if (!CursorChat) return;
        foreach (var item in _chatModel.Items)
        {
            var picked = ReadPicker(item);
            if (picked.Id is not { Length: > 0 } id) continue;
            if (!DeskChatConnections.PrefersGrok47(new CursorChatModel(id, picked.Label ?? id))) continue;
            if (SelectChatModel(id)) return;
        }
        _chatStatus.Text = "Switched to Cursor. Connect to load Grok 4.7.";
    }

    private void DisconnectCursorAfterFailure()
    {
        _reconnectOnSend = true; _needsContext = true; _projectSessionEpoch++;
        _chatReady = false;
        if (GrokBuildChat) { _grokBuild?.Dispose(); _grokBuild = null; }
        else { _cursorChat?.Dispose(); _cursorChat = null; }
    }

    private string WithReportedHelpers(string prompt)
    {
        var report = SubagentChatPanels.ReportedForPrompt(_subagentRows.Values);
        return report.Length == 0 ? prompt : prompt + "\n\n" + report;
    }

    private Task<CursorTurnOutcome> SendActiveAgentAsync(string model, string prompt, Action<string> delta, CancellationToken ct, IReadOnlyList<ChatImage> images) =>
        GrokBuildChat
            ? (_grokBuild ?? throw new IOException("Connect Grok Build before sending.")).SendAsync(model, prompt, delta, ct, images)
            : (_cursorChat ?? throw new IOException("Connect Cursor before sending.")).SendAsync(model, prompt, delta, ct, images);

    private Task<bool> SteerActiveAgentAsync(CancellationToken ct) =>
        GrokBuildChat
            ? (_grokBuild ?? throw new IOException("Connect Grok Build before sending.")).RequestSteeringAsync(ct)
            : (_cursorChat ?? throw new IOException("Connect Cursor before sending.")).RequestSteeringAsync(ct);

    private async Task SendCodexChatAsync()
    {
        if (_disposed || _deleted || _acceptingCursorInstruction || _attachmentBusy || !CursorComposerAvailable) return;
        var rawText = _chatInput.Text;
        var text = rawText.Trim();
        var files = _draftFiles.ToArray();
        var model = SelectedChatModel ?? _saved.Model;
        if (text.Length == 0 && files.Length == 0) return;
        if (text.Length == 0) text = "Please examine the attached files.";
        _lastPrompt = text;
        var run = false;
        _acceptingCursorInstruction = true;
        SetAttachmentBusy(_attachmentBusy);
        try
        {
            if (!_codexSendLoop && _reconnectOnSend)
            {
                var requestedModel = model ?? _saved.Model;
                _saved = _saved with { Model = requestedModel };
                await ConnectChatAsync();
                if (_disposed || !_chatReady || PhoneSendCancelled) return;
                model = SelectedChatModel;
                if (model != requestedModel)
                {
                    _chatStatus.Text = "Your previous model is unavailable. Choose a model before sending again.";
                    return;
                }
            }
            if (PhoneSendCancelled) return;
            if (!_chatReady || string.IsNullOrWhiteSpace(model) || !HasChatModel(model))
            {
                _chatStatus.Text = "Connect and choose an available model before sending.";
                return;
            }
            if (_codexSendLoop)
            {
                var correction = AcceptCodexCorrectionAsync(rawText, text, model, files);
                _codexCorrectionTask = correction;
                try { await correction; }
                finally { if (ReferenceEquals(_codexCorrectionTask, correction)) _codexCorrectionTask = null; }
                return;
            }
            var instruction = _steeringQueue.Accept(text, model, files, false);
            if (_saved.Turns.Length == 0 && _steeringQueue.Count == 1) _saved = _saved with { Title = ChatArchive.TitleFor(text) };
            // Clear only the captured draft. A draft edited during reconnect belongs to the user.
            if (_chatInput.Text == rawText) _chatInput.Text = "";
            _draftFiles.RemoveAll(f => files.Any(sent => sent.Id == f.Id));
            RenderDraftFiles();
            _chatFollowLatest = true;
            ShowPendingInstruction(instruction);
            Save(); FollowChatIfNeeded();
            _cursorStopRequested = false;
            CodexComposer.Resume();
            run = true;
        }
        finally { _acceptingCursorInstruction = false; SetAttachmentBusy(_attachmentBusy); }
        if (run) await RunCodexInstructionsAsync();
    }

    private async Task AcceptCodexCorrectionAsync(string rawText, string text, string model, ChatAttachment[] files)
    {
        var token = _chatCancellation?.Token ?? CancellationToken.None;
        var submission = await CodexComposer.SubmitDuringRunAsync(text, model, files, (instruction, ct) =>
            _codexChat is null
                ? Task.FromException<bool>(new IOException("OpenAI is not connected in this chat."))
                : _codexChat.RequestSteeringAsync(instruction, ct), token);
        if (submission.IgnoredDuplicate || _disposed) return;
        if (_chatInput.Text == rawText) _chatInput.Text = "";
        _draftFiles.RemoveAll(f => files.Any(sent => sent.Id == f.Id));
        RenderDraftFiles();
        _chatFollowLatest = true;
        if (submission.Instruction is not { } instruction) return;
        if (submission.Result == CodexComposerSteerResult.Accepted)
        {
            _acceptedIntoActiveTurn.Add(instruction.Id);
            _instructionStatus[instruction.Id] = "Accepted into this response";
            if (TryAppendInstructionToActiveTurn(instruction))
            {
                _steeringQueue.DeliverToActiveTurn(instruction.Id);
                _acceptedIntoActiveTurn.Remove(instruction.Id);
                _instructionStatus.Remove(instruction.Id);
                _removedInstructionLabels[instruction.Id] = "Accepted into this response";
            }
        }
        else if (submission.Failure is not null)
        {
            _instructionFailures[instruction.Id] = submission.Failure;
            RecordSteerFailure(submission.Failure);
        }
        ShowPendingInstruction(instruction);
        _chatStatus.Text = submission.Result switch
        {
            CodexComposerSteerResult.Accepted => "Correction accepted in this chat. The active response continues.",
            CodexComposerSteerResult.QueuedForNextTurn => "The current response had already finished. This instruction is queued once for the next response in this chat.",
            _ => submission.Failure is null
                ? "Stop requested. Your new instruction is saved and remains unsent."
                : "Correction could not be sent: " + submission.Failure + ". It is kept in this chat for explicit review and send."
        };
        RefreshPendingInstructions(); Save(); FollowChatIfNeeded();
    }

    private void RecordSteerFailure(string message)
    {
        if (_savedTurnIndex < 0) { _pendingSteerErrors.Add(message); return; }
        _partialActivity.Enqueue(new CursorActivityEvent("notice", "Correction failed: " + message));
        DrainSavedTurn();
    }

    private void FlushSteerFailures()
    {
        if (_pendingSteerErrors.Count == 0 || _savedTurnIndex < 0) return;
        foreach (var message in _pendingSteerErrors)
            _partialActivity.Enqueue(new CursorActivityEvent("notice", "Correction failed: " + message));
        _pendingSteerErrors.Clear();
        DrainSavedTurn();
    }

    private bool TryAppendInstructionToActiveTurn(SavedChatInstruction instruction)
    {
        if (_savedTurnIndex < 0 || _savedTurnIndex >= _saved.Turns.Length) return false;
        var turn = _saved.Turns[_savedTurnIndex];
        var existing = turn.Instructions ?? [];
        if (existing.Any(i => i.Id == instruction.Id)) return true;
        _saved.Turns[_savedTurnIndex] = turn with { Instructions = [.. existing, instruction] };
        return true;
    }

    private void RememberAcceptedCorrections(bool delivered)
    {
        foreach (var id in _acceptedIntoActiveTurn.ToArray())
        {
            var instruction = _steeringQueue.Snapshot.FirstOrDefault(i => i.Id == id);
            if (instruction is not null) TryAppendInstructionToActiveTurn(instruction);
            if (!delivered)
            {
                _steeringQueue.MarkMayHaveBeenSent(id);
                _instructionStatus[id] = InterruptedDelivery;
                _acceptedIntoActiveTurn.Remove(id);
                continue;
            }
            if (instruction is null || _savedTurnIndex < 0) { if (instruction is null) _acceptedIntoActiveTurn.Remove(id); continue; }
            _steeringQueue.DeliverToActiveTurn(id);
            _acceptedIntoActiveTurn.Remove(id);
            _instructionStatus.Remove(id);
            _removedInstructionLabels[id] = "Accepted into this response";
        }
    }

    private async Task RunCodexInstructionsAsync()
    {
        if (_codexSendLoop || _disposed || _deleted || !_chatReady) return;
        _codexSendLoop = true;
        SetChatBusy(true);
        try
        {
            while (!_disposed && !_deleted && !_cursorStopRequested && _steeringQueue.HasAutomaticPending)
            {
                var batch = _steeringQueue.BeginBatch();
                if (batch.Length == 0) break;
                var model = batch[0].Model;
                var prompt = string.Join("\n\n", batch.Select(i => i.Text));
                var images = new List<ChatImage>();
                var prepared = new List<string>();
                var content = new System.Text.StringBuilder();
                ChatActivityView? turn = null;
                var outcome = "failed";
                string? failureReason = null;
                // Project work may take hours. Stop still cancels; wall time must not.
                _chatCancellation = CreateTurnCancellation();
                SetChatBusy(true); RefreshPendingInstructions();
                try
                {
                    if (!HasChatModel(model)) throw new IOException("The captured model is unavailable. Edit the pending instruction to choose another model.");
                    SelectChatModel(model);
                    _chatStatus.Text = "Preparing captured instructions. Stop keeps them available for explicit send.";
                    foreach (var instruction in batch)
                    {
                        var attachmentCopy = instruction.Attachments.ToArray();
                        var preparedResult = await PrepareAttachmentsAsync(instruction.Text, attachmentCopy, _chatCancellation.Token);
                        prepared.Add(preparedResult.Text); images.AddRange(preparedResult.Images);
                    }
                    _chatCancellation.Token.ThrowIfCancellationRequested();
                    var sentText = prepared.Count == 1 ? prepared[0]
                        : "Apply these user instructions together, in submission order:\n\n" + string.Join("\n\n", prepared.Select((s, i) => $"Instruction {i + 1}:\n{s}"));
                    var restoredPrompt = WithReportedHelpers(WithTeamContext(_needsContext ? ChatArchive.RestoreContext(_saved, sentText) : sentText));
                    var label = ReadPicker(_chatModel.Items.Cast<object>().FirstOrDefault(i => ReadPicker(i).Id == model)).Label ?? model;
                    turn = new ChatActivityView(label, UpdateChangesPanel, FollowChatIfNeeded, SetComposerStatus, PauseFollow, Path.Combine(_profileDirectory, "chat-activity"), _classicChat);
                    _activeChatTurn = turn; TrackChatTurn(turn); _chatMessages.Children.Add(turn.Root);
                    batch = _steeringQueue.MarkDispatched(batch);
                    BeginSavedTurn(prompt, model, batch.SelectMany(i => i.Attachments).ToArray(), sentText, batch);
                    FlushSteerFailures();
                    RememberAcceptedCorrections(delivered: true);
                    FollowChatIfNeeded();
                    _chatStatus.Text = "Main agent · working. Enter queues a correction for the main agent. Stop stops the main agent and its helpers. A message is not resent on its own.";
                    void Delta(string text) { content.Append(text); turn.Append(text); _partialReply.Enqueue(text); }
                    ApplySkillGuidance(restoredPrompt);
                    await _codexChat!.SendAsync(model, restoredPrompt, Delta, _chatCancellation.Token, images);
                    outcome = "completed";
                    _needsContext = false;
                    _chatStatus.Text = _projectFolder is not null ? "Response complete. Review Changed files in this turn." : "Response complete.";
                }
                catch (OperationCanceledException)
                {
                    outcome = "stopped";
                    _steeringQueue.Pause();
                    _chatStatus.Text = "Response stopped. Partial text and file edits are kept. Unsent instructions stay in this chat until you explicitly send them.";
                    DisconnectCodexAfterFailure();
                }
                catch (Exception ex)
                {
                    failureReason = ProjectAgentException.Describe(ex); turn?.ShowFailure(failureReason);
                    _steeringQueue.Pause();
                    _chatStatus.Text = "Response failed: " + ex.Message + ". Pending instructions are kept for review; reconnect and explicitly send them when ready.";
                    DisconnectCodexAfterFailure();
                }
                finally
                {
                    var correction = _codexCorrectionTask;
                    if (correction is not null) await correction;
                    RememberAcceptedCorrections(outcome is "completed");
                    if (turn is not null) { turn.Finish(outcome); FinishSavedTurn(content.ToString(), outcome, turn, failureReason); }
                    _steeringQueue.CompleteBatch(batch, outcome is "completed");
                    FinishSubagents(outcome); _activeChatTurn = null;
                    _chatCancellation.Dispose(); _chatCancellation = null;
                    RefreshPendingInstructions(); Save();
                }
                if (outcome is not "completed") break;
            }
        }
        finally
        {
            _codexSendLoop = false;
            SetChatBusy(false); RefreshPendingInstructions(); Save();
        }
    }

    private void DisconnectCodexAfterFailure()
    {
        _reconnectOnSend = true; _needsContext = true; _projectSessionEpoch++;
        _chatReady = false; _codexChat?.Dispose(); _codexChat = null;
    }

    internal void OfferMainAgentCorrection(string helperName, string note)
    {
        var name = string.IsNullOrWhiteSpace(helperName) ? "the helper" : helperName.Trim();
        var body = string.IsNullOrWhiteSpace(note)
            ? "Correction for helper \"" + name + "\": "
            : "Correction for helper \"" + name + "\": " + note.Trim();
        if (!string.IsNullOrWhiteSpace(_chatInput.Text))
        {
            _chatStatus.Text = "Main agent · the composer already has a draft. Send or clear it before adding a helper correction.";
            return;
        }
        _chatInput.Text = body;
        _chatInput.Focus(FocusState.Programmatic);
        _chatStatus.Text = "Main agent · the correction is in the composer and has not been sent. Press Send to queue it. This helper cannot take a direct reply.";
    }

    private void RevealDeliveredInstruction(SavedChatInstruction instruction)
    {
        if (!_deliveredInstructionIds.Add(instruction.Id)) return;
        _chatMessages.Children.Add(UserMessage(instruction.Text));
        if (instruction.Attachments.Length > 0) _chatMessages.Children.Add(AttachmentHistory(instruction.Attachments));
    }

    private void ShowPendingInstruction(SavedChatInstruction instruction, bool representedInHistory = false)
    {
        if (_pendingInstructionViews.ContainsKey(instruction.Id)) { RefreshPendingInstructions(); return; }
        if (representedInHistory) _deliveredInstructionIds.Add(instruction.Id);
        var preview = new TextBlock
        {
            Text = instruction.Text,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(preview, instruction.Text);
        var status = Note("Queued");
        status.TextWrapping = TextWrapping.Wrap;
        status.VerticalAlignment = VerticalAlignment.Center;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DeskTheme.Space4, VerticalAlignment = VerticalAlignment.Center };
        var resume = Button("Resume main agent", SendPendingCursorInstructionsAsync);
        ToolTipService.SetToolTip(resume, "Sends this pending message through the main agent. It is not sent again until you press this.");
        actions.Children.Add(resume);
        actions.Children.Add(Button("Edit in composer", () => EditPendingInstruction(instruction.Id)));
        actions.Children.Add(Button("Remove pending instruction", () => RemovePendingInstruction(instruction.Id)));
        var root = new StackPanel { Spacing = DeskTheme.Space4 };
        if (!representedInHistory)
        {
            // Show the captured message once in the transcript, before delivery is confirmed.
            root.Tag = instruction.Id;
            root.Children.Add(UserMessage(instruction.Text));
            if (instruction.Attachments.Length > 0) root.Children.Add(AttachmentHistory(instruction.Attachments));
            _deliveredInstructionIds.Add(instruction.Id);
        }
        root.Children.Add(status); root.Children.Add(actions);
        _pendingInstructionViews[instruction.Id] = (root, preview, status, actions);
        _chatMessages.Children.Add(root);
        StylePendingRow(preview, status, actions);
        RefreshPendingInstructions();
        FollowChatIfNeeded();
    }

    private void RefreshPendingInstructions()
    {
        var pending = _steeringQueue.Snapshot.ToDictionary(i => i.Id);
        foreach (var (id, view) in _pendingInstructionViews)
        {
            var remains = pending.TryGetValue(id, out var instruction);
            var detail = !remains ? _removedInstructionLabels.GetValueOrDefault(id, "Your message · sent")
                : _instructionStatus.TryGetValue(id, out var forced) ? forced
                : _steeringQueue.IsInFlight(id) ? "Your message · Waiting for the main agent to accept it."
                : _instructionFailures.TryGetValue(id, out var failure) ? "Your message · Failed · explicit send required. " + failure
                : _codexSendLoop && !_cursorStopRequested && _steeringQueue.IsAutomatic(id) ? "Your message · Queued for the next response from the main agent."
                : _cursorSendLoop && !_cursorStopRequested && _steeringQueue.HasAutomaticPending ? "Your message · Accepted · queued for the main agent in submission order."
                : instruction?.MayHaveBeenSent == true ? InterruptedDelivery
                : "Your message · Not sent · press Resume main agent. Nothing is sent automatically.";
            if (remains && instruction is not null && (_steeringQueue.IsInFlight(id) || instruction.MayHaveBeenSent))
                RevealDeliveredInstruction(instruction);
            view.Status.Text = !remains ?
                _removedInstructionLabels.TryGetValue(id, out var removed) ? removed : "Delivered"
                : _instructionFailures.TryGetValue(id, out var error) ? "Failed · " + error
                : _instructionStatus.TryGetValue(id, out var forcedStatus) ?
                    forcedStatus == InterruptedDelivery ? "Interrupted · check before resending" : forcedStatus
                : instruction?.MayHaveBeenSent == true && !_steeringQueue.IsInFlight(id) ? "Interrupted · check before resending"
                : _steeringQueue.IsInFlight(id) ? "Waiting for acceptance"
                : _cursorStopRequested || (!_cursorSendLoop && !_codexSendLoop) ? "Not sent"
                : "Queued";
            ToolTipService.SetToolTip(view.Status, detail);
            var discarded = _removedInstructionLabels.GetValueOrDefault(id) is "Moved to composer · unsent" or "Removed from pending instructions";
            view.Root.Visibility = !discarded && (remains || view.Root.Tag is string) ? Visibility.Visible : Visibility.Collapsed;
            view.Actions.Visibility = remains && !IsWorking && !_steeringQueue.IsInFlight(id) ? Visibility.Visible : Visibility.Collapsed;
            foreach (var button in view.Actions.Children.OfType<Button>()) button.IsEnabled = !_cursorSendLoop && !_codexSendLoop && !_acceptingCursorInstruction && !_attachmentBusy;
        }
        var seen = new Dictionary<string, (string Id, int Count)>(StringComparer.Ordinal);
        foreach (var (id, view) in _pendingInstructionViews)
        {
            if (!pending.ContainsKey(id) || view.Root.Visibility != Visibility.Visible) continue;
            var key = view.Preview.Text;
            if (!seen.TryGetValue(key, out var first)) seen[key] = (id, 1);
            else
            {
                view.Root.Visibility = Visibility.Collapsed;
                seen[key] = (first.Id, first.Count + 1);
            }
        }
        foreach (var group in seen.Values)
        {
            if (group.Count < 2 || !_pendingInstructionViews.TryGetValue(group.Id, out var view)) continue;
            var marker = " · ×" + group.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!view.Status.Text.Contains(" · ×", StringComparison.Ordinal)) view.Status.Text += marker;
        }
    }

    private async Task SendPendingCursorInstructionsAsync()
    {
        if (!(CursorChat || GrokBuildChat || CodexChat) || IsWorking || _disposed || _deleted || _steeringQueue.Count == 0) return;
        // A saved instruction's model remains captured even when the current picker changes.
        if (!_chatReady) { await ConnectChatAsync(); if (_disposed || !_chatReady) return; }
        _cursorStopRequested = false;
        if (CodexChat) CodexComposer.Resume();
        _steeringQueue.ResumeAll();
        if (CodexChat) await RunCodexInstructionsAsync();
        else await RunCursorInstructionsAsync();
    }

    private void EditPendingInstruction(string id)
    {
        if (IsWorking || _steeringQueue.IsInFlight(id)) return;
        var instruction = _steeringQueue.Snapshot.FirstOrDefault(i => i.Id == id);
        if (instruction is null) return;
        if (!string.IsNullOrWhiteSpace(_chatInput.Text) || _draftFiles.Count > 0)
        {
            _chatStatus.Text = "Send or clear the current draft before moving a pending instruction into the composer.";
            return;
        }
        _steeringQueue.Remove(id);
        _chatInput.Text = instruction.Text; _draftFiles.AddRange(instruction.Attachments);
        if (_chatReady) SelectChatModel(instruction.Model);
        else _saved = _saved with { Model = instruction.Model };
        _removedInstructionLabels[id] = "Moved to composer · unsent";
        RefreshPendingInstructions();
        RenderDraftFiles(); Save(); _chatInput.Focus(FocusState.Programmatic);
    }

    private void RemovePendingInstruction(string id)
    {
        if (IsWorking) return;
        var text = _steeringQueue.Snapshot.FirstOrDefault(instruction => instruction.Id == id)?.Text;
        var ids = text is null
            ? new[] { id }
            : _steeringQueue.Snapshot.Where(instruction => instruction.Text == text).Select(instruction => instruction.Id).ToArray();
        var removed = false;
        foreach (var item in ids)
        {
            if (!_steeringQueue.Remove(item)) continue;
            _removedInstructionLabels[item] = "Removed from pending instructions";
            removed = true;
        }
        if (!removed) return;
        RefreshPendingInstructions();
        Save();
    }
}
