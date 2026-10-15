using Sovereign.Desktop.Connector;
using Xunit;

namespace XaiDesk.Tests;

public sealed class SwarmRunViewTests
{
    private static readonly DateTime Start = new(2026, 10, 15, 16, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Reviewed_done_with_a_doing_task_ends_at_the_board_write()
    {
        var board = Board("Reviewed (done)", "doing");
        var write = Start.AddMinutes(5);
        var now = Start.AddMinutes(40);
        Assert.Equal(SwarmRunPhase.Done, SwarmRunView.Phase(board));
        var state = SwarmRunView.Describe(board, Start, Start, write, now, _ => "5:00 PM");
        Assert.Equal(SwarmRunPhase.Done, state.Phase);
        Assert.Equal("Done 5:00 PM · 5:00", state.Label);
        Assert.Equal(write, state.EndedUtc);
        Assert.Equal(TimeSpan.FromMinutes(5), state.Elapsed);
    }

    [Fact]
    public void All_done_with_an_empty_review_is_done()
    {
        Assert.Equal(SwarmRunPhase.Done, SwarmRunView.Phase(Board("", "done", "done")));
        Assert.Equal(SwarmRunPhase.Done, SwarmRunView.Phase(Board(null, "done")));
    }

    [Fact]
    public void Reviewed_continue_is_done_only_when_every_task_is_done()
    {
        Assert.Equal(SwarmRunPhase.Done, SwarmRunView.Phase(Board("Reviewed (continue)", "done", "done")));
        Assert.Equal(SwarmRunPhase.Running, SwarmRunView.Phase(Board("Reviewed (continue)", "doing")));
    }

    [Fact]
    public void Empty_review_with_done_and_failed_stops_at_the_board_write()
    {
        var board = Board("", "done", "failed");
        var write = Start.AddMinutes(5);
        var now = Start.AddMinutes(40);
        Assert.False(SwarmSection.StopRunClock(board.ReviewStatus, board.Tasks.Select(task => task.Status)));
        Assert.Equal(SwarmRunPhase.Stopped, SwarmRunView.Phase(board));
        var state = SwarmRunView.Describe(board, Start, Start, write, now, _ => "5:00 PM");
        Assert.Equal(SwarmRunPhase.Stopped, state.Phase);
        Assert.Equal("Stopped 5:00 PM · 5:00", state.Label);
        Assert.Equal(write, state.EndedUtc);
    }

    [Fact]
    public void Waiting_for_grokbot_or_review_stays_running_when_every_task_is_done()
    {
        var write = Start.AddMinutes(5);
        var now = Start.AddMinutes(1);
        foreach (var review in new[] { "Waiting for Grokbot", "Waiting for review" })
        {
            var board = Board(review, "done", "done");
            Assert.Equal(SwarmRunPhase.Running, SwarmRunView.Phase(board));
            var state = SwarmRunView.Describe(board, Start, now, write, now, _ => "5:00 PM");
            Assert.Equal(SwarmRunPhase.Running, state.Phase);
            Assert.Null(state.EndedUtc);
        }
    }

    [Fact]
    public void Stall_starts_at_fifteen_minutes_and_a_done_run_stays_done()
    {
        var doing = Board("", "doing");
        var almost = Start.AddMinutes(14).AddSeconds(59);
        var stalledAt = Start.AddMinutes(15);
        var running = SwarmRunView.Describe(doing, Start, Start, Start, almost);
        Assert.Equal(SwarmRunPhase.Running, running.Phase);
        Assert.Equal("Running · 14:59", running.Label);

        var stalled = SwarmRunView.Describe(doing, Start, Start, Start, stalledAt);
        Assert.Equal(SwarmRunPhase.Stalled, stalled.Phase);
        Assert.Equal("Stalled — waiting on 1 running task · 15:00", stalled.Label);
        Assert.Null(stalled.EndedUtc);

        var done = Board("Reviewed (done)", "doing");
        var write = Start.AddMinutes(5);
        var later = SwarmRunView.Describe(done, Start, Start, write, Start.AddMinutes(40), _ => "5:00 PM");
        Assert.Equal(SwarmRunPhase.Done, later.Phase);
        Assert.Equal("Done 5:00 PM · 5:00", later.Label);
        Assert.Equal(write, later.EndedUtc);
    }

    [Fact]
    public void Task_label_marks_open_chips_when_the_run_has_finished()
    {
        Assert.Equal("todo · run ended", SwarmRunView.TaskLabel("todo", true));
        Assert.Equal("doing · run ended", SwarmRunView.TaskLabel("doing", true));
        Assert.Equal("review · run ended", SwarmRunView.TaskLabel("review", true));
        Assert.Equal("done", SwarmRunView.TaskLabel("done", true));
        Assert.Equal("failed", SwarmRunView.TaskLabel("failed", true));
        Assert.Equal("todo", SwarmRunView.TaskLabel("todo", false));
        Assert.Equal("doing", SwarmRunView.TaskLabel("doing", false));
        Assert.Equal("review", SwarmRunView.TaskLabel("review", false));
        Assert.Equal("done", SwarmRunView.TaskLabel("done", false));
        Assert.Equal("failed", SwarmRunView.TaskLabel("failed", false));
    }

    private static SwarmBoard Board(string? review, params string[] statuses) => new()
    {
        Id = "run",
        ReviewStatus = review ?? "",
        Tasks = statuses.Select((status, index) => new SwarmTask
        {
            Id = "t-" + index,
            Title = "Task " + index,
            Status = status
        }).ToList()
    };
}
