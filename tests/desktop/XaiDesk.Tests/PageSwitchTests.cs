using Sovereign.Desktop.Connector;
using Xunit;

namespace XaiDesk.Tests;

public sealed class PageSwitchTests
{
    [Fact]
    public void User_startup_and_shell_may_switch_pages()
    {
        Assert.True(PageSwitch.Allowed(PageSwitchSource.User, "grokbot", "chat"));
        Assert.True(PageSwitch.Allowed(PageSwitchSource.User, "chat", "settings"));
        Assert.True(PageSwitch.Allowed(PageSwitchSource.Startup, "chat", "chat"));
        Assert.True(PageSwitch.Allowed(PageSwitchSource.Startup, "grokbot", "chat"));
        Assert.True(PageSwitch.Allowed(PageSwitchSource.Shell, "grokbot", "chat"));
    }

    [Fact]
    public void Connector_and_system_cannot_move_the_user_off_their_page()
    {
        Assert.False(PageSwitch.Allowed(PageSwitchSource.Connector, "grokbot", "chat"));
        Assert.False(PageSwitch.Allowed(PageSwitchSource.Connector, "chat", "grokbot"));
        Assert.False(PageSwitch.Allowed(PageSwitchSource.Connector, "chat", "new-chat"));
        Assert.False(PageSwitch.Allowed(PageSwitchSource.Connector, "settings", "room"));
        Assert.False(PageSwitch.Allowed(PageSwitchSource.System, "grokbot", "chat"));
        Assert.False(PageSwitch.Allowed(PageSwitchSource.System, "chat", "settings"));
    }

    [Fact]
    public void Same_page_is_always_allowed()
    {
        Assert.True(PageSwitch.Allowed(PageSwitchSource.Connector, "grokbot", "grokbot"));
        Assert.True(PageSwitch.Allowed(PageSwitchSource.Connector, "chat", " Chat "));
        Assert.True(PageSwitch.Allowed(PageSwitchSource.System, "settings", "settings"));
        Assert.True(PageSwitch.Allowed(PageSwitchSource.Connector, "room", "room"));
    }

    [Fact]
    public void Pending_connector_input_turns_a_user_switch_into_a_connector_switch()
    {
        Assert.Equal(PageSwitchSource.Connector, PageSwitch.Resolve(PageSwitchSource.User, 1_000, 999));
        Assert.Equal(PageSwitchSource.User, PageSwitch.Resolve(PageSwitchSource.User, 1_000, 1_000));
        Assert.Equal(PageSwitchSource.User, PageSwitch.Resolve(PageSwitchSource.User, 1_000, 5_000));
        Assert.Equal(PageSwitchSource.User, PageSwitch.Resolve(PageSwitchSource.User, 0, 0));
        Assert.Equal(PageSwitchSource.Startup, PageSwitch.Resolve(PageSwitchSource.Startup, 1_000, 999));
        Assert.Equal(PageSwitchSource.Shell, PageSwitch.Resolve(PageSwitchSource.Shell, 1_000, 999));
        Assert.Equal(PageSwitchSource.System, PageSwitch.Resolve(PageSwitchSource.System, 1_000, 999));
        Assert.True(PageSwitch.InputWindow >= TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Connector_may_switch_only_while_the_act_window_is_open()
    {
        Assert.True(PageSwitch.Allowed(PageSwitchSource.Connector, "grokbot", "chat", actOn: true));
        Assert.True(PageSwitch.Allowed(PageSwitchSource.Connector, "chat", "settings", actOn: true));
        Assert.False(PageSwitch.Allowed(PageSwitchSource.Connector, "grokbot", "chat", actOn: false));
        Assert.False(PageSwitch.Allowed(PageSwitchSource.System, "grokbot", "chat", actOn: true));
        Assert.False(PageSwitch.Allowed(PageSwitchSource.System, "chat", "settings", actOn: true));
    }

    [Fact]
    public void Connector_reply_says_not_switched_when_the_act_window_is_closed()
    {
        var closed = new GrokbotActGate(false, "the latest Grokbot-tab message is not marked Act on.");
        Assert.False(PageSwitch.TryConnectorSwitch("grokbot", "Chat", closed, out var blocked));
        Assert.StartsWith("Not switched: the latest Grokbot-tab message is not marked Act on.", blocked, StringComparison.Ordinal);
        Assert.Contains("under 10 min old", blocked, StringComparison.Ordinal);
        Assert.False(PageSwitch.TryConnectorSwitch("grokbot", "Grokbot", closed, out var same));
        Assert.StartsWith("Not switched:", same, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => PageSwitch.TryConnectorSwitch("chat", "Nowhere", closed, out _));
    }

    [Fact]
    public void Connector_reply_says_opened_while_the_act_window_is_open()
    {
        var open = new GrokbotActGate(true, "");
        Assert.True(PageSwitch.TryConnectorSwitch("grokbot", "Chat", open, out var moved));
        Assert.Equal("Opened Chat.", moved);
        Assert.True(PageSwitch.TryConnectorSwitch("chat", " chat ", open, out var trimmed));
        Assert.Equal("Opened chat.", trimmed);
        Assert.Throws<ArgumentException>(() => PageSwitch.TryConnectorSwitch("chat", "Nowhere", open, out _));
        Assert.Equal("Not switched: the user is on another page. Page changes are user-only.", PageSwitch.NotSwitched(null));
        Assert.Equal("Not switched: the user is on Shared room. Page changes are user-only.", PageSwitch.NotSwitched("room"));
    }
}
