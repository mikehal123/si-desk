using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sovereign.Desktop.Client;

/// <summary>
/// Readable command text. A nonzero exit stays a failure. Startup denial is not a command that ran.
/// </summary>
public static class CommandPresentation
{
    public sealed record Result(string Title, string Input, string Output, string Diagnostics, string Status, bool ProcessStarted, int? ExitCode);

    private static readonly Regex Secret = new(
        @"(?i)\b(api[_-]?key|access[_-]?token|refresh[_-]?token|authorization|password|secret)\b[""']?\s*[:=]\s*(?:Bearer\s+[^\s,""']+|""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'|[^\s,;}]+)|Bearer\s+[^\s,""']+|\bsk-[A-Za-z0-9_-]{8,}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));

    public static Result Describe(JsonElement item, string status, string? priorOutput)
    {
        var command = CommandText(item);
        var aggregated = Text(item, "aggregatedOutput") ?? Text(item, "aggregated_output") ?? "";
        var stderr = Text(item, "stderr") ?? "";
        var stdout = Text(item, "stdout") ?? "";
        var captured = aggregated.Length > 0 ? aggregated : Combine(stderr, stdout);
        var streamed = priorOutput ?? "";
        var evidence = captured.Length == 0 ? streamed
            : streamed.Length == 0 || captured.Contains(streamed, StringComparison.Ordinal) ? captured
            : streamed.Contains(captured, StringComparison.Ordinal) ? streamed
            : streamed + captured;
        var exit = Integer(item, "exitCode") ?? Integer(item, "exit_code");
        var duration = DurationMs(item);
        var source = Text(item, "source") ?? "";
        if (exit is int code && code != 0 && status == "completed") status = "failed";
        var started = !IsStartupFailure(status, exit, duration, evidence, source);
        var denied = IsAccessDenied(evidence);
        var title = command.Length == 0 ? "Command" : Clip(command, 240);
        string output;
        if (!started && denied)
        {
            title = "Windows could not start the WindowsApps PowerShell alias";
            output = title + (command.Length == 0 ? "" : "\nCommand: " + Clip(command, 500))
                + "\nCodex launched the Microsoft Store pwsh.exe alias. That file is an execution alias, and the sandbox refused to start it."
                + "\nThis command did not start. SI Desk did not automatically retry it.";
        }
        else if (!started)
        {
            title = "Windows could not start this command";
            output = title + (exit is int failed ? $"\nExit: {failed}" : "")
                + (command.Length == 0 ? "" : "\nCommand: " + Clip(command, 500))
                + "\nThis command did not start. SI Desk did not automatically retry it.";
        }
        else if (status == "failed" && IsNoMatch(command, evidence, exit))
            output = "No matches" + (exit is int searchExit ? $"\nExit: {searchExit}" : "");
        else if (status == "failed")
            output = "Command failed" + (exit is int failed ? $" — exit {failed}" : "")
                + (evidence.Length == 0 ? "" : "\n" + Clip(evidence, 4000));
        else output = evidence;
        if (started && status == "completed" && exit is int finished)
        {
            var exitLine = "Exit: " + finished;
            if (!output.Contains(exitLine, StringComparison.Ordinal))
                output = output.Length == 0 ? exitLine : output.TrimEnd() + "\n" + exitLine;
        }
        var diagnostics = Redact(Clip(item.ValueKind == JsonValueKind.Object ? item.GetRawText() : "", 4000));
        return new(title, command, Clip(Redact(output), 32000), diagnostics, status, started, exit);
    }

    private static readonly Regex RunningCell = new(
        @"^Script running with cell ID \d+\.?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));

    /// <summary>"Script running" with no command output is still in progress. Termination is not success.</summary>
    public static bool RunningCellNotice(string text)
    {
        var body = text.Replace("\r\n", "\n").Trim();
        if (body.Length == 0) return false;
        var first = body.Split('\n', 2)[0].Trim();
        try { if (!RunningCell.IsMatch(first)) return false; }
        catch (RegexMatchTimeoutException) { return false; }
        var marker = body.IndexOf("Output:", StringComparison.OrdinalIgnoreCase);
        return marker < 0 || body[(marker + "Output:".Length)..].Trim().Length == 0;
    }

    public static string? CellId(string text)
    {
        try
        {
            var match = Regex.Match(text, @"cell(?:\s+ID)?\s+(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
            return match.Success ? match.Groups[1].Value : null;
        }
        catch (RegexMatchTimeoutException) { return null; }
    }

    public static string CodeActivityStatus(string status, string body, int? exitCode)
    {
        if (exitCode is int code && code != 0) return "failed";
        if (status == "failed") return "failed";
        if (body.Contains("terminat", StringComparison.OrdinalIgnoreCase)) return "unconfirmed";
        if (status is "unconfirmed" or "blocked") return status;
        if (RunningCellNotice(body)) return "in_progress";
        return status.Length == 0 ? "in_progress" : status;
    }

    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        try { return Secret.Replace(text, "$1 [redacted]"); }
        catch (RegexMatchTimeoutException) { return "[diagnostic redacted]"; }
    }

    private static bool IsStartupFailure(string status, int? exit, long? durationMs, string evidence, string source)
    {
        if (status != "failed") return false;
        if (evidence.Contains("CreateProcessAsUserW", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("Failed to create unified exec", StringComparison.OrdinalIgnoreCase)) return true;
        if (exit == -1 && durationMs is 0 or null && source.Contains("startup", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsAccessDenied(string evidence) =>
        evidence.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("CreateProcessAsUserW failed: 5", StringComparison.OrdinalIgnoreCase);

    private static bool IsNoMatch(string command, string evidence, int? exit)
    {
        if (exit is not 1 || IsAccessDenied(evidence) || evidence.Contains("CreateProcess", StringComparison.OrdinalIgnoreCase)) return false;
        if (!LooksLikeSearch(command)) return false;
        var body = evidence.Trim();
        return body.Length == 0 || body.Contains("no matches", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeSearch(string command)
    {
        foreach (var token in command.Split([' ', '\t', '\r', '\n', ';', '|'], StringSplitOptions.RemoveEmptyEntries))
        {
            var name = Path.GetFileNameWithoutExtension(token.Trim('"'));
            if (name.Equals("rg", StringComparison.OrdinalIgnoreCase)
                || name.Equals("grep", StringComparison.OrdinalIgnoreCase)
                || name.Equals("findstr", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Select-String", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string CommandText(JsonElement item)
    {
        if (!item.TryGetProperty("command", out var command)) return "";
        if (command.ValueKind == JsonValueKind.String) return command.GetString() ?? "";
        if (command.ValueKind != JsonValueKind.Array) return "";
        return string.Join(" ", command.EnumerateArray().Where(part => part.ValueKind == JsonValueKind.String).Select(part => part.GetString()).Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string? Text(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static int? Integer(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number) return null;
        return value.TryGetInt32(out var number) ? number : null;
    }

    private static long? DurationMs(JsonElement item)
    {
        var direct = Integer(item, "durationMs");
        if (direct is not null) return direct;
        if (!item.TryGetProperty("duration", out var duration) || duration.ValueKind != JsonValueKind.Object) return null;
        if (!duration.TryGetProperty("secs", out var secs) || secs.ValueKind != JsonValueKind.Number || !secs.TryGetInt64(out var seconds)) return null;
        return seconds * 1000;
    }

    private static string Combine(string stderr, string stdout)
    {
        if (stderr.Length == 0) return stdout;
        if (stdout.Length == 0 || stderr.Contains(stdout, StringComparison.Ordinal)) return stderr;
        if (stdout.Contains(stderr, StringComparison.Ordinal)) return stdout;
        return stderr + "\n" + stdout;
    }

    private static string Clip(string value, int limit) => value.Length <= limit ? value : value[..limit];
}
