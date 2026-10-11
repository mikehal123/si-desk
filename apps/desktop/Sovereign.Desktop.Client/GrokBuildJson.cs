using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sovereign.Desktop.Client;

public sealed class GrokBuildUsageException : IOException
{
    public GrokBuildUsageException(string message) : base(message) {}
}

/// <summary>Tolerant JSON-RPC readers for Grok CLI frames. Numeric strings never throw.</summary>
public static class GrokBuildJson
{
    private const long LogLimit = 1_048_576;
    private static readonly object LogSync = new();
    private static string? _logFile;
    private static readonly Regex BearerToken = new(
        @"Bearer\s+[A-Za-z0-9._~+/=-]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SecretAssignment = new(
        @"((?:refresh[_-]?token|api[_-]?key|secret|token)\s*[""']?\s*[:=]\s*[""']?)([^""'\s,}&]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex QueryString = new(
        @"\?[^\s""'\\<>]+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static void SetLogFile(string? path)
    {
        lock (LogSync) _logFile = path;
    }

    public static bool TryGetInt32(JsonElement element, out int value)
    {
        if (TryGetInt64(element, out var wide) && wide is >= int.MinValue and <= int.MaxValue)
        {
            value = (int)wide;
            return true;
        }
        value = 0;
        return false;
    }

    public static bool TryGetInt64(JsonElement element, out long value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number when element.TryGetInt64(out value):
                return true;
            case JsonValueKind.String when long.TryParse(element.GetString()?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value):
                return true;
            default:
                value = 0;
                return false;
        }
    }

    public static bool TryGetDouble(JsonElement element, out double value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number when element.TryGetDouble(out value):
                return true;
            case JsonValueKind.String when double.TryParse(element.GetString()?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value):
                return true;
            default:
                value = 0;
                return false;
        }
    }

    internal static string? ScalarText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString()?.Trim(),
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null
    };

    internal static bool IsUsageFailure(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.Contains("429", StringComparison.OrdinalIgnoreCase)
            || text.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || text.Contains("usage limit", StringComparison.OrdinalIgnoreCase)
            || text.Contains("quota", StringComparison.OrdinalIgnoreCase)
            || text.Contains("free-usage-exhausted", StringComparison.OrdinalIgnoreCase)
            || text.Contains("free usage", StringComparison.OrdinalIgnoreCase);
    }

    internal static string ProviderText(string? code, string? message)
    {
        var trimmedMessage = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
        var trimmedCode = string.IsNullOrWhiteSpace(code) ? null : code.Trim();
        if (trimmedMessage is null) return trimmedCode ?? "usage limit";
        if (trimmedCode is null || trimmedMessage.Contains(trimmedCode, StringComparison.Ordinal) || trimmedCode.StartsWith('-'))
            return trimmedMessage;
        return trimmedCode + " " + trimmedMessage;
    }

    internal static bool TryReadUsageFailure(JsonElement element, out string text) =>
        TryReadUsageFailure(element, 0, out text);

    /// <summary>Parse usage, limit, remaining, token, and quota fields from numbers or numeric strings.</summary>
    internal static void ReadMeters(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (IsMeterName(property.Name)) ReadMeter(property.Value);
                    else ReadMeters(property.Value);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) ReadMeters(item);
                break;
        }
    }

    internal static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var value = BearerToken.Replace(text, "Bearer [redacted]");
        value = SecretAssignment.Replace(value, "$1[redacted]");
        return QueryString.Replace(value, "?[redacted]");
    }

    internal static void Log(string line)
    {
        try
        {
            var payload = Redact(line) + Environment.NewLine;
            lock (LogSync)
            {
                var path = string.IsNullOrWhiteSpace(_logFile)
                    ? Path.Combine(Sovereign.Desktop.Core.XaiDeskIdentity.DataDirectory(), "grokbuild.log")
                    : _logFile;
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.AppendAllText(path, payload);
                var length = new FileInfo(path).Length;
                if (length <= LogLimit) return;
                var bytes = File.ReadAllBytes(path);
                var keep = (int)Math.Min(LogLimit, bytes.Length);
                var start = bytes.Length - keep;
                var newline = Array.IndexOf(bytes, (byte)'\n', start);
                if (newline >= 0 && newline + 1 < bytes.Length) start = newline + 1;
                File.WriteAllBytes(path, bytes.AsSpan(start).ToArray());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool TryReadUsageFailure(JsonElement element, int depth, out string text)
    {
        text = "";
        if (depth > 6) return false;
        if (element.ValueKind == JsonValueKind.String)
        {
            var scalar = element.GetString()?.Trim();
            if (!IsUsageFailure(scalar)) return false;
            text = scalar!;
            return true;
        }
        if (element.ValueKind != JsonValueKind.Object) return false;

        var code = element.TryGetProperty("code", out var codeElement) ? ScalarText(codeElement) : null;
        string? detail = null;
        string? fallback = null;
        foreach (var name in new[] { "message", "error", "reason", "detail", "stopReason", "status" })
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (TryReadUsageFailure(value, depth + 1, out text)) return true;
                continue;
            }
            var scalar = ScalarText(value);
            if (string.IsNullOrWhiteSpace(scalar)) continue;
            if (name == "stopReason" && scalar is "end_turn" or "end" or "cancelled") continue;
            if (name == "status" && scalar is "completed" or "pending" or "in_progress" or "ok") continue;
            fallback ??= scalar;
            if (!IsUsageFailure(scalar)) continue;
            detail = scalar;
            break;
        }
        if (detail is null && IsUsageFailure(code)) detail = fallback;
        if (detail is null && !IsUsageFailure(code))
        {
            if (!element.TryGetProperty("sessionUpdate", out var kind)) return false;
            detail = ScalarText(kind);
            if (!IsUsageFailure(detail)) return false;
        }
        text = ProviderText(code, detail);
        return IsUsageFailure(text);
    }

    private static void ReadMeter(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
            case JsonValueKind.String:
                TryGetInt64(value, out _);
                TryGetDouble(value, out _);
                break;
            default:
                ReadMeters(value);
                break;
        }
    }

    private static bool IsMeterName(string name) =>
        name.Contains("usage", StringComparison.OrdinalIgnoreCase)
        || name.Contains("limit", StringComparison.OrdinalIgnoreCase)
        || name.Contains("remaining", StringComparison.OrdinalIgnoreCase)
        || name.Contains("tokens", StringComparison.OrdinalIgnoreCase)
        || name.Contains("quota", StringComparison.OrdinalIgnoreCase);
}
