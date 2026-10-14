using System.Globalization;
using System.Text.Json;

namespace Sovereign.Desktop.Connector;

/// <summary>
/// Wake cadence for a Grokbot handoff review.
/// Repeats use the run id and review loop so the same wake is harmless.
/// The audit line never includes the webhook URL or the sender key.
/// </summary>
public static class SwarmWakeRepeat
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(4);
    public static readonly TimeSpan AuthRetry = TimeSpan.FromSeconds(60);
    public const string KeyWarning = "check wake key";

    public static bool Due(DateTimeOffset lastWake, DateTimeOffset now, TimeSpan? interval = null)
    {
        var every = interval ?? Interval;
        if (every < TimeSpan.Zero) every = TimeSpan.Zero;
        return now - lastWake >= every;
    }

    public static TimeSpan? NextAuthRetry(int? statusCode, bool alreadyRetried) =>
        !alreadyRetried && statusCode is 400 or 401 ? AuthRetry : null;

    public static string? Warning(int? statusCode) =>
        statusCode is 400 or 401 ? KeyWarning : null;

    public static string Panel(DateTimeOffset at, int? statusCode)
    {
        var clock = at.ToString("HH:mm", CultureInfo.InvariantCulture);
        var http = statusCode is int code ? code.ToString(CultureInfo.InvariantCulture) : "none";
        return "Wake sent " + clock + ", HTTP " + http;
    }

    public static string Identity(string? runId, int loop) =>
        (runId ?? "") + ":" + loop.ToString(CultureInfo.InvariantCulture);

    public static bool TryReadIdentity(string? json, out string runId, out int loop)
    {
        runId = "";
        loop = 0;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("run_id", out var run) && run.ValueKind == JsonValueKind.String)
                runId = run.GetString() ?? "";
            if (root.TryGetProperty("review_loop", out var value) && value.TryGetInt32(out var parsed))
                loop = parsed;
            return runId.Length > 0 || loop > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static GrokbotAuditLine Audit(DateTimeOffset at, string? runId, int loop, int attempt, int? statusCode, string? url = null, string? key = null)
    {
        var args = Strip("run=" + (runId ?? "")
            + " loop=" + loop.ToString(CultureInfo.InvariantCulture)
            + " attempt=" + attempt.ToString(CultureInfo.InvariantCulture), url, key);
        var result = Strip(statusCode is int code ? "HTTP " + code.ToString(CultureInfo.InvariantCulture) : "HTTP none", url, key);
        return new GrokbotAuditLine(at, "wake", args, result);
    }

    public static void AppendAudit(string directory, GrokbotAuditLine line, string? url = null, string? key = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var safe = new GrokbotAuditLine(line.At, "wake", Strip(line.Args, url, key), Strip(line.Result, url, key));
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(safe, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        File.AppendAllText(Path.Combine(directory, "audit.jsonl"), json + "\n");
    }

    private static string Strip(string? text, string? url, string? key)
    {
        var clean = text ?? "";
        if (!string.IsNullOrEmpty(url)) clean = clean.Replace(url, "", StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(key)) clean = clean.Replace(key, "", StringComparison.Ordinal);
        return clean;
    }
}
