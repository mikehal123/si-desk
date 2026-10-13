using Sovereign.Desktop.Connector;
using Xunit;

namespace XaiDesk.Tests;

public sealed class GrokbotThinkingTests
{
    private static readonly DateTimeOffset PickedUp = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Label_is_thinking_under_five_minutes()
    {
        var created = PickedUp.AddMinutes(-10);
        var now = PickedUp.AddMinutes(5).AddTicks(-1);
        Assert.Equal("Grokbot is thinking…", GrokbotThinking.Label(GrokbotMailStatus.PickedUp, null, false, created, PickedUp, now));
        Assert.Equal("Grokbot is thinking…", GrokbotThinking.Label(GrokbotMailStatus.PickedUp, "   ", false, created, PickedUp, now));
    }

    [Fact]
    public void Label_is_still_working_at_exactly_five_minutes()
    {
        var now = PickedUp.AddMinutes(5);
        var pickedUpAt = now.AddMinutes(-5);
        Assert.Equal("Still working…", GrokbotThinking.Label(GrokbotMailStatus.PickedUp, null, false, now, pickedUpAt, now));
    }

    [Fact]
    public void Label_is_empty_when_stopped()
    {
        var created = PickedUp;
        Assert.Equal("", GrokbotThinking.Label(GrokbotMailStatus.Queued, null, true, created, null, created));
        Assert.Equal("", GrokbotThinking.Label(GrokbotMailStatus.PickedUp, null, true, created, PickedUp, PickedUp));
    }

    [Fact]
    public void Label_is_empty_when_a_reply_is_present()
    {
        var created = PickedUp;
        Assert.Equal("", GrokbotThinking.Label(GrokbotMailStatus.Queued, "done", false, created, null, created));
        Assert.Equal("", GrokbotThinking.Label(GrokbotMailStatus.PickedUp, "done", false, created, PickedUp, PickedUp));
    }

    [Fact]
    public void Label_is_thinking_when_queued_just_sent()
    {
        Assert.Equal("Grokbot is thinking…", GrokbotThinking.Label(GrokbotMailStatus.Queued, null, false, PickedUp, null, PickedUp));
        Assert.Equal("Grokbot is thinking…", GrokbotThinking.Label(GrokbotMailStatus.Queued, "   ", false, PickedUp, null, PickedUp));
    }

    [Fact]
    public void Label_is_still_working_when_queued_at_exactly_five_minutes()
    {
        var created = PickedUp;
        var now = created.AddMinutes(5);
        Assert.Equal("Still working…", GrokbotThinking.Label(GrokbotMailStatus.Queued, null, false, created, null, now));
    }

    [Fact]
    public void Label_anchors_on_created_when_picked_up_at_is_null()
    {
        var created = PickedUp;
        Assert.Equal("Grokbot is thinking…", GrokbotThinking.Label(GrokbotMailStatus.PickedUp, null, false, created, null, created.AddMinutes(5).AddTicks(-1)));
        Assert.Equal("Still working…", GrokbotThinking.Label(GrokbotMailStatus.PickedUp, null, false, created, null, created.AddMinutes(5)));
    }

    [Fact]
    public void Label_is_empty_when_answered()
    {
        Assert.Equal("", GrokbotThinking.Label(GrokbotMailStatus.Answered, null, false, PickedUp, PickedUp, PickedUp));
    }

    [Fact]
    public void PollInterval_keeps_one_second_and_caps_five_seconds_while_picked_up()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), GrokbotThinking.PollInterval(TimeSpan.FromSeconds(1), true));
        Assert.Equal(TimeSpan.FromSeconds(2), GrokbotThinking.PollInterval(TimeSpan.FromSeconds(5), true));
        Assert.Equal(TimeSpan.FromSeconds(5), GrokbotThinking.PollInterval(TimeSpan.FromSeconds(5), false));
    }

    [Fact]
    public void SetLiked_round_trips_through_a_second_open()
    {
        var folder = Path.Combine(Path.GetTempPath(), "xai-desk-grokbot-like-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = GrokbotMailbox.Open(folder);
            var mail = first.Queue("hello", false);
            Assert.False(mail.Liked);
            first.SetLiked(mail.Id, true);
            var second = GrokbotMailbox.Open(folder);
            Assert.True(Assert.Single(second.Messages).Liked);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void SetLiked_toggle_off_round_trips_through_a_second_open()
    {
        var folder = Path.Combine(Path.GetTempPath(), "xai-desk-grokbot-like-off-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = GrokbotMailbox.Open(folder);
            var mail = first.Queue("hello", false);
            first.SetLiked(mail.Id, true);
            first.SetLiked(mail.Id, false);
            var second = GrokbotMailbox.Open(folder);
            Assert.False(Assert.Single(second.Messages).Liked);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void ListSince_sets_picked_up_at_once_and_leaves_it_on_the_second_list()
    {
        var folder = Path.Combine(Path.GetTempPath(), "xai-desk-grokbot-pickup-" + Guid.NewGuid().ToString("N"));
        try
        {
            var box = GrokbotMailbox.Open(folder);
            var queued = box.Queue("hello", false);
            Assert.Null(queued.PickedUpAt);
            var first = Assert.Single(box.ListSince(null));
            Assert.Equal(GrokbotMailStatus.PickedUp, first.Status);
            Assert.NotNull(first.PickedUpAt);
            var second = Assert.Single(box.ListSince(null));
            Assert.Equal(first.PickedUpAt, second.PickedUpAt);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Missing_mail_json_keeps_picked_up_at_null_and_liked_false()
    {
        var folder = Path.Combine(Path.GetTempPath(), "xai-desk-grokbot-old-mail-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "thread.jsonl"), "{\"id\":\"m1\",\"text\":\"keep me\",\"askToAct\":false,\"status\":\"Queued\",\"created\":\"2026-10-10T18:00:00+00:00\"}\n");
            var mail = GrokbotMailbox.Open(folder);
            var row = Assert.Single(mail.Messages);
            Assert.Null(row.PickedUpAt);
            Assert.False(row.Liked);
            Assert.Null(row.Image);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void SetLiked_unknown_id_throws_key_not_found()
    {
        var box = new GrokbotMailbox(null);
        var mail = box.Queue("hello", false);
        Assert.Throws<KeyNotFoundException>(() => box.SetLiked(mail.Id + "x", true));
    }

    [Fact]
    public void Scale_matches_the_1600_longest_side_formula()
    {
        Assert.Equal(Math.Min(1d, 1600d / Math.Max(800, 3200)), GrokbotImage.Scale(800, 3200));
        Assert.Equal(1d, GrokbotImage.Scale(1600, 900));
    }

    [Fact]
    public void Wrong_type_and_too_big_do_not_queue()
    {
        var box = new GrokbotMailbox(null);
        var wrong = GrokbotImage.Reject(".gif", false, false, 8, 2, 2, 8);
        var clipboard = GrokbotImage.Reject("bitmap", true, false, 8, 2, 2, 8);
        var source = GrokbotImage.Reject(".png", false, true, GrokbotImage.MaxBytes + 1, 2, 2, 8);
        var encoded = GrokbotImage.Reject(".jpg", false, true, 8, 2, 2, GrokbotImage.MaxBytes + 1);
        Assert.Equal("wrong type", wrong);
        Assert.Equal("wrong type", clipboard);
        Assert.Equal("too big", source);
        Assert.Equal("too big", encoded);
        if (wrong is null) box.Queue("kept", false);
        if (clipboard is null) box.Queue("kept", false);
        if (source is null) box.Queue("kept", false);
        if (encoded is null) box.Queue("kept", false);
        Assert.Empty(box.Messages);
        Assert.Null(GrokbotImage.Reject(".png", false, true, GrokbotImage.MaxBytes, 2, 2, GrokbotImage.MaxBytes));
        Assert.Null(GrokbotImage.Reject(".jpeg", false, false, 8, 2, 2, 8));
        Assert.Null(GrokbotImage.Reject(".webp", false, false, 8, 2, 2, 8));
        Assert.Null(GrokbotImage.Reject(".PNG", false, false, 8, 2, 2, 8));
    }

    [Fact]
    public void Too_many_pixels_above_24000000_does_not_queue()
    {
        var box = new GrokbotMailbox(null);
        Assert.Equal(24_000_000L, 6000L * 4000L);
        Assert.Equal(GrokbotImage.MaxPixels, 24_000_000L);
        Assert.Null(GrokbotImage.Reject(".png", false, true, 32, 6000, 4000, 32));
        var reason = GrokbotImage.Reject(".png", false, true, 32, 6000, 4001, 32);
        Assert.Equal("too many pixels", reason);
        Assert.True(6000L * 4001L > 24_000_000L);
        if (reason is null) box.Queue("pixels", false, [137, 80, 78, 71]);
        Assert.Empty(box.Messages);
    }

    [Fact]
    public void Reopen_returns_the_same_png_bytes_and_empty_image_stays_null()
    {
        var folder = Path.Combine(Path.GetTempPath(), "xai-desk-grokbot-png-" + Guid.NewGuid().ToString("N"));
        var png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 4, 5, 6 };
        try
        {
            var first = GrokbotMailbox.Open(folder);
            Assert.Null(GrokbotImage.Reject(".png", false, true, png.Length, 2, 2, png.Length));
            var plain = first.Queue("plain", false, []);
            Assert.Null(plain.Image);
            var mail = first.Queue("picture", false, png);
            Assert.Equal(png, mail.Image);
            var second = GrokbotMailbox.Open(folder);
            Assert.Null(second.Messages[0].Image);
            Assert.Equal(png, second.Messages[1].Image);
            Assert.DoesNotContain("image", File.ReadAllText(Path.Combine(folder, "thread.jsonl")).Split('\n')[0], StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }
}
