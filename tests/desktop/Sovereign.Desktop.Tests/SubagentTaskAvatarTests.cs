using Sovereign.Desktop.Client;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class SubagentTaskAvatarTests
{
    [Theory]
    [InlineData("Fix the column alignment", SubagentTaskKind.Table, "table.svg")]
    [InlineData("Repair ledger formulas", SubagentTaskKind.Table, "table.svg")]
    [InlineData("Inspect report.PDF pages", SubagentTaskKind.Pdf, "pdf.svg")]
    [InlineData("Fix PDF table columns", SubagentTaskKind.Pdf, "pdf.svg")]
    [InlineData("Clean up unused files", SubagentTaskKind.Cleanup, "cleanup.svg")]
    [InlineData("Strip report run metadata", SubagentTaskKind.Cleanup, "cleanup.svg")]
    [InlineData("Research the issuer documentation", SubagentTaskKind.Research, "research.svg")]
    [InlineData("Implement an API bug fix", SubagentTaskKind.Code, "code.svg")]
    [InlineData("Plan a weekend trip", SubagentTaskKind.Unspecified, "agent.svg")]
    [InlineData("Profile the scenery", SubagentTaskKind.Unspecified, "agent.svg")]
    [InlineData("", SubagentTaskKind.Unspecified, "agent.svg")]
    public void Task_picture_uses_specific_task_words_with_an_agent_fallback(string task, SubagentTaskKind kind, string? asset)
    {
        var result = SubagentTaskPresentation.ForTask(task);
        Assert.Equal(kind, result.Kind);
        Assert.Equal(asset, result.AssetFileName);
    }

    [Fact]
    public void Transient_tool_text_and_agent_name_do_not_change_the_delegated_task_picture()
    {
        var child = new CursorSubagent("one", "PDF researcher", "Fix the compiler", "running", CurrentWork: "Reading report.pdf");
        Assert.Equal(SubagentTaskKind.Code, SubagentTaskPresentation.ForTask(child).Kind);
        Assert.Equal(SubagentTaskPresentation.ForTask(child), SubagentTaskPresentation.ForTask(child with { CurrentWork = "Searching tables" }));
        Assert.Equal("agent.svg", SubagentTaskPresentation.ForTask(child with { Task = "Plan a weekend trip" }).AssetFileName);
    }

    [Fact]
    public void Recorded_task_title_does_not_supply_a_picture_when_the_delegated_task_is_missing()
    {
        var child = new CursorSubagent("one", "upgrade_research", "", "running", CurrentWork: "Reading report.pdf");
        Assert.Equal("agent.svg", SubagentTaskPresentation.ForTask(child).AssetFileName);
        Assert.Equal(SubagentTaskKind.Table, SubagentTaskPresentation.ForTask(child with { Task = "Fix CSV columns" }).Kind);
        Assert.Equal("agent.svg", SubagentTaskPresentation.ForTask(child with { Name = "investigator_retry", Task = "" }).AssetFileName);
        Assert.Equal("helper.svg", SubagentTaskPresentation.AvatarAfterLoadFailure("research.svg"));
        Assert.Null(SubagentTaskPresentation.AvatarAfterLoadFailure("helper.svg"));
    }

    [Fact]
    public void Explicit_role_marker_selects_the_role_icon_and_a_code_task_beats_cleanup_words()
    {
        var marked = new CursorSubagent("one", "investigator_retry", "SOVEREIGN_TEAM role=investigator\nRead the saved note", "running");
        Assert.Equal("agent.svg", SubagentTaskPresentation.ForTask(marked).AssetFileName);
        Assert.Equal(SubagentTaskKind.Code, SubagentTaskPresentation.ForTask("remove the compiler bug").Kind);
    }
}
