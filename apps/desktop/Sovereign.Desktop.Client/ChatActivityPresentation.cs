using System.Text;

namespace Sovereign.Desktop.Client;

/// <summary>Text for the existing activity projection. Never executes or authorizes tools.</summary>
public static class ChatActivityPresentation
{
    public static string Category(string kind) => kind switch
    {
        "read" => "Reads", "search" => "Searches", "execute" => "Commands",
        "edit" => "Edits", "fetch" => "Sources", _ => "Other tools"
    };

    public static string GroupLabel(string category, int count) => category switch
    {
        "Reads" => $"Read files · {count} steps",
        "Searches" => $"Searched · {count} steps",
        "Commands" => $"Ran {count} " + (count == 1 ? "command" : "commands"),
        "Edits" => $"Edited files · {count} steps",
        "Sources" => $"Read sources · {count} steps",
        _ => $"Other tools · {count} steps"
    };

    // A preview of provider text, not a second model call or an invented explanation.
    public static string ReasoningPreview(string text, int maxCharacters = 420)
    {
        if (maxCharacters < 1) throw new ArgumentOutOfRangeException(nameof(maxCharacters));
        var preview = new StringBuilder();
        var space = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune)) { space = preview.Length > 0; continue; }
            var width = rune.Utf16SequenceLength + (space ? 1 : 0);
            if (preview.Length + width > maxCharacters) return preview + "…";
            if (space) preview.Append(' ');
            space = false;
            preview.Append(rune.ToString());
        }
        return preview.ToString();
    }

    public static string Duration(TimeSpan elapsed) => elapsed.TotalMinutes >= 1
        ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s" : $"{Math.Max(0, (int)elapsed.TotalSeconds)}s";

    public static string Completion(TimeSpan elapsed, long steps, string outcome, bool restored) =>
        (restored ? "Saved work" : "Worked " + Duration(elapsed))
        + $" · {steps} " + (steps == 1 ? "step" : "steps")
        + (outcome == "completed" ? "" : " · " + outcome);
}
