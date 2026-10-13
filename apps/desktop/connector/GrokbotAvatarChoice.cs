namespace Sovereign.Desktop.Connector;

/// <summary>Allowed Grokbot avatar tokens and the ink bytes the mark paints.</summary>
public static class GrokbotAvatarChoice
{
    public const string DefaultColor = "blue";
    public const string DefaultShape = "round";

    public static IReadOnlyList<string> Colors { get; } = ["blue", "pink", "gray", "red", "green", "teal"];
    public static IReadOnlyList<string> Shapes { get; } = ["round", "triangle", "hex"];

    public static string NormalizeColor(string? value) => Normalize(value, Colors, DefaultColor);

    public static string NormalizeShape(string? value) => Normalize(value, Shapes, DefaultShape);

    public static (byte R, byte G, byte B) Ink(string? color) => NormalizeColor(color) switch
    {
        "pink" => (244, 114, 182),
        "gray" => (156, 163, 175),
        "red" => (248, 113, 113),
        "green" => (52, 211, 153),
        "teal" => (45, 212, 191),
        _ => (76, 141, 255)
    };

    private static string Normalize(string? value, IReadOnlyList<string> allowed, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var token = value.Trim();
        foreach (var candidate in allowed)
            if (candidate.Equals(token, StringComparison.OrdinalIgnoreCase))
                return candidate;
        return fallback;
    }
}
