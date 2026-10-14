using Sovereign.Desktop.Connector;
using Xunit;

namespace XaiDesk.Tests;

public sealed class HeaderBadgeTests
{
    [Fact]
    public void Subagent_badge_excludes_swarm_rows_and_closed_rows()
    {
        var count = HeaderBadges.Subagents(
        [
            new HeaderBadges.SubagentRow(false, false),
            new HeaderBadges.SubagentRow(true, false),
            new HeaderBadges.SubagentRow(false, true),
            new HeaderBadges.SubagentRow(true, true),
            new HeaderBadges.SubagentRow(false, false)
        ]);
        Assert.Equal(2, count);
    }

    [Fact]
    public void Changes_badge_sums_snapshot_lengths_and_ignores_the_reported_total()
    {
        var snapshots = new[] { 2, 0, 3 };
        const int reported = 40;
        var badge = HeaderBadges.Changes(snapshots);
        Assert.Equal(5, badge);
        Assert.NotEqual(reported, badge);
    }
}
