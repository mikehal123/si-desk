namespace Sovereign.Desktop.Connector;

/// <summary>Size and type gate for one Grokbot image. The desktop page decodes and encodes; this method only decides.</summary>
public static class GrokbotImage
{
    public const long MaxBytes = 20L * 1024 * 1024;
    public const long MaxPixels = 24_000_000;

    public static double Scale(int width, int height) => Math.Min(1d, 1600d / Math.Max(width, height));

    public static bool IsPng(byte[]? data) =>
        data is { Length: >= 8 }
        && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47
        && data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A;

    public static string? Reject(string? type, bool clipboardBitmap, bool encodedAsPng, long sourceBytes, int width, int height, long encodedPngBytes)
    {
        if (clipboardBitmap)
        {
            if (!encodedAsPng) return "wrong type";
        }
        else if (!IsFileType(type))
            return "wrong type";
        if (sourceBytes > MaxBytes) return "too big";
        if ((long)width * height > MaxPixels) return "too many pixels";
        if (encodedPngBytes > MaxBytes) return "too big";
        return null;
    }

    private static bool IsFileType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return false;
        var ext = type.Trim();
        var dot = ext.LastIndexOf('.');
        ext = dot >= 0 ? ext[dot..] : "." + ext;
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }
}
