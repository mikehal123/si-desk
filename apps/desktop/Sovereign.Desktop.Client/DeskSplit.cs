using System.Globalization;
using System.Text.Json;

namespace Sovereign.Desktop.Client;

/// <summary>Saved splitter widths for the shell sidebar and the chat side panel.</summary>
public sealed record DeskLayoutWidths(double RightPanel, double Sidebar)
{
    public static DeskLayoutWidths Default { get; } = new(DeskSplit.RightDefault, DeskSplit.SidebarDefault);
}

public static class DeskSplit
{
    public const double RightDefault = 360;
    public const double RightMin = 260;
    public const double RightMaxFraction = 0.60;
    public const double ChatMin = 420;
    public const double SidebarDefault = 240;
    public const double SidebarMin = 180;
    public const double SidebarMax = 480;
    public const double Nudge = 16;
    // Keep a generous pointer target even when the visible divider is a hairline.
    public const double Grip = 16;

    public static double ClampRight(double requested, double window)
    {
        if (double.IsNaN(requested) || requested <= 0) requested = RightDefault;
        var room = window - ChatMin - Grip;
        if (room < RightMin) return RightMin;
        var max = Math.Min(window * RightMaxFraction, room);
        if (max < RightMin) return RightMin;
        return Math.Clamp(requested, RightMin, max);
    }

    public static double ClampSidebar(double requested, double window)
    {
        if (double.IsNaN(requested) || requested <= 0) requested = SidebarDefault;
        var room = window - ChatMin - Grip;
        if (room < SidebarMin) return SidebarMin;
        var max = Math.Min(SidebarMax, Math.Min(window * 0.40, room));
        if (max < SidebarMin) return SidebarMin;
        return Math.Clamp(requested, SidebarMin, max);
    }

    /// <summary>Pointer movement to the right shrinks the right panel.</summary>
    public static double DragRight(double current, double pointerDelta, double window) =>
        ClampRight(current - pointerDelta, window);

    /// <summary>Pointer movement to the right grows the sidebar.</summary>
    public static double DragSidebar(double current, double pointerDelta, double window) =>
        ClampSidebar(current + pointerDelta, window);

    /// <summary>Arrow +1 moves the divider right.</summary>
    public static double NudgeRight(double current, int arrow, double window) =>
        ClampRight(current - arrow * Nudge, window);

    public static double NudgeSidebar(double current, int arrow, double window) =>
        ClampSidebar(current + arrow * Nudge, window);
}

public static class DeskLayoutStore
{
    public static DeskLayoutWidths Load(string profile)
    {
        try
        {
            var path = Path.Combine(profile, "layout.json");
            if (!File.Exists(path)) return DeskLayoutWidths.Default;
            using var body = JsonDocument.Parse(File.ReadAllText(path));
            if (body.RootElement.ValueKind != JsonValueKind.Object) return DeskLayoutWidths.Default;
            return new DeskLayoutWidths(Read(body.RootElement, "rightPanel", DeskSplit.RightDefault), Read(body.RootElement, "sidebar", DeskSplit.SidebarDefault));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return DeskLayoutWidths.Default;
        }
    }

    public static void Save(string profile, DeskLayoutWidths widths)
    {
        Directory.CreateDirectory(profile);
        var path = Path.Combine(profile, "layout.json");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var json = "{\"rightPanel\":" + widths.RightPanel.ToString("0.###", CultureInfo.InvariantCulture)
            + ",\"sidebar\":" + widths.Sidebar.ToString("0.###", CultureInfo.InvariantCulture) + "}";
        try
        {
            File.WriteAllText(temp, json);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static void SaveRight(string profile, double width)
    {
        var current = Load(profile);
        Save(profile, current with { RightPanel = width });
    }

    public static void SaveSidebar(string profile, double width)
    {
        var current = Load(profile);
        Save(profile, current with { Sidebar = width });
    }

    private static double Read(JsonElement root, string name, double fallback) =>
        root.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) && number > 0 ? number : fallback;
}
