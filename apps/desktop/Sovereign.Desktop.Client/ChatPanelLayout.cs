namespace Sovereign.Desktop.Client;

/// <summary>Keep a readable main conversation beside a bounded, user-sized inspector.</summary>
public static class ChatPanelLayout
{
    public const double DefaultWidth = 360;
    public const double DividerWidth = DeskSplit.Grip;

    public static double Width(double available, double? preferred)
    {
        if (!double.IsFinite(available) || available <= 0) return 0;
        var target = preferred is { } value && double.IsFinite(value) ? value : DefaultWidth;
        // At small sizes the inspector shrinks first; it never takes over the transcript.
        var maximum = Math.Max(0, Math.Min(available * .60, available - DividerWidth - Math.Min(380, available * .56)));
        var minimum = Math.Min(260, maximum);
        return Math.Clamp(target, minimum, maximum);
    }

    /// <summary>Share the inspector between its list and the selected helper or file detail.</summary>
    public static double ListFraction(double availableHeight, double? preferred, bool helper)
    {
        var target = preferred is { } value && double.IsFinite(value) ? value : helper ? .32 / 1.32 : 1.0 / 3;
        if (!double.IsFinite(availableHeight) || availableHeight <= 0) return Math.Clamp(target, .10, .80);
        var minimum = Math.Min(100, availableHeight * .35) / availableHeight;
        var maximum = 1 - Math.Min(180, availableHeight * .50) / availableHeight;
        return Math.Clamp(target, minimum, maximum);
    }
}
