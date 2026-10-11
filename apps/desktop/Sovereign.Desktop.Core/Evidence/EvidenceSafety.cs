namespace Sovereign.Desktop.Core.Evidence;

public static class EvidenceSafety
{
    public static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "https",
        "http"
    };

    public static bool IsAllowedUri(string value, out Uri? uri, out string reason)
    {
        uri = null;
        reason = "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed))
        {
            reason = "URI is not absolute.";
            return false;
        }

        if (!AllowedSchemes.Contains(parsed.Scheme))
        {
            reason = $"Scheme '{parsed.Scheme}' is not allowed. Use a deliberate https/http open action.";
            return false;
        }

        uri = parsed;
        return true;
    }

    public static string AsData(string text) =>
        text.Replace("\0", "", StringComparison.Ordinal);
}
