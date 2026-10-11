using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sovereign.Desktop.Client;

/// <summary>Line comments on plans and diffs. Original to this app; no third-party review code.</summary>
public static class LineReview
{
    public const string ApproveAllMessage = "Approved. No line changes requested.";

    public static bool ShowButton(bool settingOn, bool hasPlan, bool hasFileEdits) =>
        settingOn && (hasPlan || hasFileEdits);

    public static ReviewDocument[] Documents(IEnumerable<CursorPlanItem> plan, IEnumerable<CursorFileChange> changes)
    {
        var documents = new List<ReviewDocument>();
        var steps = plan.ToArray();
        if (steps.Length > 0) documents.Add(Plan(steps));
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            var id = change.Path;
            var suffix = 2;
            while (!used.Add(id)) id = change.Path + "#" + suffix++.ToString(CultureInfo.InvariantCulture);
            documents.Add(File(change, id));
        }
        return documents.ToArray();
    }

    public static ReviewDocument Plan(IReadOnlyList<CursorPlanItem> items)
    {
        var lines = new List<ReviewLine>();
        var number = 1;
        foreach (var item in items)
        {
            var parts = SplitLines(item.Content);
            if (parts.Length == 0) parts = [""];
            for (var i = 0; i < parts.Length; i++)
            {
                var text = i == 0 ? item.Status + "  " + parts[i] : parts[i];
                lines.Add(new ReviewLine(number++, "plan", text, null));
            }
        }
        return new ReviewDocument("plan", "plan", "Plan", Cap(lines));
    }

    public static ReviewDocument File(CursorFileChange change, string id)
    {
        var lines = !string.IsNullOrEmpty(change.UnifiedDiff)
            ? ParseUnified(change.UnifiedDiff)
            : DiffText(change.Before ?? "", change.After);
        if (lines.Length == 0)
            lines = [new ReviewLine(1, "context", "No line changes in the reported snapshot.", null)];
        return new ReviewDocument(id, "file", change.Path, lines);
    }

    public static string Message(LineReviewDraft draft, bool approveAll) =>
        approveAll ? ApproveAllMessage : draft.Format();

    public static SavedChat Remember(SavedChat chat, SavedLineReview review)
    {
        var kept = (chat.LineReviews ?? []).Where(item => item.SourceId != review.SourceId || item.TurnIndex != review.TurnIndex).ToList();
        kept.Add(review);
        return chat with { LineReviews = kept.ToArray() };
    }

    public static SavedLineReview? Recall(SavedChat chat, string sourceId, int turnIndex) =>
        (chat.LineReviews ?? []).LastOrDefault(item => item.SourceId == sourceId && item.TurnIndex == turnIndex);

    public static SavedLineReview[]? Sanitize(SavedLineReview[]? reviews)
    {
        if (reviews is null || reviews.Length == 0) return null;
        var kept = new List<SavedLineReview>();
        foreach (var review in reviews)
        {
            if (!TryClean(review, out var clean)) continue;
            kept.RemoveAll(item => item.SourceId == clean.SourceId && item.TurnIndex == clean.TurnIndex);
            kept.Add(clean);
        }
        return kept.Count == 0 ? null : kept.ToArray();
    }

    public static ReviewLine[] DiffText(string before, string after)
    {
        var older = SplitLines(before);
        var newer = SplitLines(after);
        var truncated = false;
        if (older.Length > 2000 || newer.Length > 2000)
        {
            older = older.Take(2000).ToArray();
            newer = newer.Take(2000).ToArray();
            truncated = true;
        }
        var matches = LongestCommon(older, newer);
        var lines = new List<ReviewLine>();
        var left = 0;
        var right = 0;
        var number = 1;
        string? hunk = null;
        var hunkNumber = 0;
        void OpenHunk()
        {
            if (hunk is not null) return;
            hunkNumber++;
            hunk = "h" + hunkNumber.ToString(CultureInfo.InvariantCulture);
        }
        foreach (var (ai, bi) in matches)
        {
            while (left < ai)
            {
                OpenHunk();
                lines.Add(new ReviewLine(number++, "removed", older[left], hunk));
                left++;
            }
            while (right < bi)
            {
                OpenHunk();
                lines.Add(new ReviewLine(number++, "added", newer[right], hunk));
                right++;
            }
            hunk = null;
            lines.Add(new ReviewLine(number++, "context", newer[right], null));
            left++;
            right++;
        }
        while (left < older.Length)
        {
            OpenHunk();
            lines.Add(new ReviewLine(number++, "removed", older[left], hunk));
            left++;
        }
        while (right < newer.Length)
        {
            OpenHunk();
            lines.Add(new ReviewLine(number++, "added", newer[right], hunk));
            right++;
        }
        if (truncated) lines.Add(new ReviewLine(number, "context", "Diff display stopped after 2000 lines.", null));
        return Cap(lines);
    }

    private static ReviewLine[] ParseUnified(string patch)
    {
        var lines = new List<ReviewLine>();
        var number = 1;
        string? hunk = null;
        var hunkNumber = 0;
        foreach (var raw in SplitLines(patch))
        {
            if (raw.StartsWith("@@", StringComparison.Ordinal))
            {
                hunkNumber++;
                hunk = "h" + hunkNumber.ToString(CultureInfo.InvariantCulture);
                continue;
            }
            if (raw.StartsWith("diff ", StringComparison.Ordinal) || raw.StartsWith("index ", StringComparison.Ordinal)
                || raw.StartsWith("--- ", StringComparison.Ordinal) || raw.StartsWith("+++ ", StringComparison.Ordinal)
                || raw.StartsWith("new file", StringComparison.Ordinal) || raw.StartsWith("deleted file", StringComparison.Ordinal)
                || raw.StartsWith('\\'))
                continue;
            if (hunk is null) continue;
            var kind = "context";
            var text = raw;
            if (raw.StartsWith('+')) { kind = "added"; text = raw[1..]; }
            else if (raw.StartsWith('-')) { kind = "removed"; text = raw[1..]; }
            else if (raw.StartsWith(' ')) text = raw[1..];
            lines.Add(new ReviewLine(number++, kind, text, hunk));
        }
        return Cap(lines);
    }

    private static ReviewLine[] Cap(List<ReviewLine> lines)
    {
        if (lines.Count <= 1500) return lines.ToArray();
        var kept = lines.Take(1500).ToList();
        kept.Add(new ReviewLine(1501, "context", "Further lines were not loaded into this review.", null));
        return kept.ToArray();
    }

    private static string[] SplitLines(string text)
    {
        if (text.Length == 0) return [];
        var parts = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        if (parts.Length > 0 && parts[^1].Length == 0) parts = parts[..^1];
        return parts;
    }

    private static List<(int A, int B)> LongestCommon(string[] older, string[] newer)
    {
        var width = older.Length;
        var height = newer.Length;
        var scores = new int[width + 1, height + 1];
        for (var i = width - 1; i >= 0; i--)
        {
            for (var j = height - 1; j >= 0; j--)
                scores[i, j] = older[i] == newer[j] ? scores[i + 1, j + 1] + 1 : Math.Max(scores[i + 1, j], scores[i, j + 1]);
        }
        var pairs = new List<(int, int)>();
        var x = 0;
        var y = 0;
        while (x < width && y < height)
        {
            if (older[x] == newer[y]) { pairs.Add((x, y)); x++; y++; }
            else if (scores[x + 1, y] >= scores[x, y + 1]) x++;
            else y++;
        }
        return pairs;
    }

    private static bool TryClean(SavedLineReview? review, out SavedLineReview clean)
    {
        clean = null!;
        if (review is null || !Guid.TryParseExact(review.Id, "N", out _) || !ValidSource(review.SourceId) || review.TurnIndex < -1)
            return false;
        if (review.SentMessage is null || review.SentMessage.Length > 100_000) return false;
        var notes = new List<LineReviewNote>();
        foreach (var note in review.Notes ?? [])
        {
            if (note is null || note.Line < 1 || note.Line > 200_000) continue;
            if (note.Target is not ("plan" or "file")) continue;
            if (note.Action is not ("comment" or "change this" or "drop this")) continue;
            if (!ValidToken(note.DocumentId, 240) || note.Note is null || note.Note.Length > 4000) continue;
            if (note.Target == "file" && !ValidToken(note.File, 240)) continue;
            if (note.Hunk is not null && !ValidToken(note.Hunk, 40)) continue;
            notes.Add(note with { Note = OneLine(note.Note) });
        }
        var files = new List<LineReviewFileDecision>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in review.Files ?? [])
        {
            if (file is null || file.Decision is not ("approve" or "reject") || !ValidToken(file.File, 240)) continue;
            if (!seen.Add(file.File)) files.RemoveAll(item => string.Equals(item.File, file.File, StringComparison.OrdinalIgnoreCase));
            files.Add(file);
        }
        if (notes.Count == 0 && files.Count == 0 && review.SentMessage.Length == 0) return false;
        clean = review with { Notes = notes.ToArray(), Files = files.ToArray() };
        return true;
    }

    private static bool ValidSource(string? value) => ValidToken(value, 160);
    private static bool ValidToken(string? value, int max) =>
        value is { Length: > 0 } && value.Length <= max && !value.Contains('\n') && !value.Contains('\r');

    internal static string OneLine(string text) =>
        text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ').Trim();
}

public sealed record ReviewLine(int Number, string Kind, string Text, string? Hunk);
public sealed record ReviewDocument(string Id, string Kind, string Title, ReviewLine[] Lines);

public sealed record LineReviewNote(
    string DocumentId,
    string Target,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? File,
    int Line,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Hunk,
    string Action,
    string Note);

public sealed record LineReviewFileDecision(string File, string Decision);

public sealed record SavedLineReview(
    string Id,
    string SourceId,
    int TurnIndex,
    LineReviewNote[] Notes,
    LineReviewFileDecision[] Files,
    string SentMessage,
    bool ReadOnly);

public sealed class LineReviewDraft
{
    private readonly List<LineReviewNote> _notes = [];
    private readonly List<LineReviewFileDecision> _files = [];

    public string Id { get; }
    public string SourceId { get; }
    public int TurnIndex { get; }
    public IReadOnlyList<ReviewDocument> Documents { get; }
    public IReadOnlyList<LineReviewNote> Notes => _notes;
    public IReadOnlyList<LineReviewFileDecision> Files => _files;

    public LineReviewDraft(string sourceId, int turnIndex, IReadOnlyList<ReviewDocument> documents)
        : this(Guid.NewGuid().ToString("N"), sourceId, turnIndex, documents) { }

    public LineReviewDraft(string id, string sourceId, int turnIndex, IReadOnlyList<ReviewDocument> documents)
    {
        Id = id;
        SourceId = sourceId;
        TurnIndex = turnIndex;
        Documents = documents;
    }

    public static LineReviewDraft FromSaved(SavedLineReview saved, IReadOnlyList<ReviewDocument> documents)
    {
        var draft = new LineReviewDraft(saved.Id, saved.SourceId, saved.TurnIndex, documents);
        foreach (var note in saved.Notes ?? [])
            if (draft.Documents.Any(document => document.Id == note.DocumentId && document.Lines.Any(line => line.Number == note.Line)))
                draft._notes.Add(note);
        foreach (var file in saved.Files ?? []) draft.Decide(file.File, file.Decision);
        return draft;
    }

    public LineReviewNote Capture(string documentId, int line, string action, string note, bool wholeHunk)
    {
        if (action is not ("comment" or "change this" or "drop this"))
            throw new ArgumentOutOfRangeException(nameof(action), "Use comment, change this, or drop this.");
        var document = Documents.FirstOrDefault(item => item.Id == documentId)
            ?? throw new ArgumentOutOfRangeException(nameof(documentId), "That review target is not open.");
        var selected = document.Lines.FirstOrDefault(item => item.Number == line)
            ?? throw new ArgumentOutOfRangeException(nameof(line), "That line is not in the review.");
        var numbers = wholeHunk && selected.Hunk is { } hunk
            ? document.Lines.Where(item => item.Hunk == hunk).Select(item => item.Number).ToArray()
            : [line];
        LineReviewNote last = null!;
        foreach (var number in numbers)
        {
            _notes.RemoveAll(item => item.DocumentId == documentId && item.Line == number);
            var source = document.Lines.First(item => item.Number == number);
            last = new LineReviewNote(document.Id, document.Kind, document.Kind == "file" ? document.Title : null,
                number, source.Hunk, action, LineReview.OneLine(note));
            _notes.Add(last);
        }
        return last;
    }

    public void Decide(string file, string decision)
    {
        if (decision is not ("approve" or "reject"))
            throw new ArgumentOutOfRangeException(nameof(decision), "Use approve or reject.");
        if (string.IsNullOrWhiteSpace(file) || file.Contains('\n') || file.Contains('\r'))
            throw new ArgumentOutOfRangeException(nameof(file), "The file path is not valid for a review.");
        _files.RemoveAll(item => string.Equals(item.File, file, StringComparison.OrdinalIgnoreCase));
        _files.Add(new LineReviewFileDecision(file, decision));
    }

    public string Format()
    {
        var text = new StringBuilder();
        text.Append("Line review\nRevise only what is flagged. Leave every other line unchanged.");
        if (!string.Equals(SourceId, "main", StringComparison.Ordinal))
            text.Append("\n\nsource: ").Append(SourceId);
        if (_notes.Count == 0 && _files.Count == 0)
        {
            text.Append("\n\nNo lines flagged.");
            return text.ToString();
        }
        foreach (var note in _notes)
        {
            text.Append("\n\ntarget: ").Append(note.Target);
            if (note.File is not null) text.Append("\nfile: ").Append(note.File);
            text.Append("\nline: ").Append(note.Line.ToString(CultureInfo.InvariantCulture));
            if (note.Hunk is not null) text.Append("\nhunk: ").Append(note.Hunk);
            text.Append("\naction: ").Append(note.Action);
            text.Append("\nnote: ").Append(note.Note);
        }
        foreach (var file in _files.OrderBy(item => item.File, StringComparer.OrdinalIgnoreCase))
        {
            text.Append("\n\ntarget: file\nfile: ").Append(file.File);
            text.Append("\ndecision: ").Append(file.Decision);
        }
        return text.ToString();
    }

    public SavedLineReview ToSaved(string sentMessage, bool readOnly) =>
        new(Id, SourceId, TurnIndex, _notes.ToArray(), _files.ToArray(), sentMessage, readOnly);

    public void ApproveAllFiles()
    {
        foreach (var document in Documents.Where(item => item.Kind == "file"))
            Decide(document.Title, "approve");
    }
}

public static class LineReviewPreference
{
    public static bool Load(string profile)
    {
        try
        {
            var path = Path.Combine(profile, "line-review.json");
            if (!File.Exists(path)) return true;
            using var body = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var name in new[] { "enabled", "Enabled" })
                if (body.RootElement.TryGetProperty(name, out var enabled) && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    return enabled.GetBoolean();
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        { return true; }
    }

    public static void Save(string profile, bool enabled)
    {
        Directory.CreateDirectory(profile);
        var path = Path.Combine(profile, "line-review.json");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, "{\"enabled\":" + (enabled ? "true" : "false") + "}");
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
