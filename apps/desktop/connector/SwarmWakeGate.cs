using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sovereign.Desktop.Connector;

/// <summary>What the gate decided. Send carries the wake body; Hold names when the run's window reopens.</summary>
public sealed record SwarmWakeDecision(bool Send, string? Body, DateTimeOffset? ReopenAt, IReadOnlyList<SwarmRunEvent> Events, bool IncludesReview)
{
    public static readonly SwarmWakeDecision Nothing = new(false, null, null, [], false);
}

/// <summary>
/// One wake per run per <see cref="Window"/>. The first wake in a window goes out at once; everything else that fires
/// inside the window is held and goes out as one wake listing them all when the window reopens. The handoff-review
/// wake shares the same window, so a review never doubles a wake. Pure: the clock is a parameter.
/// </summary>
public sealed class SwarmWakeGate
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    private sealed class RunState
    {
        public DateTimeOffset? LastSent;
        public List<SwarmRunEvent> Pending = [];
        public string? ReviewJson;
        public string? SwarmId;
        public int Sequence;
        /// <summary>Set by <see cref="Defer"/>: a review wake for this run is expected, so hold the events for it until then.</summary>
        public DateTimeOffset? HoldUntil;
    }

    private readonly Dictionary<string, RunState> _runs = new(StringComparer.Ordinal);

    /// <summary>Offers run events. Only events that wake count; routine task moves are dropped here.</summary>
    public SwarmWakeDecision Offer(string runId, IEnumerable<SwarmRunEvent> events, DateTimeOffset now, string? swarmId = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        var state = State(runId);
        if (!string.IsNullOrWhiteSpace(swarmId)) state.SwarmId = swarmId;
        var any = false;
        foreach (var item in events)
        {
            if (item is null || !item.Wakes) continue;
            state.Pending.Add(item);
            any = true;
        }
        return any ? Decide(runId, state, now) : SwarmWakeDecision.Nothing;
    }

    /// <summary>
    /// Offers events that a handoff review is about to follow (a manager SOVEREIGN_HANDOFF that arms the review).
    /// They are held, not sent, so the review wake carries them and the handoff is one wake. If no review arrives
    /// within <paramref name="grace"/>, the next <see cref="Flush"/> sends them alone.
    /// </summary>
    public SwarmWakeDecision Defer(string runId, IEnumerable<SwarmRunEvent> events, DateTimeOffset now, TimeSpan grace, string? swarmId = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        var state = State(runId);
        if (!string.IsNullOrWhiteSpace(swarmId)) state.SwarmId = swarmId;
        var any = false;
        foreach (var item in events)
        {
            if (item is null || !item.Wakes) continue;
            state.Pending.Add(item);
            any = true;
        }
        if (!any) return HasPending(state) ? Hold(state, now) : SwarmWakeDecision.Nothing;
        var until = now + grace;
        if (state.HoldUntil is not DateTimeOffset existing || existing < until) state.HoldUntil = until;
        return Hold(state, now);
    }

    /// <summary>Offers the handoff-review wake body (<see cref="SwarmHandoffReview.WakeJson"/>). The latest review body wins and ends any <see cref="Defer"/> hold.</summary>
    public SwarmWakeDecision OfferReview(string runId, string reviewJson, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewJson);
        var state = State(runId);
        state.ReviewJson = reviewJson;
        state.HoldUntil = null;
        return Decide(runId, state, now);
    }

    /// <summary>The review watch was cancelled before its held wake went out; drop it. Held events stay and flush on their own.</summary>
    public void DropReview(string runId)
    {
        if (!_runs.TryGetValue(Key(runId), out var state)) return;
        state.ReviewJson = null;
        state.HoldUntil = null;
    }

    /// <summary>Sends what a run has been holding once its window reopened.</summary>
    public SwarmWakeDecision Flush(string runId, DateTimeOffset now)
    {
        if (!_runs.TryGetValue(Key(runId), out var state) || !HasPending(state)) return SwarmWakeDecision.Nothing;
        return Decide(runId, state, now);
    }

    /// <summary>Runs holding something, for the periodic flush.</summary>
    public IReadOnlyList<string> Holding()
    {
        var runs = new List<string>();
        foreach (var pair in _runs)
            if (HasPending(pair.Value)) runs.Add(pair.Key);
        return runs;
    }

    public bool HasReviewPending(string runId) =>
        _runs.TryGetValue(Key(runId), out var state) && state.ReviewJson is not null;

    private SwarmWakeDecision Decide(string runId, RunState state, DateTimeOffset now)
    {
        if (!HasPending(state)) return SwarmWakeDecision.Nothing;
        if (state.HoldUntil is DateTimeOffset until && now < until && state.ReviewJson is null) return Hold(state, now);
        if (state.LastSent is DateTimeOffset last && now >= last && now - last < Window) return Hold(state, now);
        var events = state.Pending.ToArray();
        var review = state.ReviewJson;
        state.Sequence++;
        var body = Body(Key(runId), state.SwarmId, events, review, state.Sequence);
        state.LastSent = now;
        state.Pending = [];
        state.ReviewJson = null;
        state.HoldUntil = null;
        return new SwarmWakeDecision(true, body, null, events, review is not null);
    }

    private static SwarmWakeDecision Hold(RunState state, DateTimeOffset now)
    {
        var reopen = state.LastSent is DateTimeOffset last && now - last < Window ? last + Window : now;
        if (state.HoldUntil is DateTimeOffset until && state.ReviewJson is null && until > reopen) reopen = until;
        return new SwarmWakeDecision(false, null, reopen, state.Pending.ToArray(), state.ReviewJson is not null);
    }

    private static bool HasPending(RunState state) => state.Pending.Count > 0 || state.ReviewJson is not null;

    private RunState State(string runId)
    {
        var key = Key(runId);
        if (!_runs.TryGetValue(key, out var state))
        {
            state = new RunState();
            _runs[key] = state;
        }
        return state;
    }

    private static string Key(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId)) throw new ArgumentException("run id is required.", nameof(runId));
        return runId.Trim();
    }

    /// <summary>
    /// The wake body. With a review pending, the body is the review wake JSON (unchanged) plus an "events" array;
    /// otherwise it is an events wake: message_id, act false, preview, kind "events", run_id, swarm_id, events.
    /// </summary>
    public static string Body(string runId, string? swarmId, IReadOnlyList<SwarmRunEvent> events, string? reviewJson, int sequence)
    {
        var list = new JsonArray();
        foreach (var item in events)
        {
            var node = new JsonObject
            {
                ["at"] = item.At.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                ["kind"] = item.KindName,
                ["summary"] = GrokbotWake.Scrub(item.Summary)
            };
            if (!string.IsNullOrWhiteSpace(item.Detail)) node["detail"] = GrokbotWake.Scrub(item.Detail);
            list.Add(node);
        }
        if (reviewJson is not null)
        {
            JsonObject? review = null;
            try { review = JsonNode.Parse(reviewJson) as JsonObject; }
            catch (JsonException) { }
            if (review is not null)
            {
                if (list.Count > 0) review["events"] = list;
                return review.ToJsonString();
            }
        }
        var preview = events.Count == 0 ? "" : GrokbotWake.Preview(events[0].Summary);
        var payload = new JsonObject
        {
            ["message_id"] = "events-" + runId + "-" + sequence.ToString(CultureInfo.InvariantCulture),
            ["act"] = false,
            ["preview"] = preview,
            ["kind"] = "events",
            ["run_id"] = runId,
            ["events"] = list
        };
        if (!string.IsNullOrWhiteSpace(swarmId)) payload["swarm_id"] = swarmId;
        return payload.ToJsonString();
    }
}

/// <summary>
/// Drives <see cref="SwarmWakeGate"/> for the app: sends at once when the window is open, otherwise holds and sends
/// on <see cref="FlushDue"/> (call it from a 1 s tick). The review wake awaits the same window and gets the real
/// send result. The clock is injected; nothing here touches WinUI.
/// </summary>
public sealed class SwarmRunWaker
{
    private readonly SwarmWakeGate _gate = new();
    private readonly Func<string, CancellationToken, Task<SwarmWakeSend>> _deliver;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _lock = new();
    private readonly Dictionary<string, TaskCompletionSource<SwarmWakeSend>> _reviewWaiters = new(StringComparer.Ordinal);
    private int _sent;

    public SwarmRunWaker(Func<string, CancellationToken, Task<SwarmWakeSend>> deliver, Func<DateTimeOffset>? clock = null)
    {
        _deliver = deliver ?? throw new ArgumentNullException(nameof(deliver));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Wakes handed to the deliver callback so far.</summary>
    public int Sent => Volatile.Read(ref _sent);

    /// <summary>Run wakes go out only with a wake URL and a sender key. Without them nothing is offered and nothing is sent.</summary>
    public static bool Configured(string? wakeUrl, string? senderKey) =>
        !string.IsNullOrWhiteSpace(wakeUrl) && !string.IsNullOrEmpty(senderKey);

    /// <summary>Offers events for a run. Returns the send task when a wake went out now, otherwise null (held or nothing to wake on).</summary>
    public Task<SwarmWakeSend>? Offer(string runId, IEnumerable<SwarmRunEvent> events, string? swarmId = null)
    {
        SwarmWakeDecision decision;
        lock (_lock) decision = _gate.Offer(runId, events, _clock(), swarmId);
        return decision.Send ? SendAsync(runId, decision, CancellationToken.None) : null;
    }

    /// <summary>How long held events wait for the review wake that was announced with <see cref="Defer"/>.</summary>
    public static readonly TimeSpan ReviewGrace = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Events from a manager turn that arms a handoff review. Held so the review wake that follows carries them
    /// (one wake for the handoff); sent alone by <see cref="FlushDue"/> if no review comes within <see cref="ReviewGrace"/>.
    /// </summary>
    public void Defer(string runId, IEnumerable<SwarmRunEvent> events, string? swarmId = null)
    {
        lock (_lock) _gate.Defer(runId, events, _clock(), ReviewGrace, swarmId);
    }

    /// <summary>The handoff-review wake. Sends now when the run's window is open; otherwise waits for the shared window and returns that send's result.</summary>
    public Task<SwarmWakeSend> OfferReviewAsync(string runId, string reviewJson, CancellationToken ct = default)
    {
        SwarmWakeDecision decision;
        TaskCompletionSource<SwarmWakeSend> waiter;
        lock (_lock)
        {
            decision = _gate.OfferReview(runId, reviewJson, _clock());
            if (decision.Send) return SendAsync(runId, decision, ct);
            waiter = new TaskCompletionSource<SwarmWakeSend>(TaskCreationOptions.RunContinuationsAsynchronously);
            _reviewWaiters[runId.Trim()] = waiter;
        }
        if (ct.CanBeCanceled)
            ct.Register(() =>
            {
                lock (_lock)
                {
                    _gate.DropReview(runId);
                    _reviewWaiters.Remove(runId.Trim());
                }
                waiter.TrySetCanceled(ct);
            });
        return waiter.Task;
    }

    /// <summary>Sends held wakes whose window reopened. Returns the number of wakes sent.</summary>
    public int FlushDue()
    {
        var sends = new List<(string RunId, SwarmWakeDecision Decision)>();
        lock (_lock)
        {
            var now = _clock();
            foreach (var runId in _gate.Holding())
            {
                var decision = _gate.Flush(runId, now);
                if (decision.Send) sends.Add((runId, decision));
            }
        }
        foreach (var (runId, decision) in sends) _ = SendAsync(runId, decision, CancellationToken.None);
        return sends.Count;
    }

    private async Task<SwarmWakeSend> SendAsync(string runId, SwarmWakeDecision decision, CancellationToken ct)
    {
        Interlocked.Increment(ref _sent);
        SwarmWakeSend result;
        try { result = await _deliver(decision.Body!, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { result = new SwarmWakeSend(false, "Wake failed."); }
        if (decision.IncludesReview)
        {
            TaskCompletionSource<SwarmWakeSend>? waiter;
            lock (_lock) _reviewWaiters.Remove(runId.Trim(), out waiter);
            waiter?.TrySetResult(result);
        }
        return result;
    }
}
