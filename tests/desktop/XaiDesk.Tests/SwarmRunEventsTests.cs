using System.Text.Json;
using Sovereign.Desktop.Connector;
using Xunit;

namespace XaiDesk.Tests;

/// <summary>Grandparent wiring: which run events wake Grok Bot, and one wake per run per minute.</summary>
public sealed class SwarmRunEventsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);

    private static SwarmBoard Board(string id, params (string Id, string Role, string Status)[] tasks)
    {
        var board = new SwarmBoard { Id = id, Goal = "Ship", Root = Path.Combine(Path.GetTempPath(), "swarm", "runs") };
        foreach (var (taskId, role, status) in tasks)
            board.Tasks.Add(new SwarmTask { Id = taskId, Title = "Task " + taskId, Role = role, Status = status });
        return board;
    }

    private static SwarmRunEvent Status(string run, int minute = 0) =>
        new(T0.AddMinutes(minute), run, SwarmRunEventKind.Status, "manager update: progress");

    private sealed class Deliveries
    {
        public readonly List<string> Bodies = [];
        public Task<SwarmWakeSend> Deliver(string body, CancellationToken ct)
        {
            Bodies.Add(body);
            return Task.FromResult(new SwarmWakeSend(true, "Grokbot pinged", 200));
        }
    }

    [Fact]
    public void Manager_status_update_is_one_status_event_while_the_run_is_live()
    {
        var live = SwarmRunEvents.FromReply("run-1", "Half way there.\nMore detail.", manager: true, live: true, T0);
        var item = Assert.Single(live);
        Assert.Equal(SwarmRunEventKind.Status, item.Kind);
        Assert.Equal("manager update: Half way there.", item.Summary);
        Assert.True(item.Wakes);
        Assert.Empty(SwarmRunEvents.FromReply("run-1", "Half way there.", manager: true, live: false, T0));
        Assert.Empty(SwarmRunEvents.FromReply("run-1", "worker chatter", manager: false, live: true, T0));
    }

    [Fact]
    public void Routine_task_moves_are_recorded_but_never_wake()
    {
        var before = Board("run-1", ("t1", "engineer", "todo"), ("t2", "researcher", "doing"));
        var doing = Board("run-1", ("t1", "engineer", "doing"), ("t2", "researcher", "doing"));
        var moved = SwarmRunEvents.Diff(before, doing, T0);
        var item = Assert.Single(moved);
        Assert.Equal(SwarmRunEventKind.Task, item.Kind);
        Assert.Equal("t1 (engineer) Task t1: todo -> doing", item.Summary);
        Assert.False(item.Wakes);
        var review = Board("run-1", ("t1", "engineer", "doing"), ("t2", "researcher", "review"));
        var toReview = Assert.Single(SwarmRunEvents.Diff(doing, review, T0));
        Assert.Equal(SwarmRunEventKind.Task, toReview.Kind);
        Assert.Contains("doing -> review", toReview.Summary, StringComparison.Ordinal);
        Assert.Empty(SwarmRunEvents.Diff(review, review, T0));
        Assert.Empty(SwarmRunEvents.Diff(null, review, T0));
        Assert.Empty(SwarmRunEvents.Diff(Board("other", ("t1", "engineer", "todo")), review, T0));

        var gate = new SwarmWakeGate();
        Assert.False(gate.Offer("run-1", moved, T0).Send);
        Assert.Empty(gate.Holding());
        var log = new SwarmRunEvents();
        log.Record(moved);
        Assert.Single(log.Since("run-1", null));
    }

    [Fact]
    public void Handoff_ready_from_manager_or_worker_is_one_wake_with_the_block_as_detail()
    {
        const string reply = "Done.\nSOVEREIGN_HANDOFF\nstatus: ready\nnext: review\nEND_SOVEREIGN_HANDOFF\n";
        var worker = SwarmRunEvents.FromReply("run-1", reply, manager: false, live: true, T0);
        var item = Assert.Single(worker);
        Assert.Equal(SwarmRunEventKind.Handoff, item.Kind);
        Assert.Equal("worker handoff ready: status: ready", item.Summary);
        Assert.StartsWith("SOVEREIGN_HANDOFF", item.Detail, StringComparison.Ordinal);
        Assert.EndsWith("END_SOVEREIGN_HANDOFF", item.Detail, StringComparison.Ordinal);
        var manager = SwarmRunEvents.FromReply("run-1", reply, manager: true, live: true, T0);
        Assert.Equal(2, manager.Count);
        Assert.Equal(SwarmRunEventKind.Status, manager[0].Kind);
        Assert.Equal("manager handoff ready: status: ready", manager[1].Summary);

        var sent = new Deliveries();
        var waker = new SwarmRunWaker(sent.Deliver, () => T0);
        Assert.NotNull(waker.Offer("run-1", worker, "swarm-a"));
        Assert.Equal(1, waker.Sent);
        using var body = JsonDocument.Parse(Assert.Single(sent.Bodies));
        Assert.Equal("events", body.RootElement.GetProperty("kind").GetString());
        Assert.Equal("run-1", body.RootElement.GetProperty("run_id").GetString());
        Assert.Equal("swarm-a", body.RootElement.GetProperty("swarm_id").GetString());
        Assert.False(body.RootElement.GetProperty("act").GetBoolean());
        var row = Assert.Single(body.RootElement.GetProperty("events").EnumerateArray());
        Assert.Equal("handoff", row.GetProperty("kind").GetString());
        Assert.Contains("SOVEREIGN_HANDOFF", row.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Contains("handoff\tworker handoff ready: status: ready\nSOVEREIGN_HANDOFF", item.Line(), StringComparison.Ordinal);
    }

    [Fact]
    public void Task_failed_is_one_wake_with_the_reason()
    {
        var before = Board("run-1", ("t1", "engineer", "doing"), ("t2", "verifier", "todo"));
        var after = Board("run-1", ("t1", "engineer", "failed"), ("t2", "verifier", "todo"));
        after.Tasks[0].VerifierNote = "tests red\nsecond line";
        var events = SwarmRunEvents.Diff(before, after, T0);
        var item = Assert.Single(events);
        Assert.Equal(SwarmRunEventKind.Failed, item.Kind);
        Assert.Equal("t1 (engineer) Task t1: doing -> failed \u00b7 tests red", item.Summary);
        var sent = new Deliveries();
        var waker = new SwarmRunWaker(sent.Deliver, () => T0);
        Assert.NotNull(waker.Offer("run-1", events));
        Assert.Equal(1, waker.Sent);
        Assert.Contains("\"kind\":\"failed\"", sent.Bodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Run_finished_done_and_stopped_each_wake_once()
    {
        var open = Board("run-1", ("t1", "engineer", "done"), ("t2", "verifier", "doing"));
        var done = Board("run-1", ("t1", "engineer", "done"), ("t2", "verifier", "done"));
        var events = SwarmRunEvents.Diff(open, done, T0);
        Assert.Equal(2, events.Count);
        Assert.Equal(SwarmRunEventKind.Task, events[0].Kind);
        var finished = events[1];
        Assert.Equal(SwarmRunEventKind.Finished, finished.Kind);
        Assert.Equal("run finished: done 2/2, failed 0", finished.Summary);
        Assert.Empty(SwarmRunEvents.Diff(done, done, T0));

        var sent = new Deliveries();
        var waker = new SwarmRunWaker(sent.Deliver, () => T0);
        Assert.NotNull(waker.Offer("run-1", events));
        Assert.Equal(1, waker.Sent);
        using var body = JsonDocument.Parse(sent.Bodies[0]);
        var row = Assert.Single(body.RootElement.GetProperty("events").EnumerateArray());
        Assert.Equal("finished", row.GetProperty("kind").GetString());

        var stopped = SwarmRunEvents.Stopped("run-2", T0);
        Assert.Equal(SwarmRunEventKind.Finished, stopped.Kind);
        Assert.Equal("run stopped", stopped.Summary);
        Assert.NotNull(waker.Offer("run-2", [stopped]));
        Assert.Equal(2, waker.Sent);
        Assert.Contains("run stopped", sent.Bodies[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Several_events_inside_sixty_seconds_become_one_wake_listing_all()
    {
        var now = T0;
        var sent = new Deliveries();
        var waker = new SwarmRunWaker(sent.Deliver, () => now);
        Assert.NotNull(waker.Offer("run-1", [Status("run-1")]));
        Assert.Equal(1, waker.Sent);

        now = T0.AddSeconds(10);
        Assert.Null(waker.Offer("run-1", [new SwarmRunEvent(now, "run-1", SwarmRunEventKind.Failed, "t1 (engineer) Build: doing -> failed")]));
        now = T0.AddSeconds(30);
        Assert.Null(waker.Offer("run-1", [new SwarmRunEvent(now, "run-1", SwarmRunEventKind.Handoff, "manager handoff ready: all done", "SOVEREIGN_HANDOFF\nEND_SOVEREIGN_HANDOFF")]));
        now = T0.AddSeconds(59);
        Assert.Equal(0, waker.FlushDue());
        Assert.Equal(1, waker.Sent);

        now = T0.AddSeconds(60);
        Assert.Equal(1, waker.FlushDue());
        Assert.Equal(2, waker.Sent);
        using var body = JsonDocument.Parse(sent.Bodies[1]);
        var rows = body.RootElement.GetProperty("events").EnumerateArray().Select(row => row.GetProperty("kind").GetString()).ToArray();
        Assert.Equal(new[] { "failed", "handoff" }, rows.Select(kind => kind ?? "").ToArray());
        Assert.Equal("events-run-1-2", body.RootElement.GetProperty("message_id").GetString());
        Assert.Equal(0, waker.FlushDue());
    }

    [Fact]
    public void After_sixty_seconds_the_next_event_wakes_at_once()
    {
        var now = T0;
        var sent = new Deliveries();
        var waker = new SwarmRunWaker(sent.Deliver, () => now);
        Assert.NotNull(waker.Offer("run-1", [Status("run-1")]));
        now = T0.AddSeconds(61);
        Assert.NotNull(waker.Offer("run-1", [Status("run-1", 1)]));
        Assert.Equal(2, waker.Sent);
        now = T0.AddSeconds(90);
        Assert.Null(waker.Offer("run-1", [Status("run-1", 1)]));
        Assert.Equal(2, waker.Sent);
    }

    [Fact]
    public void Separate_runs_have_independent_windows()
    {
        var sent = new Deliveries();
        var waker = new SwarmRunWaker(sent.Deliver, () => T0);
        Assert.NotNull(waker.Offer("run-1", [Status("run-1")]));
        Assert.NotNull(waker.Offer("run-2", [Status("run-2")]));
        Assert.Null(waker.Offer("run-1", [Status("run-1")]));
        Assert.Null(waker.Offer("run-2", [Status("run-2")]));
        Assert.Equal(2, waker.Sent);
        Assert.Contains("\"run_id\":\"run-1\"", sent.Bodies[0], StringComparison.Ordinal);
        Assert.Contains("\"run_id\":\"run-2\"", sent.Bodies[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Review_wake_shares_the_run_window_and_gets_the_merged_send_result()
    {
        var now = T0;
        var sent = new Deliveries();
        var waker = new SwarmRunWaker(sent.Deliver, () => now);
        Assert.NotNull(waker.Offer("run-1", [Status("run-1")]));
        var reviewJson = "{\"message_id\":\"review-run-1-1\",\"act\":false,\"kind\":\"review\",\"run_id\":\"run-1\",\"review_loop\":1}";
        now = T0.AddSeconds(20);
        var pending = waker.OfferReviewAsync("run-1", reviewJson);
        Assert.False(pending.IsCompleted);
        Assert.Equal(1, waker.Sent);
        now = T0.AddSeconds(40);
        Assert.Null(waker.Offer("run-1", [new SwarmRunEvent(now, "run-1", SwarmRunEventKind.Failed, "t3 (verifier) Check: doing -> failed")]));
        now = T0.AddSeconds(60);
        Assert.Equal(1, waker.FlushDue());
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Ok);
        Assert.Equal("Grokbot pinged", result.Note);
        Assert.Equal(2, waker.Sent);
        using var body = JsonDocument.Parse(sent.Bodies[1]);
        Assert.Equal("review", body.RootElement.GetProperty("kind").GetString());
        Assert.Equal("review-run-1-1", body.RootElement.GetProperty("message_id").GetString());
        Assert.Equal(1, body.RootElement.GetProperty("review_loop").GetInt32());
        var row = Assert.Single(body.RootElement.GetProperty("events").EnumerateArray());
        Assert.Equal("failed", row.GetProperty("kind").GetString());

        now = T0.AddSeconds(200);
        var direct = await waker.OfferReviewAsync("run-1", reviewJson);
        Assert.True(direct.Ok);
        Assert.Equal(3, waker.Sent);
        Assert.DoesNotContain("\"events\"", sent.Bodies[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Manager_handoff_that_arms_a_review_is_one_wake_carrying_the_review_and_the_events()
    {
        var now = T0;
        var sent = new Deliveries();
        var waker = new SwarmRunWaker(sent.Deliver, () => now);
        const string reply = "All tasks landed.\nSOVEREIGN_HANDOFF\nverified: ready\nEND_SOVEREIGN_HANDOFF";
        var events = SwarmRunEvents.FromReply("run-1", reply, manager: true, live: true, now);
        Assert.Equal(2, events.Count); // status + handoff

        // The UI knows the review is armed, so the manager-turn events are held for the review wake.
        waker.Defer("run-1", events, "swarm-a");
        Assert.Equal(0, waker.Sent);
        now = T0.AddSeconds(1);
        Assert.Equal(0, waker.FlushDue());

        var reviewJson = "{\"message_id\":\"review-run-1-1\",\"act\":false,\"kind\":\"review\",\"run_id\":\"run-1\",\"review_loop\":1,\"handoff\":\"SOVEREIGN_HANDOFF\"}";
        now = T0.AddSeconds(2);
        var result = await waker.OfferReviewAsync("run-1", reviewJson).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Ok);
        Assert.Equal(1, waker.Sent);
        using var body = JsonDocument.Parse(Assert.Single(sent.Bodies));
        Assert.Equal("review", body.RootElement.GetProperty("kind").GetString());
        Assert.Equal("review-run-1-1", body.RootElement.GetProperty("message_id").GetString());
        Assert.Equal("SOVEREIGN_HANDOFF", body.RootElement.GetProperty("handoff").GetString());
        var kinds = body.RootElement.GetProperty("events").EnumerateArray().Select(row => row.GetProperty("kind").GetString() ?? "").ToArray();
        Assert.Equal(new[] { "status", "handoff" }, kinds);

        // Nothing else goes out for that handoff: not at once, not when the window reopens.
        now = T0.AddSeconds(30);
        Assert.Equal(0, waker.FlushDue());
        now = T0.AddSeconds(90);
        Assert.Equal(0, waker.FlushDue());
        Assert.Equal(1, waker.Sent);
    }

    [Fact]
    public void Deferred_events_go_out_alone_when_no_review_arrives_within_the_grace()
    {
        var now = T0;
        var sent = new Deliveries();
        var waker = new SwarmRunWaker(sent.Deliver, () => now);
        var handoff = new SwarmRunEvent(now, "run-1", SwarmRunEventKind.Handoff, "manager handoff ready: done", "SOVEREIGN_HANDOFF\nEND_SOVEREIGN_HANDOFF");
        waker.Defer("run-1", [handoff]);
        Assert.Equal(0, waker.Sent);
        now = T0 + SwarmRunWaker.ReviewGrace - TimeSpan.FromSeconds(1);
        Assert.Equal(0, waker.FlushDue());
        now = T0 + SwarmRunWaker.ReviewGrace;
        Assert.Equal(1, waker.FlushDue());
        Assert.Equal(1, waker.Sent);
        using var body = JsonDocument.Parse(sent.Bodies[0]);
        Assert.Equal("events", body.RootElement.GetProperty("kind").GetString());
        Assert.Equal("handoff", Assert.Single(body.RootElement.GetProperty("events").EnumerateArray()).GetProperty("kind").GetString());

        // A deferred handoff while the window is already closed still ends up as one merged wake when the review comes.
        now = T0.AddSeconds(20);
        waker.Defer("run-1", [handoff]);
        var pending = waker.OfferReviewAsync("run-1", "{\"kind\":\"review\",\"run_id\":\"run-1\"}");
        Assert.False(pending.IsCompleted);
        now = T0 + SwarmRunWaker.ReviewGrace + SwarmWakeGate.Window;
        Assert.Equal(1, waker.FlushDue());
        Assert.Equal(2, waker.Sent);
        Assert.Contains("\"kind\":\"review\"", sent.Bodies[1], StringComparison.Ordinal);
        Assert.Contains("\"events\":[", sent.Bodies[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelled_review_wait_drops_the_held_review()
    {
        var now = T0;
        var sent = new Deliveries();
        var waker = new SwarmRunWaker(sent.Deliver, () => now);
        Assert.NotNull(waker.Offer("run-1", [Status("run-1")]));
        using var cts = new CancellationTokenSource();
        now = T0.AddSeconds(5);
        var pending = waker.OfferReviewAsync("run-1", "{\"run_id\":\"run-1\"}", cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        now = T0.AddSeconds(61);
        Assert.Equal(0, waker.FlushDue());
        Assert.Equal(1, waker.Sent);
    }

    [Fact]
    public void Events_log_filters_by_since_and_caps_at_fifty()
    {
        var log = new SwarmRunEvents();
        for (var i = 0; i < 70; i++)
            log.Record(new SwarmRunEvent(T0.AddSeconds(i), "run-1", SwarmRunEventKind.Task, "move " + i));
        log.Record(new SwarmRunEvent(T0, "run-2", SwarmRunEventKind.Status, "other run"));
        var recent = log.Since("run-1", null);
        Assert.Equal(50, recent.Count);
        Assert.Equal("move 20", recent[0].Summary);
        Assert.Equal("move 69", recent[^1].Summary);
        var since = log.Since("run-1", T0.AddSeconds(67));
        Assert.Equal(["move 68", "move 69"], since.Select(item => item.Summary).ToArray());
        Assert.Empty(log.Since("run-3", null));
        Assert.Equal(T0, SwarmRunEvents.ParseSince("2026-10-14T10:00:00Z"));
        Assert.Null(SwarmRunEvents.ParseSince(" "));
        Assert.Throws<ArgumentException>(() => SwarmRunEvents.ParseSince("yesterday"));
        Assert.Equal("2026-10-14T10:00:00Z\trun-2\tstatus\tother run", SwarmRunEvents.Text(log.Since("run-2", null)));
    }

    [Fact]
    public void No_wake_is_offered_when_the_wake_url_or_key_is_not_set()
    {
        Assert.False(SwarmRunWaker.Configured(null, "key"));
        Assert.False(SwarmRunWaker.Configured("  ", "key"));
        Assert.False(SwarmRunWaker.Configured("https://wake.example/hook", null));
        Assert.False(SwarmRunWaker.Configured("https://wake.example/hook", ""));
        Assert.True(SwarmRunWaker.Configured("https://wake.example/hook", "key"));
    }

    [Fact]
    public void Gate_body_without_a_review_is_an_events_wake_and_keeps_no_secrets()
    {
        var body = SwarmWakeGate.Body("run-1", null, [Status("run-1")], null, 1);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("events-run-1-1", json.RootElement.GetProperty("message_id").GetString());
        Assert.False(json.RootElement.TryGetProperty("swarm_id", out _));
        Assert.Equal("manager update: progress", json.RootElement.GetProperty("preview").GetString());
        Assert.Throws<ArgumentException>(() => new SwarmWakeGate().Offer(" ", [], T0));
    }
}
