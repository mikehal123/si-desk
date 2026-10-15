using System.Globalization;
using System.Text;

namespace Sovereign.Desktop.Connector;

/// <summary>Plain-text Grokbot transcript and jump-to-latest rules. No WinUI.</summary>
public static class GrokbotTranscript
{
    public const double BottomSlack = 1;

    public readonly record struct Jump(bool StickToEnd, int Unread, double Offset)
    {
        public static Jump Following => new(true, 0, 0);
    }

    public static string Format(IEnumerable<GrokbotMail> messages)
    {
        var builder = new StringBuilder();
        foreach (var mail in messages)
        {
            if (!mail.IsPost) Append(builder, "You: ", mail.Text, mail.Created);
            if (string.IsNullOrWhiteSpace(mail.Reply)) continue;
            Append(builder, "Grokbot: ", mail.Reply, mail.AnsweredAt ?? mail.Created);
        }
        return builder.ToString();
    }

    public static string Timestamp(DateTimeOffset at) =>
        at.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture);

    public static int EntryCount(IEnumerable<GrokbotMail> messages)
    {
        var count = 0;
        foreach (var mail in messages)
        {
            if (!mail.IsPost) count++;
            if (!string.IsNullOrWhiteSpace(mail.Reply)) count++;
        }
        return count;
    }

    public static double DistanceFromBottom(double extent, double viewport, double offset)
    {
        if (!Finite(extent) || !Finite(viewport) || !Finite(offset)) return 0;
        var distance = extent - viewport - offset;
        if (!Finite(distance) || distance < 0) return 0;
        return distance;
    }

    public static bool IsAtBottom(double extent, double viewport, double offset) =>
        DistanceFromBottom(extent, viewport, offset) <= BottomSlack;

    public static bool ShowJump(double distanceFromBottom, double viewportHeight) =>
        viewportHeight > 0 && distanceFromBottom > viewportHeight;

    public static double LatestOffset(double extent, double viewport)
    {
        if (!Finite(extent) || !Finite(viewport)) return 0;
        var latest = extent - viewport;
        return latest > 0 ? latest : 0;
    }

    public static bool ButtonVisible(Jump state, double extent, double viewport) =>
        !state.StickToEnd && ShowJump(DistanceFromBottom(extent, viewport, state.Offset), viewport);

    public static Jump OnScroll(Jump state, double extent, double viewport, double offset)
    {
        if (IsAtBottom(extent, viewport, offset))
            return new Jump(true, 0, LatestOffset(extent, viewport));
        var held = Finite(offset) && offset > 0 ? offset : 0;
        return new Jump(false, state.Unread, held);
    }

    public static Jump OnArrivals(Jump state, int arrived, double extent, double viewport)
    {
        if (arrived < 0) arrived = 0;
        if (!Finite(extent) || !Finite(viewport) || viewport <= 0 || extent <= 0)
        {
            if (state.StickToEnd) return new Jump(true, 0, state.Offset);
            return new Jump(false, state.Unread + arrived, state.Offset);
        }

        var latest = LatestOffset(extent, viewport);
        if (state.StickToEnd) return new Jump(true, 0, latest);
        var offset = state.Offset > latest ? latest : state.Offset;
        if (offset < 0) offset = 0;
        if (IsAtBottom(extent, viewport, offset)) return new Jump(true, 0, latest);
        return new Jump(false, state.Unread + arrived, offset);
    }

    public static Jump OnJump(Jump state, double extent, double viewport) =>
        new(true, 0, LatestOffset(extent, viewport));

    private static void Append(StringBuilder builder, string label, string text, DateTimeOffset at)
    {
        if (builder.Length > 0) builder.Append('\n');
        builder.Append(label);
        builder.Append(text);
        builder.Append('\n');
        builder.Append(Timestamp(at.ToLocalTime()));
    }

    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
