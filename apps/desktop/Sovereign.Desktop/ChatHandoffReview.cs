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

    internal bool GrokbotReviewsHandoffs => _saved.GrokbotReviewsHandoffs == true;

    internal void AcceptGrokbotReview(string text, bool resume)
    {
        AcceptGrokBotMessage(text);
        if (!resume) return;
        _chatInput.Text = "continue";
        _ = SendSwarmContinueAsync();
    }

    private void DrainGrokbotReviewQueue()
    {
        if (_grokbotReviewDrained) return;
        _grokbotReviewDrained = true;
        IReadOnlyList<SwarmReviewDelivery> queued;
        try { queued = SwarmContinue.Deliver(Id); }
        catch (Exception) { return; }
        foreach (var item in queued)
            AcceptGrokbotReview(item.Text, item.Resume);
    }

    private async Task SendSwarmContinueAsync()
    {
        try { await SendChatAsync(); }
        catch (Exception ex) { _chatStatus.Text = "Response failed: " + ex.Message; }
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
        try
        {
            var folder = Path.Combine(XaiDeskIdentity.DataDirectory(), "grokbot");
            var url = (GrokbotMailbox.Open(folder).PreferencesCopy().WakeUrl ?? "").Trim();
            var key = GrokbotWakeKey.Read() ?? "";
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            var receipt = await GrokbotWake.DeliverAsync(handler, url, key, "review", false, "", cancellationToken, body: json).ConfigureAwait(false);
            var note = receipt.Note ?? "Wake failed.";
            if (!string.IsNullOrEmpty(key) && note.Contains(key, StringComparison.Ordinal)) note = "Wake failed.";
            return new SwarmWakeSend(note == "Grokbot pinged", note);
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
