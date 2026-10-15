using Sovereign.Desktop.Connector;
using Sovereign.Desktop.Core;

namespace Sovereign.Desktop;

/// <summary>
/// Grandparent wiring. Grok Bot is woken when the manager posts a status or plan update, when a SOVEREIGN_HANDOFF
/// is ready, when a task fails, and when the run finishes or stops. Routine task moves are only recorded.
/// Detection and the per-run window live in the connector project; this file only feeds them.
/// </summary>
internal sealed partial class ChatConversation
{
    private SwarmRunWaker? _runWaker;
    private SwarmBoard? _swarmEventsPrevious;

    private SwarmRunWaker RunWaker => _runWaker ??= new SwarmRunWaker(DeliverRunWakeAsync);

    /// <summary>
    /// Every manager turn lands here (see ArmHandoffReview). Status update while live; handoff; stopped run.
    /// With <paramref name="reviewComing"/> the handoff-review wake follows at once, so the events are held for it
    /// and the handoff is one wake instead of an events wake plus a review wake.
    /// </summary>
    private void NoteSwarmManagerTurn(string reply, string outcome, bool reviewComing)
    {
        try
        {
            var board = SwarmSection.TryLoad(_saved.Id);
            if (board is null || string.IsNullOrWhiteSpace(board.Id)) return;
            var live = SwarmSchedule.HasOpenWork(board);
            var now = DateTimeOffset.UtcNow;
            var events = new List<SwarmRunEvent>(SwarmRunEvents.FromReply(board.Id, reply, manager: true, live, now));
            if (outcome == "stopped" && live) events.Add(SwarmRunEvents.Stopped(board.Id, now));
            PublishSwarmEvents(board, events, holdForReview: reviewComing);
        }
        catch (Exception) { }
    }

    /// <summary>A swarm worker finished. Only a SOVEREIGN_HANDOFF in its output is an event.</summary>
    private void NoteSwarmWorkerReply(string? output)
    {
        if (string.IsNullOrWhiteSpace(output) || SwarmHandoffReview.HandoffText(output) is null) return;
        try
        {
            var board = SwarmSection.TryLoad(_saved.Id);
            if (board is null || string.IsNullOrWhiteSpace(board.Id)) return;
            PublishSwarmEvents(board, SwarmRunEvents.FromReply(board.Id, output, manager: false, live: false, DateTimeOffset.UtcNow));
        }
        catch (Exception) { }
    }

    /// <summary>Called on every board reload. Diffs against the last read of the same run.</summary>
    private void NoteSwarmBoard(SwarmBoard board)
    {
        try
        {
            var previous = _swarmEventsPrevious;
            _swarmEventsPrevious = board;
            if (previous is null || !string.Equals(previous.Id, board.Id, StringComparison.Ordinal)) return;
            PublishSwarmEvents(board, SwarmRunEvents.Diff(previous, board, DateTimeOffset.UtcNow));
        }
        catch (Exception) { }
    }

    private void PublishSwarmEvents(SwarmBoard board, IReadOnlyList<SwarmRunEvent> events, bool holdForReview = false)
    {
        if (events.Count == 0) return;
        _owner.SwarmEvents.Record(events);
        if (!events.Any(item => item.Wakes) || !RunWakeConfigured()) return;
        SwarmHandoffReview.Identity(board, out var swarmId, out var runId);
        // Legacy boards (no run id) send the review wake directly, so there is nothing to merge into: offer at once.
        if (holdForReview && !string.IsNullOrEmpty(runId)) RunWaker.Defer(board.Id, events, swarmId);
        else RunWaker.Offer(board.Id, events, swarmId);
    }

    /// <summary>1 s tick: send held wakes whose run window reopened.</summary>
    private void FlushRunWakes()
    {
        try { _runWaker?.FlushDue(); }
        catch (Exception) { }
    }

    private static bool RunWakeConfigured()
    {
        try
        {
            var folder = Path.Combine(XaiDeskIdentity.DataDirectory(), "grokbot");
            var url = GrokbotMailbox.Open(folder).PreferencesCopy().WakeUrl;
            return SwarmRunWaker.Configured(url, GrokbotWakeKey.Read());
        }
        catch (Exception) { return false; }
    }

    /// <summary>The one HTTP sender for run wakes and the handoff-review wake. Same audit and panel as before.</summary>
    private async Task<SwarmWakeSend> DeliverRunWakeAsync(string json, CancellationToken cancellationToken)
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
}
