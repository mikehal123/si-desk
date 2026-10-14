using System.Globalization;
using System.Text.Json;

namespace Sovereign.Desktop.Client;

public enum GrokTuiStyle { Plain, Bold, Heading, InlineCode, Bullet, CodeBlock }

public enum GrokTuiKeyAction { None, Send, NewLine, Stop, Recall, ClearView }

public sealed record GrokTuiUserBar(string Text, string Time);

public sealed record GrokTuiRun(string Text, GrokTuiStyle Style);

public sealed record GrokTuiBlock(GrokTuiStyle Style, IReadOnlyList<GrokTuiRun> Runs);

public sealed record GrokTuiStatus(string Left, string Right, string Stop);

/// <summary>Pure Grok TUI text. No WinUI, so tests can call it directly.</summary>
public static class GrokTuiPresentation
{
    private const string Spinner = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";

    public static GrokTuiUserBar UserBar(string text, DateTimeOffset at) =>
        new(text, at.ToString("HH:mm", CultureInfo.InvariantCulture));

    public static IReadOnlyList<GrokTuiBlock> Markdown(string source)
    {
        if (string.IsNullOrEmpty(source)) return [];
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var blocks = new List<GrokTuiBlock>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                var inner = new List<string>();
                i++;
                for (; i < lines.Length && !lines[i].StartsWith("```", StringComparison.Ordinal); i++)
                    inner.Add(lines[i]);
                blocks.Add(new GrokTuiBlock(GrokTuiStyle.CodeBlock, [new GrokTuiRun(string.Join('\n', inner), GrokTuiStyle.CodeBlock)]));
                continue;
            }

            if (string.IsNullOrWhiteSpace(line)) continue;
            if (HeadingText(line, out var heading))
            {
                blocks.Add(new GrokTuiBlock(GrokTuiStyle.Heading, [new GrokTuiRun(heading, GrokTuiStyle.Heading)]));
                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                blocks.Add(new GrokTuiBlock(GrokTuiStyle.Bullet, Inline(line[2..])));
                continue;
            }

            blocks.Add(new GrokTuiBlock(GrokTuiStyle.Plain, Inline(line)));
        }

        return blocks;
    }

    public static string ToolLine(string title, string status, string kind) =>
        Glyph(status) + " " + ToolBody(title, kind);

    public static string WorkedFor(TimeSpan elapsed)
    {
        var seconds = Math.Max(0, (int)Math.Round(elapsed.TotalSeconds));
        return "Worked for " + seconds.ToString(CultureInfo.InvariantCulture) + "s";
    }

    public static GrokTuiStatus Status(TimeSpan elapsed, int? tokens, int frame)
    {
        var index = frame % Spinner.Length;
        if (index < 0) index += Spinner.Length;
        var seconds = elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        var left = Spinner[index] + " Main agent waiting… " + seconds + "s";
        var right = tokens is int count
            ? count.ToString("N0", CultureInfo.InvariantCulture) + " tok"
            : "—";
        return new GrokTuiStatus(left, right, "[stop]");
    }

    public static string ContextLabel(long? usedTokens, long limitTokens) =>
        Compact(usedTokens) + " / " + Compact(limitTokens);

    public static long ContextLimit(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return 256000;
        const string marker = "context=";
        for (var i = 0; i <= modelId.Length - marker.Length; i++)
        {
            if (!modelId.AsSpan(i, marker.Length).Equals(marker, StringComparison.OrdinalIgnoreCase)) continue;
            var start = i + marker.Length;
            var end = start;
            while (end < modelId.Length && char.IsAsciiDigit(modelId[end])) end++;
            if (end == start || end >= modelId.Length || modelId[end] is not 'k' and not 'K') continue;
            if (long.TryParse(modelId.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out var thousands))
                return thousands * 1000;
        }

        return 256000;
    }

    public static string Approval(bool plan, bool alwaysAllow)
    {
        if (plan) return "plan";
        return alwaysAllow ? "always approve" : "always ask";
    }

    public static string Footer(string? model, string approval)
    {
        var name = string.IsNullOrWhiteSpace(model) ? "Grok" : model.Trim();
        return name + " · " + approval;
    }

    /// <summary>One transcript line for an unsent instruction. Empty text is an attachment-only draft.</summary>
    public static string PendingPreview(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Attachment";
        var line = text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ').Trim();
        while (line.Contains("  ", StringComparison.Ordinal)) line = line.Replace("  ", " ", StringComparison.Ordinal);
        const int max = 72;
        return line.Length <= max ? line : line[..(max - 1)] + "…";
    }

    /// <summary>Short status for the review strip. Unknown text is kept so a new state still shows.</summary>
    public static string PendingStatus(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return "";
        if (detail.Contains("Interrupted", StringComparison.Ordinal)) return "your message · interrupted · not resent";
        if (detail.Contains("Failed", StringComparison.Ordinal)) return "your message · failed · send again";
        if (detail.Contains("Not sent", StringComparison.Ordinal)) return "your message · not sent";
        if (detail.Contains("Accepted · queued", StringComparison.Ordinal) || detail.Contains("Queued", StringComparison.Ordinal))
            return "your message · queued for the main agent";
        if (detail.Contains("Waiting", StringComparison.Ordinal)) return "your message · waiting on the main agent";
        if (detail.Contains("Accepted", StringComparison.Ordinal)) return "your message · waiting to send";
        return detail;
    }

    public static GrokTuiKeyAction Key(string key, bool shift, bool control)
    {
        if (string.Equals(key, "Enter", StringComparison.OrdinalIgnoreCase))
            return shift ? GrokTuiKeyAction.NewLine : GrokTuiKeyAction.Send;
        if (string.Equals(key, "Escape", StringComparison.OrdinalIgnoreCase)) return GrokTuiKeyAction.Stop;
        if (string.Equals(key, "Up", StringComparison.OrdinalIgnoreCase)) return GrokTuiKeyAction.Recall;
        if (control && string.Equals(key, "L", StringComparison.OrdinalIgnoreCase)) return GrokTuiKeyAction.ClearView;
        return GrokTuiKeyAction.None;
    }

    private static bool HeadingText(string line, out string text)
    {
        if (line.StartsWith("### ", StringComparison.Ordinal)) { text = line[4..]; return true; }
        if (line.StartsWith("## ", StringComparison.Ordinal)) { text = line[3..]; return true; }
        if (line.StartsWith("# ", StringComparison.Ordinal)) { text = line[2..]; return true; }
        text = "";
        return false;
    }

    private static List<GrokTuiRun> Inline(string text)
    {
        var runs = new List<GrokTuiRun>();
        var plainStart = 0;
        var i = 0;
        while (i < text.Length)
        {
            if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
            {
                var close = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (close > i + 2)
                {
                    AppendPlain(runs, text, plainStart, i);
                    runs.Add(new GrokTuiRun(text[(i + 2)..close], GrokTuiStyle.Bold));
                    i = close + 2;
                    plainStart = i;
                    continue;
                }
            }

            if (text[i] == '`')
            {
                var close = text.IndexOf('`', i + 1);
                if (close > i + 1)
                {
                    AppendPlain(runs, text, plainStart, i);
                    runs.Add(new GrokTuiRun(text[(i + 1)..close], GrokTuiStyle.InlineCode));
                    i = close + 1;
                    plainStart = i;
                    continue;
                }
            }

            i++;
        }

        AppendPlain(runs, text, plainStart, text.Length);
        return runs;
    }

    private static void AppendPlain(List<GrokTuiRun> runs, string text, int start, int end)
    {
        if (end > start) AppendPaths(runs, text[start..end]);
    }

    private static void AppendPaths(List<GrokTuiRun> runs, string text)
    {
        var start = 0;
        var i = 0;
        while (i < text.Length)
        {
            var length = PathTokenLength(text, i);
            if (length > 0)
            {
                if (i > start) runs.Add(new GrokTuiRun(text[start..i], GrokTuiStyle.Plain));
                runs.Add(new GrokTuiRun(text[i..(i + length)], GrokTuiStyle.InlineCode));
                i += length;
                start = i;
                continue;
            }

            i++;
        }

        if (start < text.Length) runs.Add(new GrokTuiRun(text[start..], GrokTuiStyle.Plain));
    }

    private static int PathTokenLength(string text, int index)
    {
        var windows = WindowsPathLength(text, index);
        return windows > 0 ? windows : UnixPathLength(text, index);
    }

    private static int WindowsPathLength(string text, int index)
    {
        if ((uint)index + 3 >= (uint)text.Length) return 0;
        if (!char.IsAsciiLetter(text[index]) || text[index + 1] != ':' || text[index + 2] != '\\') return 0;
        if (index > 0 && char.IsAsciiLetterOrDigit(text[index - 1])) return 0;
        var end = index + 3;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        while (end > index + 3 && IsTrailingPunctuation(text[end - 1])) end--;
        return end > index + 3 ? end - index : 0;
    }

    private static int UnixPathLength(string text, int index)
    {
        if (text[index] != '/') return 0;
        if (index > 0 && !IsPathBoundary(text[index - 1])) return 0;
        var end = index;
        var segments = 0;
        while (end < text.Length && text[end] == '/')
        {
            var next = end + 1;
            if (next >= text.Length || !IsUnixPathChar(text[next])) break;
            while (next < text.Length && IsUnixPathChar(text[next])) next++;
            segments++;
            end = next;
        }

        if (segments < 2) return 0;
        while (end > index && IsTrailingPunctuation(text[end - 1])) end--;
        return HasTwoUnixSegments(text, index, end) ? end - index : 0;
    }

    private static bool HasTwoUnixSegments(string text, int index, int end)
    {
        var segments = 0;
        var i = index;
        while (i < end && text[i] == '/')
        {
            var next = i + 1;
            if (next >= end || !IsUnixPathChar(text[next])) break;
            while (next < end && IsUnixPathChar(text[next])) next++;
            segments++;
            i = next;
        }

        return segments >= 2 && i == end;
    }

    private static bool IsPathBoundary(char value) =>
        char.IsWhiteSpace(value) || value is '(' or '[' or '{' or '"' or '\'' or '`';

    private static bool IsUnixPathChar(char value) =>
        char.IsAsciiLetterOrDigit(value) || value is '.' or '_' or '-';

    private static bool IsTrailingPunctuation(char value) =>
        value is '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '}' or '"' or '\'';

    private static string Glyph(string status)
    {
        if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "done", StringComparison.OrdinalIgnoreCase))
            return "✓";
        if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "error", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "blocked", StringComparison.OrdinalIgnoreCase))
            return "✗";
        return "◇";
    }

    private static string ToolBody(string title, string kind)
    {
        if (string.Equals(kind, "search", StringComparison.OrdinalIgnoreCase)) return Searched(title);
        if (string.Equals(kind, "execute", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "shell", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "command", StringComparison.OrdinalIgnoreCase))
            return "Ran " + title;
        if (string.Equals(kind, "edit", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "write", StringComparison.OrdinalIgnoreCase))
            return "Edited " + title;
        if (string.Equals(kind, "read", StringComparison.OrdinalIgnoreCase)) return "Read " + title;
        return title;
    }

    private static string Searched(string title)
    {
        var count = FirstInteger(title);
        var noun = count == 1 ? "pattern" : "patterns";
        return "Searched " + count.ToString(CultureInfo.InvariantCulture) + " " + noun;
    }

    private static int FirstInteger(string title)
    {
        for (var i = 0; i < title.Length; i++)
        {
            if (!char.IsAsciiDigit(title[i])) continue;
            var end = i + 1;
            while (end < title.Length && char.IsAsciiDigit(title[end])) end++;
            return int.TryParse(title.AsSpan(i, end - i), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 1;
        }

        return 1;
    }

    private static string Compact(long? tokens) => tokens is long value ? Compact(value) : "—";

    private static string Compact(long tokens)
    {
        if (tokens < 1000) return tokens.ToString(CultureInfo.InvariantCulture);
        var thousands = (long)Math.Round(tokens / 1000d, MidpointRounding.AwayFromZero);
        return thousands.ToString(CultureInfo.InvariantCulture) + "K";
    }
}

/// <summary>Classic chat toggle. Missing or unreadable settings stay on. An explicit enabled flag is kept.</summary>
public static class ClassicChatPreference
{
    public static bool Load(string profile)
    {
        try
        {
            var path = Path.Combine(profile, "classic-chat.json");
            if (!File.Exists(path)) return true;
            using var body = JsonDocument.Parse(File.ReadAllText(path));
            if (body.RootElement.ValueKind != JsonValueKind.Object) return false;
            foreach (var name in new[] { "enabled", "Enabled" })
                if (body.RootElement.TryGetProperty(name, out var enabled) && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    return enabled.GetBoolean();
            return false;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        { return true; }
    }

    public static void Save(string profile, bool enabled)
    {
        Directory.CreateDirectory(profile);
        var path = Path.Combine(profile, "classic-chat.json");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, "{\"enabled\":" + (enabled ? "true" : "false") + "}");
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
