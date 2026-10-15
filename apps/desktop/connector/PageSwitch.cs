namespace Sovereign.Desktop.Connector;

/// <summary>Who asked for the selected page to change.</summary>
public enum PageSwitchSource
{
    /// <summary>A click, key, or menu action by the person at the desk.</summary>
    User,
    /// <summary>The first page shown when the window is built.</summary>
    Startup,
    /// <summary>The Grok Bot connector (app_navigate, or a synthetic app_click / app_key).</summary>
    Connector,
    /// <summary>"Open with SI Desk" from Explorer. The user chose it, so it counts as a user action.</summary>
    Shell,
    /// <summary>Any other in-app code path that is not a user gesture (timers, reloads, settings fix-ups).</summary>
    System
}

/// <summary>
/// Rules for changing the selected page. Only the user (and startup, and an explicit shell open)
/// may move the view. Background sources update data and badges, never the page. The connector
/// is the one exception, and only while the Grokbot-tab act window is open (<see cref="GrokbotMailbox.ActGate"/>).
/// </summary>
public static class PageSwitch
{
    /// <summary>
    /// After the connector injects a click or key, the input lands through the message pump a little
    /// later than the tool call itself. A page change seen inside this window is treated as connector-driven.
    /// </summary>
    public static readonly TimeSpan InputWindow = TimeSpan.FromSeconds(2);

    /// <param name="actOn">The Grokbot-tab act window is open. Only matters for <see cref="PageSwitchSource.Connector"/>.</param>
    public static bool Allowed(PageSwitchSource source, string? currentPage, string? targetPage, bool actOn = false) =>
        source is PageSwitchSource.User or PageSwitchSource.Startup or PageSwitchSource.Shell
        || (source == PageSwitchSource.Connector && actOn)
        || string.Equals(Canon(currentPage), Canon(targetPage), StringComparison.Ordinal);

    /// <summary>A user-looking page change that lands while connector input is still pending is the connector's.</summary>
    public static PageSwitchSource Resolve(PageSwitchSource source, long connectorInputUntilMs, long nowMs) =>
        source == PageSwitchSource.User && nowMs < connectorInputUntilMs ? PageSwitchSource.Connector : source;

    /// <summary>
    /// Decides an app_navigate call and writes the tool reply. Returns true only when the page may change.
    /// With the act window closed nothing happens, even for the page already shown, and the reply says Not switched.
    /// </summary>
    public static bool TryConnectorSwitch(string? currentPage, string? requestedPage, GrokbotActGate gate, out string reply)
    {
        var target = GrokbotWindow.CanonicalPage(requestedPage);
        if (!gate.Open)
        {
            reply = GrokbotMailbox.NotSwitched(gate);
            return false;
        }
        if (Allowed(PageSwitchSource.Connector, currentPage, target, true))
        {
            reply = "Opened " + (requestedPage ?? "").Trim() + ".";
            return true;
        }
        reply = NotSwitched(currentPage);
        return false;
    }

    public static string NotSwitched(string? currentPage) =>
        "Not switched: the user is on " + PageName(currentPage) + ". Page changes are user-only.";

    public static string PageName(string? page) => Canon(page) switch
    {
        "chat" => "Chat",
        "room" => "Shared room",
        "settings" => "Settings",
        "grokbot" => "Grokbot",
        "" => "another page",
        var other => other
    };

    private static string Canon(string? page) => (page ?? "").Trim().ToLowerInvariant();
}
