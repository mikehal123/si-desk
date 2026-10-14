using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Sovereign.Desktop.Client;
using Sovereign.Desktop.Connector;
using Sovereign.Desktop.Core;
using Windows.System;

namespace Sovereign.Desktop;

internal sealed partial class ChatConversation
{
    private bool _holdSwarmHelpersForReview;
    private bool _grokbotReviewDrained;
    private CancellationTokenSource? _handoffReview;
    private string? _pendingReviewText;
    private int _pendingReviewId;
    private DateTimeOffset? _pendingReviewPostedAt;
    private bool _reviewSendStarted;
    private bool _reviewNoticeShown;
    private bool _reviewDispatching;
    private int _reviewWakeAttempt;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _reviewResumeTimer;

    internal bool GrokbotReviewsHandoffs => _saved.GrokbotReviewsHandoffs == true;

    internal void AcceptGrokbotReview(string text, bool resume) =>
        AcceptGrokbotReview(text, resume, 0, null, false);

    private void AcceptGrokbotReview(string text, bool resume, int reviewId, DateTimeOffset? postedAt, bool alreadyPosted)
    {
        if (!alreadyPosted)
        {
            AcceptGrokBotMessage(text);
            if (reviewId > 0)
            {
                try { SwarmContinue.MarkPosted(Id, reviewId, postedAt ?? DateTimeOffset.UtcNow); }
                catch (Exception) { }
            }
        }
        try { ReloadSwarmSection(); }
        catch (Exception) { }
        if (!resume)
        {
            if (reviewId > 0)
            {
                try { SwarmContinue.MarkSendStarted(Id, reviewId); }
                catch (Exception) { }
            }
            return;
        }
        var loop = reviewId > 0 ? reviewId : CurrentReviewLoop();
        BeginReviewResume(text, loop, postedAt ?? DateTimeOffset.UtcNow);
    }

    private void DrainGrokbotReviewQueue()
    {
        if (_grokbotReviewDrained) return;
        _grokbotReviewDrained = true;
        IReadOnlyList<SwarmReviewDelivery> queued;
        try { queued = SwarmContinue.Pending(Id); }
        catch (Exception) { return; }
        foreach (var item in queued)
            AcceptGrokbotReview(item.Text, item.Resume, item.ReviewId, item.PostedAt, item.Posted);
    }

    private void BeginReviewResume(string text, int reviewId, DateTimeOffset postedAt)
    {
        var send = SwarmReviewResume.SendText(true, text);
        if (send is null) return;
        _pendingReviewText = send;
        _pendingReviewId = reviewId;
        _pendingReviewPostedAt = postedAt;
        _reviewSendStarted = false;
        _reviewNoticeShown = false;
        var handoff = ReviewHandoffHolding();
        var plan = SwarmReviewResume.Plan(true, send, true, IsWorking && !handoff, _chatReady && ReviewComposerFree(), handoff);
        if (plan.Action == SwarmResumeAction.Queue)
        {
            try { SwarmContinue.Hold(Id, new SwarmReviewDelivery(send, true, reviewId, postedAt, true)); }
            catch (Exception) { }
            if (handoff) EnqueueReviewForActiveLoop(send);
            ArmReviewResumeTimer();
            return;
        }
        if (plan.Text is null) return;
        _reviewDispatching = true;
        _chatInput.Text = plan.Text;
        _ = SendSwarmContinueAsync();
        ArmReviewResumeTimer();
    }

    private int CurrentReviewLoop()
    {
        try { return SwarmSection.TryLoad(_saved.Id)?.ReviewLoops ?? 0; }
        catch (Exception) { return 0; }
    }

    private bool ReviewHandoffHolding() =>
        _handoffReview is not null
        && (_cursorSendLoop || _codexSendLoop)
        && _cursorPromptTask is null
        && _chatCancellation is null;

    private bool ReviewComposerFree()
    {
        var draft = (_chatInput.Text ?? "").Trim();
        return draft.Length == 0 || draft == _pendingReviewText;
    }

    private bool ReviewSendMatches(string? text) =>
        _pendingReviewText is not null && string.Equals((text ?? "").Trim(), _pendingReviewText, StringComparison.Ordinal);

    private void NoteReviewSendStarted(string? text)
    {
        if (_reviewSendStarted || !ReviewSendMatches(text)) return;
        _reviewSendStarted = true;
        _reviewResumeTimer?.Stop();
        try { SwarmContinue.MarkSendStarted(Id, _pendingReviewId); }
        catch (Exception) { }
    }

    private void EnqueueReviewForActiveLoop(string text)
    {
        var model = SelectedChatModel ?? _saved.Model;
        if (string.IsNullOrWhiteSpace(model)) return;
        if (_steeringQueue.Snapshot.Any(item => item.Text == text)) return;
        var instruction = _steeringQueue.Accept(text, model, [], true);
        ShowPendingInstruction(instruction);
        Save();
    }

    private void TryStartQueuedReview()
    {
        if (_reviewDispatching || _reviewSendStarted || _pendingReviewText is null) return;
        if (!_chatReady || IsWorking || ReviewHandoffHolding() || !ReviewComposerFree()) return;
        var existing = _steeringQueue.Snapshot.FirstOrDefault(item => item.Text == _pendingReviewText);
        if (existing is not null)
        {
            if (!(CursorChat || GrokBuildChat || CodexChat) || _steeringQueue.IsInFlight(existing.Id)) return;
            _reviewDispatching = true;
            _ = ResumeQueuedReviewAsync();
            return;
        }
        var text = SwarmReviewResume.SendText(true, _pendingReviewText);
        if (text is null) return;
        _reviewDispatching = true;
        _chatInput.Text = text;
        _ = DispatchQueuedReviewAsync();
    }

    private async Task DispatchQueuedReviewAsync()
    {
        try { await SendChatAsync(); }
        catch (Exception ex) { _chatStatus.Text = "Response failed: " + ex.Message; }
        finally { _reviewDispatching = false; }
    }

    private async Task ResumeQueuedReviewAsync()
    {
        try { await SendPendingCursorInstructionsAsync(); }
        finally { _reviewDispatching = false; }
    }

    private void ArmReviewResumeTimer()
    {
        ConsiderReviewResumeNotice();
        if (_reviewResumeTimer is not null) return;
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (queue is null) return;
        _reviewResumeTimer = queue.CreateTimer();
        _reviewResumeTimer.Interval = TimeSpan.FromSeconds(1);
        _reviewResumeTimer.IsRepeating = true;
        _reviewResumeTimer.Tick += (_, _) => ConsiderReviewResumeNotice();
        _reviewResumeTimer.Start();
    }

    private void ConsiderReviewResumeNotice()
    {
        TryStartQueuedReview();
        if (_pendingReviewPostedAt is null || _reviewSendStarted) return;
        var notice = SwarmReviewResume.Notice(_pendingReviewPostedAt.Value, DateTimeOffset.UtcNow, _reviewSendStarted);
        if (notice is null) return;
        if (!_reviewNoticeShown)
        {
            _reviewNoticeShown = true;
            _chatStatus.Text = notice;
            WriteReviewResumeNotice(notice);
        }
        EnsurePausedReviewRow();
    }

    private void WriteReviewResumeNotice(string notice)
    {
        try
        {
            var board = SwarmSection.TryLoad(_saved.Id);
            if (board is null) return;
            if ((board.ReviewNote ?? "").Contains(notice, StringComparison.Ordinal)) return;
            var note = (board.ReviewNote ?? "").TrimEnd();
            board.ReviewNote = note.Length == 0 ? notice : note + "\n" + notice;
            board.Save();
            ReloadSwarmSection();
        }
        catch (Exception) { }
    }

    private void EnsurePausedReviewRow()
    {
        if (_reviewSendStarted || _pendingReviewText is null) return;
        var model = SelectedChatModel ?? _saved.Model;
        if (string.IsNullOrWhiteSpace(model)) return;
        var existing = _steeringQueue.Snapshot.FirstOrDefault(item => item.Text == _pendingReviewText);
        if (existing is not null && _steeringQueue.IsInFlight(existing.Id))
        {
            NoteReviewSendStarted(existing.Text);
            return;
        }
        if (existing is null)
        {
            existing = _steeringQueue.Accept(_pendingReviewText, model, [], false);
            ShowPendingInstruction(existing);
        }
        _steeringQueue.KeepOutOfNextTurn(existing.Id);
        RefreshPendingInstructions();
        Save();
    }

    private void StopReviewResumeTimer()
    {
        _reviewResumeTimer?.Stop();
        _reviewResumeTimer = null;
    }

    private async Task SendSwarmContinueAsync()
    {
        try { await SendChatAsync(); }
        catch (Exception ex) { _chatStatus.Text = "Response failed: " + ex.Message; }
        finally { _reviewDispatching = false; }
    }

    private bool ArmHandoffReview(string reply, string outcome)
    {
        _holdSwarmHelpersForReview = false;
        if (outcome is not ("completed" or "redirected")) return false;
        var board = SwarmSection.TryLoad(_saved.Id);
        if (!SwarmHandoffReview.ShouldStart(_saved.GrokbotReviewsHandoffs, reply, board, _saved.GrokbotReviewLoopCap))
            return false;
        _holdSwarmHelpersForReview = true;
        return true;
    }

    private void ReleaseHandoffReviewHold() => _holdSwarmHelpersForReview = false;

    private async Task AwaitHandoffReviewAsync(string reply)
    {
        if (_disposed || _deleted || _cursorStopRequested) return;
        var board = SwarmSection.TryLoad(_saved.Id);
        if (board is null) return;
        _handoffReview?.Cancel();
        _handoffReview?.Dispose();
        _handoffReview = new CancellationTokenSource();
        var token = _handoffReview.Token;
        var previous = _chatStatus.Text;
        if (SwarmHandoffReview.ShouldStart(_saved.GrokbotReviewsHandoffs, reply, board, _saved.GrokbotReviewLoopCap))
            _chatStatus.Text = SwarmHandoffReview.WaitingNote(board);
        try
        {
            var result = await SwarmHandoffReview.WatchAsync(
                _saved.GrokbotReviewsHandoffs,
                reply,
                board,
                _saved.GrokbotReviewLoopCap,
                SendReviewWakeAsync,
                cancellationToken: token);
            if (_disposed || _deleted) return;
            if (!result.Started)
            {
                _chatStatus.Text = previous;
                return;
            }
            _chatStatus.Text = result.Status;
            if (!string.IsNullOrWhiteSpace(result.Transcript))
                AcceptGrokBotMessage(result.Transcript);
            if (result.Instruction is { Length: > 0 } text)
            {
                var model = SelectedChatModel ?? _saved.Model;
                if (!string.IsNullOrWhiteSpace(model))
                {
                    var instruction = _steeringQueue.Accept(text, model, [], true);
                    ShowPendingInstruction(instruction);
                    RefreshPendingInstructions();
                    Save();
                }
            }
            try { ReloadSwarmSection(); }
            catch (Exception) { }
        }
        catch (OperationCanceledException) { }
        finally
        {
            var source = _handoffReview;
            _handoffReview = null;
            source?.Dispose();
        }
    }

    private async Task<SwarmWakeSend> SendReviewWakeAsync(string json, CancellationToken cancellationToken)
    {
        var attempt = Interlocked.Increment(ref _reviewWakeAttempt);
        try
        {
            var folder = Path.Combine(XaiDeskIdentity.DataDirectory(), "grokbot");
            var url = (GrokbotMailbox.Open(folder).PreferencesCopy().WakeUrl ?? "").Trim();
            var key = GrokbotWakeKey.Read() ?? "";
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            var receipt = await GrokbotWake.DeliverAsync(handler, url, key, "review", false, "", cancellationToken, body: json).ConfigureAwait(false);
            var at = DateTimeOffset.Now;
            SwarmWakeRepeat.TryReadIdentity(json, out var runId, out var loop);
            try { SwarmWakeRepeat.AppendAudit(folder, SwarmWakeRepeat.Audit(at, runId, loop, attempt, receipt.StatusCode)); }
            catch (Exception) { }
            ShowWakePanel(SwarmWakeRepeat.Panel(at, receipt.StatusCode), SwarmWakeRepeat.Warning(receipt.StatusCode));
            var note = receipt.Note ?? "Wake failed.";
            if (!string.IsNullOrEmpty(key) && note.Contains(key, StringComparison.Ordinal)) note = "Wake failed.";
            return new SwarmWakeSend(note == "Grokbot pinged", note, receipt.StatusCode);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new SwarmWakeSend(false, "Wake failed.");
        }
    }

    private void ShowWakePanel(string panel, string? warning)
    {
        void Apply()
        {
            if (_swarmWakePanel is not null)
            {
                _swarmWakePanel.Text = panel;
                _swarmWakePanel.Visibility = Visibility.Visible;
            }
            if (_swarmWakeWarning is null) return;
            _swarmWakeWarning.Text = warning ?? "";
            _swarmWakeWarning.Visibility = string.IsNullOrEmpty(warning) ? Visibility.Collapsed : Visibility.Visible;
        }
        if (_swarmQueue is not null && _swarmQueue.TryEnqueue(Apply)) return;
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (queue is not null) queue.TryEnqueue(Apply);
        else Apply();
    }

    private void AttachComposerCancelsHandoffWatch()
    {
        _chatInput.PreviewKeyDown += (_, e) =>
        {
            if (e.KeyStatus.WasKeyDown || !ComposerEditCancelsReview(e.Key)) return;
            CancelHandoffWatch();
        };
        _chatInput.Paste += (_, _) => CancelHandoffWatch();
        _chatInput.TextCompositionStarted += (_, _) => CancelHandoffWatch();
    }

    private void CancelHandoffWatch()
    {
        try { _handoffReview?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private static bool ComposerEditCancelsReview(VirtualKey key) => key switch
    {
        VirtualKey.Shift or VirtualKey.Control or VirtualKey.Menu or VirtualKey.LeftWindows or VirtualKey.RightWindows
            or VirtualKey.CapitalLock or VirtualKey.Tab or VirtualKey.Escape
            or VirtualKey.Up or VirtualKey.Down or VirtualKey.Left or VirtualKey.Right
            or VirtualKey.Home or VirtualKey.End or VirtualKey.PageUp or VirtualKey.PageDown => false,
        _ => true
    };
}
