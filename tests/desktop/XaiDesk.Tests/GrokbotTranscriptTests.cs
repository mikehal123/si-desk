using Sovereign.Desktop.Connector;
using Xunit;

namespace XaiDesk.Tests;

public sealed class GrokbotTranscriptTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 14, 16, 5, 6, TimeSpan.Zero);
    private static readonly DateTimeOffset Answered = new(2026, 10, 14, 16, 6, 7, TimeSpan.Zero);

    [Fact]
    public void Timestamp_is_culture_invariant_iso_8601_with_offset()
    {
        var at = new DateTimeOffset(2026, 10, 14, 9, 5, 6, TimeSpan.FromHours(-7));
        Assert.Equal("2026-10-14T09:05:06.0000000-07:00", GrokbotTranscript.Timestamp(at));
    }

    [Fact]
    public void Format_labels_user_and_grokbot_and_uses_answered_at_or_created()
    {
        var mail = new GrokbotMail("1", "Ship the panel", false, GrokbotMailStatus.Answered, Created, "On it", Answered);
        var missingAnsweredAt = new GrokbotMail("2", "ping", false, GrokbotMailStatus.Answered, Created, "pong", null);
        var userStamp = GrokbotTranscript.Timestamp(Created.ToLocalTime());
        var botStamp = GrokbotTranscript.Timestamp(Answered.ToLocalTime());
        Assert.Equal("You: Ship the panel\n" + userStamp + "\nGrokbot: On it\n" + botStamp, GrokbotTranscript.Format(new[] { mail }));
        Assert.Equal("You: ping\n" + userStamp + "\nGrokbot: pong\n" + userStamp, GrokbotTranscript.Format(new[] { missingAnsweredAt }));
        if (Created.ToLocalTime().Offset != Created.Offset)
            Assert.DoesNotContain(GrokbotTranscript.Timestamp(Created), GrokbotTranscript.Format(new[] { mail }), StringComparison.Ordinal);
    }

    [Fact]
    public void Format_keeps_code_fences_and_skips_empty_replies()
    {
        var user = "Look\n```csharp\nvar x = 1;\n```";
        var reply = "Done\n```text\nkeep me\n```";
        var mail = new GrokbotMail("1", user, false, GrokbotMailStatus.Answered, Created, reply, Answered);
        var blank = new GrokbotMail("2", "ping", false, GrokbotMailStatus.PickedUp, Created, " \n ", null);
        var waiting = new GrokbotMail("3", "next", false, GrokbotMailStatus.Queued, Created.AddMinutes(1), null, null);
        var text = GrokbotTranscript.Format(new[] { mail });
        var userStamp = GrokbotTranscript.Timestamp(Created.ToLocalTime());
        var botStamp = GrokbotTranscript.Timestamp(Answered.ToLocalTime());
        Assert.Equal("You: " + user + "\n" + userStamp + "\nGrokbot: " + reply + "\n" + botStamp, text);
        Assert.Contains("```csharp\nvar x = 1;\n```", text, StringComparison.Ordinal);
        Assert.Contains("```text\nkeep me\n```", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Grok Bot", text, StringComparison.Ordinal);

        var skipped = GrokbotTranscript.Format(new[] { blank, waiting });
        var nextStamp = GrokbotTranscript.Timestamp(Created.AddMinutes(1).ToLocalTime());
        Assert.Equal("You: ping\n" + userStamp + "\nYou: next\n" + nextStamp, skipped);
        Assert.DoesNotContain("Grokbot:", skipped, StringComparison.Ordinal);
        Assert.DoesNotContain("thinking", skipped, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("…", skipped, StringComparison.Ordinal);
        Assert.Equal(1, GrokbotTranscript.EntryCount(new[] { blank }));
        Assert.Equal(2, GrokbotTranscript.EntryCount(new[] { mail }));
        Assert.Equal(0, GrokbotTranscript.EntryCount(Array.Empty<GrokbotMail>()));
        Assert.Equal("", GrokbotTranscript.Format(Array.Empty<GrokbotMail>()));
    }

    [Fact]
    public void Jump_hides_at_bottom_and_shows_after_more_than_one_viewport()
    {
        var bottom = GrokbotTranscript.OnScroll(GrokbotTranscript.Jump.Following, 1000, 400, 600);
        Assert.True(bottom.StickToEnd);
        Assert.False(GrokbotTranscript.ButtonVisible(bottom, 1000, 400));

        var edge = GrokbotTranscript.OnScroll(GrokbotTranscript.Jump.Following, 1000, 400, 200);
        Assert.False(edge.StickToEnd);
        Assert.Equal(200, edge.Offset);
        Assert.False(GrokbotTranscript.ButtonVisible(edge, 1000, 400));
        Assert.False(GrokbotTranscript.ShowJump(400, 400));

        var past = GrokbotTranscript.OnScroll(GrokbotTranscript.Jump.Following, 1000, 400, 199);
        Assert.True(GrokbotTranscript.ButtonVisible(past, 1000, 400));
        Assert.True(GrokbotTranscript.ShowJump(401, 400));
        Assert.False(GrokbotTranscript.ShowJump(400, 0));
    }

    [Fact]
    public void Unread_grows_only_while_scrolled_up_and_clears_on_jump_or_bottom()
    {
        var opened = GrokbotTranscript.OnArrivals(GrokbotTranscript.Jump.Following, 5, 3000, 400);
        Assert.Equal(0, opened.Unread);
        Assert.Equal(2600, opened.Offset);
        Assert.True(opened.StickToEnd);

        var up = GrokbotTranscript.OnScroll(opened, 3000, 400, 50);
        Assert.Equal(50, up.Offset);
        Assert.True(GrokbotTranscript.ButtonVisible(up, 3000, 400));
        var grown = GrokbotTranscript.OnArrivals(up, 2, 3400, 400);
        Assert.Equal(2, grown.Unread);
        Assert.Equal(50, grown.Offset);
        Assert.False(grown.StickToEnd);

        var near = GrokbotTranscript.OnScroll(GrokbotTranscript.Jump.Following, 1000, 400, 500);
        Assert.False(GrokbotTranscript.ButtonVisible(near, 1000, 400));
        var nearArrival = GrokbotTranscript.OnArrivals(near, 1, 1100, 400);
        Assert.Equal(1, nearArrival.Unread);
        Assert.Equal(500, nearArrival.Offset);

        var followed = GrokbotTranscript.OnArrivals(opened, 1, 3600, 400);
        Assert.Equal(0, followed.Unread);
        Assert.Equal(3200, followed.Offset);

        var jumped = GrokbotTranscript.OnJump(grown, 3400, 400);
        Assert.Equal(0, jumped.Unread);
        Assert.True(jumped.StickToEnd);
        Assert.Equal(3000, jumped.Offset);
        Assert.False(GrokbotTranscript.ButtonVisible(jumped, 3400, 400));

        var back = GrokbotTranscript.OnScroll(grown, 3400, 400, GrokbotTranscript.LatestOffset(3400, 400));
        Assert.Equal(0, back.Unread);
        Assert.True(back.StickToEnd);
        Assert.False(GrokbotTranscript.ButtonVisible(back, 3400, 400));

        var unread = GrokbotTranscript.OnArrivals(up, 3, 1000, 400);
        var withinSlack = GrokbotTranscript.OnScroll(unread, 1000, 400, 1000 - 400 - GrokbotTranscript.BottomSlack);
        Assert.True(withinSlack.StickToEnd);
        Assert.Equal(0, withinSlack.Unread);
        var outsideSlack = GrokbotTranscript.OnScroll(unread, 1000, 400, 1000 - 400 - (GrokbotTranscript.BottomSlack + 0.1));
        Assert.False(outsideSlack.StickToEnd);
        Assert.Equal(3, outsideSlack.Unread);

        var pending = GrokbotTranscript.OnArrivals(up, 1, 0, 0);
        Assert.Equal(50, pending.Offset);
        Assert.Equal(1, pending.Unread);
        Assert.Equal(2, GrokbotTranscript.OnArrivals(grown, -4, 3400, 400).Unread);
    }
}
