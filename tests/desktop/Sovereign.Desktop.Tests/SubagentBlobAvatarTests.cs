using Sovereign.Desktop.Client;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class SubagentBlobAvatarTests
{
    [Fact]
    public void Same_id_always_picks_the_same_blob_and_ignores_a_renamed_label()
    {
        var first = SubagentBlobAvatar.Pick("qa-helper", "Art QA");
        var again = SubagentBlobAvatar.Pick("qa-helper", "Renamed helper");
        Assert.Equal(first, again);
        Assert.InRange(first.ColorIndex, 0, SubagentBlobAvatar.ColorCount - 1);
        Assert.InRange(first.ShapeIndex, 0, SubagentBlobAvatar.ShapeCount - 1);
        Assert.Equal("blob-" + first.ColorIndex + "-" + first.ShapeIndex, first.Key);
    }

    [Fact]
    public void Missing_id_uses_the_name_and_a_set_of_helpers_is_not_one_color()
    {
        Assert.Equal(SubagentBlobAvatar.Pick(null, "Ada"), SubagentBlobAvatar.Pick("  ", "Ada"));
        Assert.Equal(SubagentBlobAvatar.Pick("helper-7", "Same Name"), SubagentBlobAvatar.Pick("helper-7", "Other Name"));
        var picks = Enumerable.Range(0, 24).Select(i => SubagentBlobAvatar.Pick("helper-" + i, "Helper " + i)).ToArray();
        Assert.True(picks.Select(p => p.ColorIndex).Distinct().Count() > 1);
        Assert.True(picks.Select(p => p.ShapeIndex).Distinct().Count() > 1);
    }
}
