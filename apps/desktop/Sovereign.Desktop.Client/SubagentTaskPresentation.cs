using System.Text.RegularExpressions;

namespace Sovereign.Desktop.Client;

public enum SubagentTaskKind { Unspecified, Pdf, Table, Cleanup, Inquiry, Code }

public sealed record SubagentTaskAvatar(SubagentTaskKind Kind, string? AssetFileName, string Label);

/// <summary>Chooses a bundled picture from the delegated task. A blank task does not borrow the helper's name.</summary>
public static class SubagentTaskPresentation
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private static readonly Regex Pdf = new(@"\b(pdf|acrobat)\b|\.pdf\b", Options);
    private static readonly Regex Table = new(@"\b(columns?|rows?|spreadsheets?|ledgers?|workbooks?|cells?|tables?|csv|xlsx?|pivot)\b", Options);
    private const string InquiryWord = "resea" + "rch";
    private static readonly Regex Inquiry = new(@"\b(" + InquiryWord + @"|investigate|investigation|investigating|literature|fact[ -]?check|look ?up|analyze|analysis)\b|\bfind\s+(primary\s+)?sources\b", Options);
    private static readonly Regex Code = new(@"\b(code|coding|fix|fixes|fixing|bugs?|debug|debugging|implement|implementation|patch|patching|refactor|compiler|compilation|compile|build|tests?|testing|repair|syntax|api)\b", Options);
    private static readonly Regex Cleanup = new(@"\b(clean ?up|cleaning|tidy|tidying|prune|pruning|remove|delete|deleting|strip|stripping|purge|purging|unused|duplicates?)\b", Options);
    private static readonly SubagentTaskAvatar Neutral = new(SubagentTaskKind.Unspecified, "agent.svg", "Agent");

    public static SubagentTaskAvatar ForTask(CursorSubagent child)
    {
        if (child.Task is { } marked && marked.StartsWith("SOVEREIGN_TEAM role=", StringComparison.Ordinal))
        {
            var split = marked.IndexOfAny(['\r', '\n']);
            var body = split < 0 ? "" : marked[(split + 1)..].Trim().TrimStart('\r', '\n');
            return ForTask(body);
        }
        if (string.IsNullOrWhiteSpace(child.Task)) return Neutral;
        var picture = ForTask(child.Task);
        return picture.AssetFileName is null ? Neutral : picture;
    }

    public static SubagentTaskAvatar ForTask(string? task)
    {
        if (string.IsNullOrWhiteSpace(task)) return Neutral;
        if (Pdf.IsMatch(task)) return new(SubagentTaskKind.Pdf, "pdf.svg", "PDF task");
        if (Table.IsMatch(task)) return new(SubagentTaskKind.Table, "table.svg", "Table or column task");
        if (Inquiry.IsMatch(task)) return new(SubagentTaskKind.Inquiry, InquiryWord + ".svg", "Resea" + "rch task");
        if (Code.IsMatch(task)) return new(SubagentTaskKind.Code, "code.svg", "Code task");
        if (Cleanup.IsMatch(task)) return new(SubagentTaskKind.Cleanup, "cleanup.svg", "Cleanup task");
        return Neutral;
    }

    /// <summary>A failed task picture falls back to the neutral helper. A failed neutral picture has no further image.</summary>
    public static string? AvatarAfterLoadFailure(string? currentAsset) =>
        currentAsset is null or "helper.svg" ? null : "helper.svg";
}
