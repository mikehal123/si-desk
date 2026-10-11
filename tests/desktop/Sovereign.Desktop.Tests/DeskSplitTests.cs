using Sovereign.Desktop.Client;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class DeskSplitTests
{
    [Fact]
    public void Drag_respects_the_chat_minimum_and_the_panel_maximum()
    {
        Assert.Equal(400, DeskSplit.DragRight(360, -40, 1600));
        Assert.Equal(DeskSplit.RightMin, DeskSplit.DragRight(260, 80, 1600));
        Assert.Equal(960, DeskSplit.DragRight(900, -200, 1600));
        Assert.Equal(564, DeskSplit.DragRight(700, -100, 1000));
    }

    [Fact]
    public void Arrow_nudges_sixteen_pixels_and_stops_at_the_limits()
    {
        Assert.Equal(376, DeskSplit.NudgeRight(360, -1, 1600));
        Assert.Equal(344, DeskSplit.NudgeRight(360, 1, 1600));
        Assert.Equal(DeskSplit.RightMin, DeskSplit.NudgeRight(270, 1, 1600));
        Assert.Equal(960, DeskSplit.NudgeRight(960, -1, 1600));
    }

    [Fact]
    public void Double_click_reset_returns_the_default_width()
    {
        var reset = DeskSplit.ClampRight(DeskSplit.RightDefault, 1600);
        Assert.Equal(DeskSplit.RightDefault, reset);
        Assert.Equal(DeskSplit.SidebarDefault, DeskSplit.ClampSidebar(DeskSplit.SidebarDefault, 1600));
    }

    [Fact]
    public void Sidebar_drag_stays_inside_its_limits()
    {
        Assert.Equal(256, DeskSplit.DragSidebar(240, 16, 1600));
        Assert.Equal(DeskSplit.SidebarMin, DeskSplit.DragSidebar(180, -40, 1600));
        Assert.Equal(DeskSplit.SidebarMax, DeskSplit.DragSidebar(470, 40, 1600));
        Assert.Equal(256, DeskSplit.NudgeSidebar(240, 1, 1600));
    }

    [Fact]
    public void Widths_persist_across_a_restart_and_a_closed_panel_does_not_erase_them()
    {
        var profile = Path.Combine(Path.GetTempPath(), "xai-desk-layout-" + Guid.NewGuid().ToString("N"));
        DeskLayoutStore.SaveRight(profile, 480);
        DeskLayoutStore.SaveSidebar(profile, 300);
        var loaded = DeskLayoutStore.Load(profile);
        Assert.Equal(480, loaded.RightPanel);
        Assert.Equal(300, loaded.Sidebar);
        var again = DeskLayoutStore.Load(profile);
        Assert.Equal(480, again.RightPanel);
        Assert.Equal(DeskLayoutWidths.Default, DeskLayoutStore.Load(Path.Combine(profile, "missing")));
        File.WriteAllText(Path.Combine(profile, "layout.json"), "{not json");
        Assert.Equal(DeskLayoutWidths.Default, DeskLayoutStore.Load(profile));
        Directory.Delete(profile, true);
    }
}
