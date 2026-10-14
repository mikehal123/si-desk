using Sovereign.Desktop.Connector;
using Xunit;

namespace XaiDesk.Tests;

public sealed class SwarmClockTests
{
    [Fact]
    public void Reviewed_done_stops_even_when_a_task_is_open()
    {
        Assert.True(SwarmSection.StopRunClock("Reviewed (done)", ["doing"]));
        Assert.True(SwarmSection.StopRunClock("Reviewed (done)", []));
    }

    [Fact]
    public void All_done_with_an_empty_review_stops()
    {
        Assert.True(SwarmSection.StopRunClock("", ["done", "done"]));
        Assert.True(SwarmSection.StopRunClock(null, ["done"]));
    }

    [Fact]
    public void Waiting_for_grokbot_does_not_stop_when_every_task_is_done()
    {
        Assert.False(SwarmSection.StopRunClock("Waiting for Grokbot", ["done", "done"]));
        Assert.False(SwarmSection.StopRunClock("Waiting for review", ["done"]));
    }

    [Fact]
    public void A_failed_task_with_an_empty_review_does_not_stop()
    {
        Assert.False(SwarmSection.StopRunClock("", ["done", "failed"]));
        Assert.False(SwarmSection.StopRunClock("", []));
        Assert.False(SwarmSection.StopRunClock("", null));
    }

    [Fact]
    public void All_done_with_reviewed_continue_stops()
    {
        Assert.True(SwarmSection.StopRunClock("Reviewed (continue)", ["done", "done"]));
        Assert.False(SwarmSection.StopRunClock("Reviewed (continue)", ["done", "doing"]));
    }
}
