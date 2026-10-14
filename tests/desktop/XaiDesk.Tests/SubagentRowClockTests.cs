using Sovereign.Desktop.Client;
using Xunit;

namespace XaiDesk.Tests;

public sealed class SubagentRowClockTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("stopped")]
    [InlineData("stop requested")]
    [InlineData("disconnected")]
    [InlineData("interrupted")]
    [InlineData("unconfirmed")]
    public void Ended_and_disconnected_rows_leave_the_live_list(string state) =>
        Assert.True(SubagentChatPanels.ShowsInOldSection(state));

    [Theory]
    [InlineData("running")]
    [InlineData("pending")]
    [InlineData("queued")]
    [InlineData("waiting")]
    [InlineData("blocked")]
    [InlineData("unknown")]
    public void Live_and_blocked_rows_stay_in_the_live_list(string state) =>
        Assert.False(SubagentChatPanels.ShowsInOldSection(state));

    [Fact]
    public void Live_clock_advances_and_a_disconnected_clock_stays_at_the_recorded_end()
    {
        var live = Row("running", Start, updated: Start);
        Assert.Equal("2m 49s", SubagentChatPanels.Elapsed(live, Start.AddMinutes(2).AddSeconds(49)));
        Assert.Equal("4m 1s", SubagentChatPanels.Elapsed(live, Start.AddMinutes(4).AddSeconds(1)));
        Assert.Equal("1h 2m", SubagentChatPanels.Elapsed(live, Start.AddHours(1).AddMinutes(2)));

        var stale = Row("disconnected", Start, updated: Start.AddMinutes(2).AddSeconds(49));
        var frozen = SubagentChatPanels.FreezeClock(stale);
        Assert.Equal(Start.AddMinutes(2).AddSeconds(49), frozen.CompletedAt);
        Assert.Equal("2m 49s", SubagentChatPanels.Elapsed(frozen, Start.AddMinutes(2).AddSeconds(49)));
        Assert.Equal("2m 49s", SubagentChatPanels.Elapsed(frozen, Start.AddHours(9)));

        var later = stale with { UpdatedAt = Start.AddMinutes(30) };
        var kept = SubagentChatPanels.FreezeClock(later, frozen);
        Assert.Equal(frozen.CompletedAt, kept.CompletedAt);
        Assert.Equal("2m 49s", SubagentChatPanels.Elapsed(kept, Start.AddHours(9)));
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("queued")]
    [InlineData("waiting")]
    public void Other_live_states_keep_refreshing(string state)
    {
        var live = Row(state, Start, updated: Start);
        Assert.Equal("10s", SubagentChatPanels.Elapsed(live, Start.AddSeconds(10)));
        Assert.Equal("11s", SubagentChatPanels.Elapsed(live, Start.AddSeconds(11)));
        Assert.Null(SubagentChatPanels.FreezeClock(live).CompletedAt);
    }

    [Fact]
    public void Confirmed_end_blocked_row_and_missing_timestamps_do_not_invent_a_moving_clock()
    {
        var confirmed = Row("completed", Start, updated: Start.AddMinutes(30), completed: Start.AddMinutes(1));
        Assert.Equal(Start.AddMinutes(1), SubagentChatPanels.FreezeClock(confirmed).CompletedAt);
        Assert.Equal("1m 0s", SubagentChatPanels.Elapsed(confirmed, Start.AddHours(4)));

        var blocked = Row("blocked", Start, updated: Start.AddMinutes(2).AddSeconds(49));
        Assert.Equal("2m 49s", SubagentChatPanels.Elapsed(blocked, Start.AddHours(5)));
        Assert.Null(SubagentChatPanels.FreezeClock(blocked).CompletedAt);

        var missing = new CursorSubagent("none", "None", "task", "disconnected");
        Assert.Equal("Not recorded", SubagentChatPanels.Elapsed(SubagentChatPanels.FreezeClock(missing), Start.AddHours(1)));

        var backwards = Row("completed", Start.AddSeconds(5), completed: Start);
        Assert.Equal("0s", SubagentChatPanels.Elapsed(backwards, Start.AddHours(1)));
    }

    private static CursorSubagent Row(string state, DateTimeOffset started, DateTimeOffset? updated = null, DateTimeOffset? completed = null) =>
        new("session", "Helper", "task", state, StartedAt: started, UpdatedAt: updated, CompletedAt: completed);
}
