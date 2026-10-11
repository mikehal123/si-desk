using Sovereign.Desktop.Client;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class SubagentCardFoldTests
{
    [Theory]
    [InlineData("running")]
    [InlineData("pending")]
    [InlineData("queued")]
    [InlineData("waiting")]
    public void Working_states_start_expanded(string state)
    {
        Assert.True(SubagentChatPanels.StartsExpanded(state));
        Assert.True(new SubagentCardFold().Expanded("card", state));
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("stopped")]
    [InlineData("disconnected")]
    [InlineData("interrupted")]
    [InlineData("blocked")]
    [InlineData("")]
    [InlineData(null)]
    public void Finished_blank_and_null_states_start_collapsed(string? state)
    {
        Assert.False(SubagentChatPanels.StartsExpanded(state));
        Assert.False(new SubagentCardFold().Expanded("card", state));
    }

    [Fact]
    public void First_expanded_result_is_remembered_when_a_later_call_passes_the_opposite_state()
    {
        var open = new SubagentCardFold();
        Assert.True(open.Expanded("card", "running"));
        Assert.True(open.Expanded("card", "completed"));
        Assert.True(open.Expanded("card", null));

        var closed = new SubagentCardFold();
        Assert.False(closed.Expanded("card", "completed"));
        Assert.False(closed.Expanded("card", "running"));
        Assert.False(closed.Expanded("card", "waiting"));
    }

    [Fact]
    public void Remember_overrides_the_start_state()
    {
        var fold = new SubagentCardFold();
        Assert.True(fold.Expanded("card", "running"));
        fold.Remember("card", false);
        Assert.False(fold.Expanded("card", "running"));
        Assert.False(fold.Expanded("card", "pending"));

        var preset = new SubagentCardFold();
        preset.Remember("card", true);
        Assert.True(preset.Expanded("card", "completed"));
        Assert.True(preset.Expanded("card", null));
    }

    [Fact]
    public void Toggle_flips_and_sticks()
    {
        var fold = new SubagentCardFold();
        Assert.True(fold.Toggle("card", "completed"));
        Assert.True(fold.Expanded("card", "failed"));
        Assert.False(fold.Toggle("card", "running"));
        Assert.False(fold.Expanded("card", "waiting"));
        Assert.True(fold.Toggle("card", null));
        Assert.True(fold.Expanded("card", "stopped"));
    }

    [Fact]
    public void Nested_ids_stay_independent()
    {
        var fold = new SubagentCardFold();
        Assert.True(fold.Expanded("parent", "running"));
        Assert.False(fold.Expanded("parent/child", "completed"));
        Assert.True(fold.Expanded("parent/child/leaf", "waiting"));

        Assert.True(fold.Toggle("parent/child", "completed"));
        fold.Remember("parent", false);

        Assert.False(fold.Expanded("parent", "running"));
        Assert.True(fold.Expanded("parent/child", "completed"));
        Assert.True(fold.Expanded("parent/child/leaf", "failed"));
    }

    [Theory]
    [InlineData("Enter", true)]
    [InlineData("enter", true)]
    [InlineData("Space", true)]
    [InlineData("space", true)]
    [InlineData("Escape", false)]
    [InlineData("Left", false)]
    [InlineData("Tab", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Toggle_keys_are_enter_and_space(string? key, bool accepted)
    {
        Assert.Equal(accepted, SubagentChatPanels.IsToggleKey(key));
    }
}
