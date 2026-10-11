namespace Sovereign.Desktop.Client;

public static class GrokBuildConnectionText
{
    public static string Format(GrokBuildStatusReport report)
    {
        if (!report.Ok) return report.Error ?? "";
        var version = report.Version.StartsWith("grok ", StringComparison.OrdinalIgnoreCase)
            ? report.Version["grok ".Length..]
            : report.Version;
        return $"grok {version} · signed in as {report.SignedInAs} · plan {report.Plan}";
    }

    public static bool OfferCursorGrok47(Exception error)
    {
        if (error is GrokBuildUsageException) return true;
        var message = error.Message;
        return message.Contains("429", StringComparison.OrdinalIgnoreCase)
            || message.Contains("free-usage-exhausted", StringComparison.OrdinalIgnoreCase)
            || message.Contains("free usage", StringComparison.OrdinalIgnoreCase)
            || message.Contains("usage limit", StringComparison.OrdinalIgnoreCase)
            || message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || message.Contains("quota", StringComparison.OrdinalIgnoreCase);
    }

    public static string? ChatFailureText(Exception error) =>
        OfferCursorGrok47(error) ? error.Message : null;
}
