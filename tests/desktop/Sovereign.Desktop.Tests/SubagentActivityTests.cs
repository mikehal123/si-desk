using Sovereign.Desktop.Client;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class SubagentActivityTests
{
    [Fact]
    public void Helper_edits_are_attributed_and_do_not_replace_parent_tool_or_reasoning()
    {
        var state = new CursorTurnActivity();
        state.Apply(new("tool", Tool: new("edit", "Parent edit", "edit", "completed", "{}", [])), TimeSpan.Zero);
        var helperTool = new CursorToolActivity("child:edit", "Edit helper file", "edit", "completed", "{}",
            [new("helper.txt", "before", "after")]);
        var child = new CursorSubagent("child", "Backend", "Fix the helper", "completed", "Private result", Tools: [helperTool]);
        state.Apply(new("subagent", Tool: helperTool, Subagent: child), TimeSpan.FromSeconds(1));
        Assert.Equal(2, state.Tools.Count);
        Assert.Contains(state.Tools, t => t.Id == "child:edit" && t.Title == "Backend · Edit helper file");
        Assert.Equal("after", Assert.Single(state.Changes).After);
        Assert.Empty(state.Reasoning);
    }

    [Fact]
    public void Repeated_helper_snapshots_do_not_revert_a_later_parent_edit()
    {
        var state = new CursorTurnActivity();
        var helperTool = new CursorToolActivity("child:edit", "Edit shared file", "edit", "completed", "{}",
            [new("shared.txt", "original", "helper")]);
        var child = new CursorSubagent("child", "Backend", "Fix shared file", "completed", Tools: [helperTool]);
        state.Apply(new("subagent", Subagent: child), TimeSpan.Zero);
        state.Apply(new("tool", Tool: new("parent-edit", "Final edit", "edit", "completed", "{}",
            [new("shared.txt", "helper", "parent-final")])), TimeSpan.FromSeconds(1));
        state.Apply(new("subagent", Subagent: child with { Output = "Helper done" }), TimeSpan.FromSeconds(2));
        var change = Assert.Single(state.Changes);
        Assert.Equal("original", change.Before);
        Assert.Equal("parent-final", change.After);
    }

    [Fact]
    public void Clipped_historical_tool_preview_cannot_revert_later_parent_changes()
    {
        var state = new CursorTurnActivity();
        var helperTool = new CursorToolActivity("child:edit", "Edit shared file", "edit", "completed", new string('a', 3000),
            [new("shared.txt", "original", "helper")], new string('b', 8000));
        var child = new CursorSubagent("child", "Backend", "Fix shared file", "running");
        state.Apply(new("subagent", Tool: helperTool, Subagent: child), TimeSpan.Zero);
        state.Apply(new("tool", Tool: new("parent-edit", "Final edit", "edit", "completed", "{}",
            [new("shared.txt", "helper", "parent-final")])), TimeSpan.FromSeconds(1));
        var preview = helperTool with { Input = helperTool.Input[..2000], Output = helperTool.Output[..6000] };
        state.Apply(new("subagent", Subagent: child with { State = "completed", Tools = [preview] }), TimeSpan.FromSeconds(2));
        Assert.Equal("parent-final", Assert.Single(state.Changes).After);
    }

    [Fact]
    public void Restored_helper_snapshot_does_not_reapply_an_archived_tool()
    {
        var state = new CursorTurnActivity();
        var helperTool = new CursorToolActivity("child:edit", "Edit shared file", "edit", "completed", "{}",
            [new("shared.txt", "original", "helper")]);
        state.Apply(new("tool", Tool: helperTool with { Title = "Backend · Edit shared file" }), TimeSpan.Zero);
        state.Apply(new("tool", Tool: new("parent-edit", "Final edit", "edit", "completed", "{}",
            [new("shared.txt", "helper", "parent-final")])), TimeSpan.FromSeconds(1));
        // Deserialization creates new array instances for the final helper snapshot.
        var restored = helperTool with { Changes = [new("shared.txt", "original", "helper")] };
        state.Apply(new("subagent", Subagent: new("child", "Backend", "Fix shared file", "completed", Tools: [restored])), TimeSpan.FromSeconds(2));
        Assert.Equal("parent-final", Assert.Single(state.Changes).After);
    }
}
