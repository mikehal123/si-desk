using Sovereign.Desktop.Client;
using Xunit;

namespace XaiDesk.Tests;

public sealed class GrokTuiTests
{
    [Fact]
    public void User_bar_keeps_text_and_formats_the_offset_clock()
    {
        var at = new DateTimeOffset(2026, 10, 10, 9, 5, 45, TimeSpan.FromHours(5));
        var bar = GrokTuiPresentation.UserBar("Ship the panel", at);
        Assert.Equal("Ship the panel", bar.Text);
        Assert.Equal("09:05", bar.Time);
        Assert.Equal(new GrokTuiUserBar("Ship the panel", "09:05"), bar);
    }

    [Fact]
    public void Markdown_parses_heading_bold_bullet_code_fence_and_path()
    {
        var source = "# Title\n## Section\n### Sub\n\nSay **bold** and `code`.\n\n- item `tick`\n\n```\nalpha\nbeta\n```\n\nSee F:\\xai-desk\\source and /src/app\n";
        var blocks = GrokTuiPresentation.Markdown(source);

        Assert.Equal(7, blocks.Count);
        Assert.Equal(GrokTuiStyle.Heading, blocks[0].Style);
        Assert.Equal([new GrokTuiRun("Title", GrokTuiStyle.Heading)], Runs(blocks[0]));
        Assert.Equal([new GrokTuiRun("Section", GrokTuiStyle.Heading)], Runs(blocks[1]));
        Assert.Equal([new GrokTuiRun("Sub", GrokTuiStyle.Heading)], Runs(blocks[2]));

        Assert.Equal(GrokTuiStyle.Plain, blocks[3].Style);
        Assert.Equal(
        [
            new GrokTuiRun("Say ", GrokTuiStyle.Plain),
            new GrokTuiRun("bold", GrokTuiStyle.Bold),
            new GrokTuiRun(" and ", GrokTuiStyle.Plain),
            new GrokTuiRun("code", GrokTuiStyle.InlineCode),
            new GrokTuiRun(".", GrokTuiStyle.Plain)
        ], Runs(blocks[3]));

        Assert.Equal(GrokTuiStyle.Bullet, blocks[4].Style);
        Assert.Equal(
        [
            new GrokTuiRun("item ", GrokTuiStyle.Plain),
            new GrokTuiRun("tick", GrokTuiStyle.InlineCode)
        ], Runs(blocks[4]));

        Assert.Equal(GrokTuiStyle.CodeBlock, blocks[5].Style);
        Assert.Equal([new GrokTuiRun("alpha\nbeta", GrokTuiStyle.CodeBlock)], Runs(blocks[5]));

        Assert.Equal(GrokTuiStyle.Plain, blocks[6].Style);
        Assert.Equal(
        [
            new GrokTuiRun("See ", GrokTuiStyle.Plain),
            new GrokTuiRun(@"F:\xai-desk\source", GrokTuiStyle.InlineCode),
            new GrokTuiRun(" and ", GrokTuiStyle.Plain),
            new GrokTuiRun("/src/app", GrokTuiStyle.InlineCode)
        ], Runs(blocks[6]));
    }

    [Fact]
    public void Tool_lines_mark_ran_and_search()
    {
        Assert.Equal("✓ Ran build", GrokTuiPresentation.ToolLine("build", "completed", "execute"));
        Assert.Equal("◇ Searched 1 pattern", GrokTuiPresentation.ToolLine("1 pattern", "in_progress", "search"));
        Assert.Equal("✗ Ran build", GrokTuiPresentation.ToolLine("build", "failed", "shell"));
        Assert.Equal("✓ Searched 2 patterns", GrokTuiPresentation.ToolLine("2 patterns", "done", "search"));
        Assert.Equal("✓ Edited notes.md", GrokTuiPresentation.ToolLine("notes.md", "completed", "edit"));
        Assert.Equal("✓ Read a.cs", GrokTuiPresentation.ToolLine("a.cs", "completed", "READ"));
    }

    [Fact]
    public void Worked_for_rounds_to_whole_seconds()
    {
        Assert.Equal("Worked for 14s", GrokTuiPresentation.WorkedFor(TimeSpan.FromSeconds(14)));
    }

    [Fact]
    public void Status_line_shows_spinner_elapsed_tokens_and_stop()
    {
        var status = GrokTuiPresentation.Status(TimeSpan.FromMilliseconds(1600), 1200, 0);
        Assert.Equal("⠋ Main agent waiting… 1.6s", status.Left);
        Assert.Equal("1,200 tok", status.Right);
        Assert.Equal("[stop]", status.Stop);
        Assert.Equal(status.Left, GrokTuiPresentation.Status(TimeSpan.FromMilliseconds(1600), 1200, 10).Left);
        Assert.Equal("—", GrokTuiPresentation.Status(TimeSpan.Zero, null, 1).Right);
    }

    [Fact]
    public void Context_approval_and_footer_format_the_chrome()
    {
        Assert.Equal("60K / 256K", GrokTuiPresentation.ContextLabel(60000, 256000));
        Assert.Equal("— / 256K", GrokTuiPresentation.ContextLabel(null, 256000));
        Assert.Equal(128000, GrokTuiPresentation.ContextLimit("grok context=128k"));
        Assert.Equal(256000, GrokTuiPresentation.ContextLimit("grok-4.7"));
        Assert.Equal("plan", GrokTuiPresentation.Approval(true, true));
        Assert.Equal("always approve", GrokTuiPresentation.Approval(false, true));
        Assert.Equal("always ask", GrokTuiPresentation.Approval(false, false));
        Assert.Equal("Grok 4.7 · always approve", GrokTuiPresentation.Footer("  Grok 4.7  ", "always approve"));
        Assert.Equal("Grok · always ask", GrokTuiPresentation.Footer("  ", "always ask"));
        Assert.Equal("yo", GrokTuiPresentation.PendingPreview("  yo \r\n"));
        Assert.Equal("Attachment", GrokTuiPresentation.PendingPreview(" \n "));
        Assert.Equal(72, GrokTuiPresentation.PendingPreview(new string('a', 80)).Length);
        Assert.Equal("your message · interrupted · not resent", GrokTuiPresentation.PendingStatus(
            "Your message · Interrupted · may already have been sent. Press Resume main agent to send it again. It is not resent automatically."));
        Assert.Equal("your message · not sent", GrokTuiPresentation.PendingStatus("Your message · Not sent · press Resume main agent. Nothing is sent automatically."));
    }

    [Fact]
    public void Keyboard_maps_enter_escape_up_and_ctrl_l()
    {
        Assert.Equal(GrokTuiKeyAction.Send, GrokTuiPresentation.Key("Enter", false, false));
        Assert.Equal(GrokTuiKeyAction.NewLine, GrokTuiPresentation.Key("Enter", true, false));
        Assert.Equal(GrokTuiKeyAction.Stop, GrokTuiPresentation.Key("Escape", false, false));
        Assert.Equal(GrokTuiKeyAction.Recall, GrokTuiPresentation.Key("Up", false, false));
        Assert.Equal(GrokTuiKeyAction.ClearView, GrokTuiPresentation.Key("L", false, true));
        Assert.Equal(GrokTuiKeyAction.ClearView, GrokTuiPresentation.Key("l", false, true));
        Assert.Equal(GrokTuiKeyAction.None, GrokTuiPresentation.Key("K", false, false));
    }

    [Fact]
    public void Classic_chat_defaults_off_and_roundtrips()
    {
        var profile = Path.Combine(Path.GetTempPath(), "xai-desk-classic-chat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        try
        {
            Assert.False(ClassicChatPreference.Load(profile));
            ClassicChatPreference.Save(profile, true);
            Assert.True(ClassicChatPreference.Load(profile));
            Assert.Equal("{\"enabled\":true}", File.ReadAllText(Path.Combine(profile, "classic-chat.json")));

            File.WriteAllText(Path.Combine(profile, "classic-chat.json"), "{\"Enabled\":false}");
            Assert.False(ClassicChatPreference.Load(profile));
            File.WriteAllText(Path.Combine(profile, "classic-chat.json"), "{");
            Assert.False(ClassicChatPreference.Load(profile));
        }
        finally
        {
            if (Directory.Exists(profile)) Directory.Delete(profile, true);
        }
    }

    [Fact]
    public void SpaceXai_icons_are_in_place()
    {
        var root = FindRepoRoot();
        var logo = Path.Combine(root, "assets", "logo.png");
        if (!File.Exists(logo)) return;
        var ico = Path.Combine(root, "source", "apps", "desktop", "Sovereign.Desktop", "Assets", "xAIDesk.ico");
        var icon192 = Path.Combine(root, "source", "apps", "desktop", "phone", "www", "icon-192.png");
        var icon512 = Path.Combine(root, "source", "apps", "desktop", "phone", "www", "icon-512.png");
        var asset = Path.Combine(root, "source", "apps", "desktop", "Sovereign.Desktop", "Assets", "logo.png");
        Assert.True(File.Exists(logo));
        Assert.True(File.Exists(asset));
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, File.ReadAllBytes(logo).AsSpan(0, 4).ToArray());
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, File.ReadAllBytes(icon192).AsSpan(0, 4).ToArray());
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, File.ReadAllBytes(icon512).AsSpan(0, 4).ToArray());
        Assert.Equal(new byte[] { 0x00, 0x00, 0x01, 0x00 }, File.ReadAllBytes(ico).AsSpan(0, 4).ToArray());
        Assert.Equal((192, 192), PngSize(icon192));
        Assert.Equal((512, 512), PngSize(icon512));
    }

    private static (int Width, int Height) PngSize(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
        var height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
        return (width, height);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))
                && Directory.Exists(Path.Combine(dir.FullName, "apps", "desktop")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("No Directory.Build.props above the test output contains apps\\desktop.");
    }

    private static GrokTuiRun[] Runs(GrokTuiBlock block) => block.Runs.ToArray();
}
