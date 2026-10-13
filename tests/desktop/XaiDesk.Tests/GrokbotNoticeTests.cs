using Sovereign.Desktop.Connector;
using Xunit;

namespace XaiDesk.Tests;

public sealed class GrokbotNoticeTests
{
    [Fact]
    public void FirstLine_returns_the_trimmed_segment_before_the_first_break()
    {
        Assert.Equal("", GrokbotNotice.FirstLine(null));
        Assert.Equal("", GrokbotNotice.FirstLine(""));
        Assert.Equal("", GrokbotNotice.FirstLine("   "));
        Assert.Equal("hello", GrokbotNotice.FirstLine("hello"));
        Assert.Equal("hello", GrokbotNotice.FirstLine("  hello  "));
        Assert.Equal("hello", GrokbotNotice.FirstLine("hello\nworld"));
        Assert.Equal("hello", GrokbotNotice.FirstLine("hello\r\nworld"));
        Assert.Equal("", GrokbotNotice.FirstLine("\nhello"));
    }

    [Fact]
    public void ShouldToast_is_false_only_when_visible_and_active()
    {
        Assert.False(GrokbotNotice.ShouldToast(true, true));
        Assert.True(GrokbotNotice.ShouldToast(true, false));
        Assert.True(GrokbotNotice.ShouldToast(false, true));
        Assert.True(GrokbotNotice.ShouldToast(false, false));
    }

    [Fact]
    public void ShouldMarkUnread_is_false_only_when_visible_and_active()
    {
        Assert.False(GrokbotNotice.ShouldMarkUnread(true, true));
        Assert.True(GrokbotNotice.ShouldMarkUnread(true, false));
        Assert.True(GrokbotNotice.ShouldMarkUnread(false, true));
        Assert.True(GrokbotNotice.ShouldMarkUnread(false, false));
    }

    [Fact]
    public void UnreadCount_is_zero_only_when_visible_and_active_and_clamps_negative()
    {
        Assert.Equal(0, GrokbotNotice.UnreadCount(4, true, true));
        Assert.Equal(4, GrokbotNotice.UnreadCount(4, true, false));
        Assert.Equal(4, GrokbotNotice.UnreadCount(4, false, true));
        Assert.Equal(4, GrokbotNotice.UnreadCount(4, false, false));
        Assert.Equal(0, GrokbotNotice.UnreadCount(-2, false, false));
    }

    [Fact]
    public void Fake_toaster_records_show_calls()
    {
        var toaster = new RecordingToaster();
        var line = GrokbotNotice.FirstLine("hello\nworld");
        if (GrokbotNotice.ShouldToast(false, true)) toaster.Show(line);
        if (GrokbotNotice.ShouldToast(true, true)) toaster.Show("hidden");
        Assert.Equal("hello", Assert.Single(toaster.Lines));
    }

    private sealed class RecordingToaster : IGrokbotToaster
    {
        public List<string> Lines { get; } = [];

        public void Show(string line) => Lines.Add(line);
    }
}
