using Sovereign.Desktop.Connector;
using Xunit;

namespace XaiDesk.Tests;

/// <summary>The Act on window: who may move the view, for how long, and that a Grok Bot post never opens it.</summary>
public sealed class GrokbotActWindowTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Window_opens_with_an_act_on_message_and_closes_after_ten_minutes()
    {
        var mail = new GrokbotMailbox(null);
        var gate = mail.ActGate(T0);
        Assert.False(gate.Open);
        Assert.Equal("there is no Grokbot-tab message.", gate.Reason);

        mail.Clock = () => T0;
        mail.Queue("just chatting", askToAct: false);
        Assert.Equal("the latest Grokbot-tab message is not marked Act on.", mail.ActGate(T0).Reason);
        Assert.False(mail.ActWindowOpen(T0));

        mail.Queue("open settings", askToAct: true);
        Assert.True(mail.ActWindowOpen(T0));
        Assert.True(mail.ActWindowOpen(T0.AddMinutes(2)));
        Assert.True(mail.ActWindowOpen(T0.AddMinutes(9).AddSeconds(59)));
        Assert.False(mail.ActWindowOpen(T0.AddMinutes(10)));
        var stale = mail.ActGate(T0.AddMinutes(11));
        Assert.False(stale.Open);
        Assert.Equal("the latest Act on message is 11 min old; the 10 min window has passed.", stale.Reason);
        Assert.Equal(TimeSpan.FromMinutes(10), GrokbotMailbox.ActWindow);

        // A later plain message closes the window again even when the Act on one is fresh.
        mail.Queue("never mind", askToAct: false);
        Assert.False(mail.ActWindowOpen(T0.AddSeconds(5)));
    }

    [Fact]
    public void Only_navigate_click_and_key_need_the_window()
    {
        Assert.True(GrokbotMailbox.NeedsActWindow("app_navigate"));
        Assert.True(GrokbotMailbox.NeedsActWindow("app_click"));
        Assert.True(GrokbotMailbox.NeedsActWindow("app_key"));
        Assert.False(GrokbotMailbox.NeedsActWindow("app_type"));
        Assert.False(GrokbotMailbox.NeedsActWindow("app_ui_tree"));
        Assert.False(GrokbotMailbox.NeedsActWindow("app_screenshot"));
        Assert.False(GrokbotMailbox.NeedsActWindow(null));

        var mail = new GrokbotMailbox(null);
        var prefs = mail.PreferencesCopy();
        prefs.AllowControl = true;
        mail.SavePreferences(prefs);
        mail.Clock = () => T0;
        mail.Queue("look only", askToAct: false);
        mail.EnsureAction("app_type");
        mail.EnsureAction("app_ui_tree");
        var refused = Assert.Throws<InvalidOperationException>(() => mail.EnsureAction("app_navigate"));
        Assert.StartsWith("Not switched: the latest Grokbot-tab message is not marked Act on.", refused.Message, StringComparison.Ordinal);
        Assert.Contains("app_navigate, app_click, and app_key run only while", refused.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => mail.EnsureAction("app_click"));
        Assert.Throws<InvalidOperationException>(() => mail.EnsureAction("app_key"));

        // Allow control is still required: an Act on message alone is not enough once control is off.
        prefs.AllowControl = false;
        mail.SavePreferences(prefs);
        mail.Queue("go", askToAct: true);
        mail.EnsureAction("app_type"); // base behaviour for the other tools: Act on grants control
        var off = Assert.Throws<InvalidOperationException>(() => mail.EnsureAction("app_navigate"));
        Assert.Equal(GrokbotMailbox.ControlOff, off.Message);
        Assert.Throws<InvalidOperationException>(() => mail.EnsureAction("app_click"));
        Assert.Throws<InvalidOperationException>(() => mail.EnsureAction("app_key"));
        prefs.Stopped = true;
        mail.SavePreferences(prefs);
        var stopped = Assert.Throws<InvalidOperationException>(() => mail.EnsureAction("app_navigate"));
        Assert.Contains("Grokbot control is off", stopped.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "none", false)]
    [InlineData(true, "plain", false)]
    [InlineData(true, "aged", false)]
    [InlineData(true, "fresh", true)]
    [InlineData(false, "fresh", false)]
    [InlineData(false, "none", false)]
    public void View_and_input_tools_need_control_on_and_a_fresh_act_on_message(bool allowControl, string latest, bool allowed)
    {
        var mail = new GrokbotMailbox(null);
        var prefs = mail.PreferencesCopy();
        prefs.AllowControl = allowControl;
        mail.SavePreferences(prefs);
        var now = T0;
        mail.Clock = () => now;
        switch (latest)
        {
            case "plain": mail.Queue("hello", askToAct: false); break;
            case "aged": mail.Queue("go", askToAct: true); now = T0.AddMinutes(11); break;
            case "fresh": mail.Queue("go", askToAct: true); now = T0.AddMinutes(2); break;
        }
        foreach (var tool in new[] { "app_navigate", "app_click", "app_key" })
        {
            if (allowed)
            {
                mail.EnsureAction(tool);
                continue;
            }
            var refused = Assert.Throws<InvalidOperationException>(() => mail.EnsureAction(tool));
            if (!allowControl) Assert.Equal(GrokbotMailbox.ControlOff, refused.Message);
            else Assert.StartsWith("Not switched: ", refused.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_post_is_a_bot_message_that_survives_reload_and_never_opens_the_window()
    {
        var folder = Path.Combine(Path.GetTempPath(), "sidesk-post-" + Guid.NewGuid().ToString("N"));
        try
        {
            var mail = GrokbotMailbox.Open(folder);
            mail.Clock = () => T0;
            mail.Queue("open settings", askToAct: true);
            var post = mail.Post("Swarm run-1: the manager posted a status update.");
            Assert.True(post.IsPost);
            Assert.Equal("", post.Text);
            Assert.Equal(GrokbotMailStatus.Answered, post.Status);
            Assert.Equal("Swarm run-1: the manager posted a status update.", post.Reply);
            // The latest user message still rules the window; the post does not count as a user message.
            Assert.True(mail.ActWindowOpen(T0.AddMinutes(1)));
            Assert.Single(mail.ListSince(null)); // only the user's message is in the inbox

            var reloaded = GrokbotMailbox.Open(folder);
            var saved = Assert.Single(reloaded.Messages, item => item.IsPost);
            Assert.Equal(post.Id, saved.Id);
            Assert.Equal(post.Reply, saved.Reply);
            var text = GrokbotTranscript.Format(reloaded.Messages);
            Assert.Contains("Grokbot: Swarm run-1: the manager posted a status update.", text, StringComparison.Ordinal);
            Assert.DoesNotContain("You: \n", text, StringComparison.Ordinal); // a post has no "You:" line
            Assert.Equal(2, GrokbotTranscript.EntryCount(reloaded.Messages)); // the user's line and the post
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }
}
