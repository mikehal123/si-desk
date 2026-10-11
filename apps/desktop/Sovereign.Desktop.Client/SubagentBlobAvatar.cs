namespace Sovereign.Desktop.Client;

/// <summary>Stable creature avatar for one helper. The same id always picks the same color and shape.</summary>
public readonly record struct SubagentBlobChoice(int ColorIndex, int ShapeIndex, byte R, byte G, byte B)
{
    public string Key => "blob-" + ColorIndex + "-" + ShapeIndex;
}

public static class SubagentBlobAvatar
{
    public const int ColorCount = 8;
    public const int ShapeCount = 4;

    private static readonly (byte R, byte G, byte B)[] Colors =
    [
        (0x4C, 0x8D, 0xFF),
        (0xF4, 0x72, 0xB6),
        (0xF0, 0x5D, 0x5E),
        (0x3D, 0xDC, 0x97),
        (0x2D, 0xD4, 0xBF),
        (0x9A, 0xA0, 0xAB),
        (0xFB, 0x92, 0x3C),
        (0xA7, 0x8B, 0xFA)
    ];

    public static SubagentBlobChoice Pick(string? id, string? name)
    {
        var key = string.IsNullOrWhiteSpace(id) ? name?.Trim() ?? "" : id.Trim();
        var hash = StableHash(key);
        var color = (int)(hash % ColorCount);
        var shape = (int)(hash / ColorCount % ShapeCount);
        var ink = Colors[color];
        return new SubagentBlobChoice(color, shape, ink.R, ink.G, ink.B);
    }

    private static uint StableHash(string text)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var ch in text)
            {
                hash ^= ch;
                hash *= 16777619;
            }
            return hash;
        }
    }
}
