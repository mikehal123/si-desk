namespace Sovereign.Desktop.Connector;

public static class GrokbotNotice
{
    public static string FirstLine(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return "";
        var breakAt = reply.IndexOfAny(['\r', '\n']);
        var segment = breakAt < 0 ? reply : reply[..breakAt];
        return segment.Trim();
    }

    public static bool ShouldToast(bool tabVisible, bool windowActive) => !(tabVisible && windowActive);

    public static bool ShouldMarkUnread(bool tabVisible, bool windowActive) => !(tabVisible && windowActive);

    public static int UnreadCount(int arrivedWhileClosedAndUncleared, bool tabVisible, bool windowActive) =>
        (tabVisible && windowActive) || arrivedWhileClosedAndUncleared < 0 ? 0 : arrivedWhileClosedAndUncleared;
}

public interface IGrokbotToaster
{
    void Show(string line);
}
