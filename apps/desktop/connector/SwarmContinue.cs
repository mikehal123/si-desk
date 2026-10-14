using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Sovereign.Desktop.Core;

namespace Sovereign.Desktop.Connector;

public sealed record SwarmContinueResult(string Outcome, string Text, bool Resume)
{
    public string ToolText() => Outcome == "ignored" ? "ignored" : Outcome + "\t" + Text;
}

public sealed record SwarmReviewDelivery(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("resume")] bool Resume,
    [property: JsonPropertyName("review_id")] int ReviewId);

/// <summary>
/// Posts a Grokbot handoff verdict into the chat that owns the run.
/// Continue and fix resume that chat. A closed chat keeps a queue record until it opens.
/// Does not write current.txt.
/// </summary>
public static class SwarmContinue
{
    private static readonly object Gate = new();
    private static readonly Regex UtcStamp = new(
        "[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions QueueJson = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true
    };

    public static SwarmContinueResult Apply(IGrokBotDesk desk, string? swarmId, string? runId, string? verdict, string? notes, string? dataDirectory = null, DateTimeOffset? reviewedAt = null)
    {
        ArgumentNullException.ThrowIfNull(desk);
        var canonical = Canonical(verdict) ?? throw new ArgumentException("Verdict must be continue, fix, or done.");
        if (string.IsNullOrWhiteSpace(swarmId)) throw new ArgumentException("Swarm id is required.");
        if (string.IsNullOrWhiteSpace(runId)) throw new ArgumentException("Run id is required.");
        var swarm = swarmId.Trim();
        var path = SwarmBoard.RunBoardPath(swarm, runId, desk.SwarmBoardRoot());
        if (!File.Exists(path)) throw new KeyNotFoundException("That swarm board is not on disk.");
        if (!desk.GrokbotReviewsHandoffs(swarm))
            throw new InvalidOperationException("Grokbot reviews handoffs is off.");
        var open = desk.ConversationOpen(swarm);
        string message;
        var resume = false;
        lock (Gate)
        {
            var board = SwarmBoard.Read(path);
            if (board.ReviewLoops < 1)
                throw new InvalidOperationException("No handoff review is open.");
            if (board.ReviewHandled == board.ReviewLoops)
                return new SwarmContinueResult("ignored", "", false);
            message = Message(canonical, notes);
            resume = canonical is "continue" or "fix";
            if (!open)
                _ = ReadQueue(QueuePath(swarm, dataDirectory));
            string[] reopened = [];
            if (canonical == "fix")
            {
                reopened = board.Tasks
                    .Where(task => task is not null && SwarmSection.StatusLabel(task.Status) == SwarmStatus.Review)
                    .Select(task => task.Id)
                    .ToArray();
                if (reopened.Length == 0)
                    throw new InvalidOperationException("No task is in review.");
            }
            WriteVerdict(board, canonical, notes, reviewedAt);
            board.ReviewHandled = board.ReviewLoops;
            if (canonical == "fix")
            {
                board.ReviewNote = NoteWithReopened(board.ReviewNote, reopened);
                foreach (var id in reopened)
                    board.Respawn(id, message);
            }
            else
                board.Save();
            if (!open)
            {
                var queuePath = QueuePath(swarm, dataDirectory);
                var items = ReadQueue(queuePath);
                items.Add(new SwarmReviewDelivery(message, resume, board.ReviewLoops));
                WriteQueue(queuePath, items);
                return new SwarmContinueResult("queued", message, resume);
            }
        }
        desk.DeliverSwarmReview(swarm, message, resume);
        return new SwarmContinueResult("posted", message, resume);
    }

    public static bool Repair(IGrokBotDesk desk, string? swarmId, string? runId, string? verdict, string? notes, DateTimeOffset? reviewedAt = null)
    {
        ArgumentNullException.ThrowIfNull(desk);
        var canonical = Canonical(verdict) ?? throw new ArgumentException("Verdict must be continue, fix, or done.");
        if (string.IsNullOrWhiteSpace(swarmId)) throw new ArgumentException("Swarm id is required.");
        if (string.IsNullOrWhiteSpace(runId)) throw new ArgumentException("Run id is required.");
        _ = Message(canonical, notes);
        var path = SwarmBoard.RunBoardPath(swarmId.Trim(), runId, desk.SwarmBoardRoot());
        if (!File.Exists(path)) throw new KeyNotFoundException("That swarm board is not on disk.");
        lock (Gate)
        {
            var board = SwarmBoard.Read(path);
            if (IsReviewed(board.ReviewStatus)) return false;
            if (!IsWaiting(board.ReviewStatus)
                || !string.IsNullOrWhiteSpace(board.ReviewVerdict)
                || board.ReviewLoops < 1
                || board.ReviewHandled != board.ReviewLoops)
                return false;
            var handled = board.ReviewHandled;
            WriteVerdict(board, canonical, notes, reviewedAt);
            board.ReviewHandled = handled;
            board.Save();
            return true;
        }
    }

    public static bool RefreshesPanel(SwarmContinueResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Outcome is "posted" or "queued";
    }

    public static string Status(IGrokBotDesk desk, string? swarmId, string? runId)
    {
        ArgumentNullException.ThrowIfNull(desk);
        if (string.IsNullOrWhiteSpace(swarmId)) throw new ArgumentException("Swarm id is required.");
        if (string.IsNullOrWhiteSpace(runId)) throw new ArgumentException("Run id is required.");
        var swarm = swarmId.Trim();
        var run = runId.Trim();
        var path = SwarmBoard.RunBoardPath(swarm, run, desk.SwarmBoardRoot());
        if (!File.Exists(path)) throw new KeyNotFoundException("That swarm board is not on disk.");
        if (!desk.GrokbotReviewsHandoffs(swarm))
            throw new InvalidOperationException("Grokbot reviews handoffs is off.");
        SwarmBoard board;
        lock (Gate)
            board = SwarmBoard.Read(path);
        var lines = new List<string>
        {
            "run_id\t" + run,
            "review_status\t" + (board.ReviewStatus ?? ""),
            "review_loops\t" + board.ReviewLoops.ToString(),
            "review_handled\t" + board.ReviewHandled.ToString()
        };
        foreach (var task in board.Tasks)
        {
            if (task is null) continue;
            lines.Add(task.Id + "\t" + SwarmSection.StatusLabel(task.Status));
        }
        return string.Join("\n", lines);
    }

    public static string Message(string verdict, string? notes)
    {
        var trimmed = (notes ?? "").Trim();
        var text = trimmed.Length == 0 ? "Grokbot review: " + verdict + "." : "Grokbot review: " + verdict + ". " + trimmed;
        if (text.Length > 8000 || text.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t'))
            throw new ArgumentException("Grok Bot message is empty, too long, or has unsupported characters.");
        return text;
    }

    public static IReadOnlyList<SwarmReviewDelivery> Deliver(string? swarmId, string? dataDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(swarmId) || !Guid.TryParseExact(swarmId.Trim(), "N", out _))
            throw new ArgumentException("Swarm id is required.");
        var path = QueuePath(swarmId.Trim(), dataDirectory);
        lock (Gate)
        {
            if (!File.Exists(path)) return [];
            var items = ReadQueue(path);
            File.Delete(path);
            return items;
        }
    }

    private static void WriteVerdict(SwarmBoard board, string canonical, string? notes, DateTimeOffset? reviewedAt)
    {
        var trimmed = (notes ?? "").Trim();
        board.ReviewVerdict = canonical;
        board.ReviewStatus = SwarmHandoffReview.StatusFor(canonical);
        if (string.IsNullOrWhiteSpace(board.ReviewIssues))
            board.ReviewIssues = trimmed;
        if (!HasUtcStamp(board.ReviewNote))
            board.ReviewNote = StampedNote(trimmed, reviewedAt ?? DateTimeOffset.UtcNow);
    }

    private static string StampedNote(string notes, DateTimeOffset utc)
    {
        var stamp = utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        return notes.Length == 0 ? stamp : notes + "\n" + stamp;
    }

    private static bool HasUtcStamp(string? note) =>
        !string.IsNullOrEmpty(note) && UtcStamp.IsMatch(note);

    private static bool IsReviewed(string? status) =>
        status is SwarmHandoffReview.ReviewedContinue or SwarmHandoffReview.ReviewedFix or SwarmHandoffReview.ReviewedDone;

    private static bool IsWaiting(string? status) =>
        string.Equals(status, SwarmHandoffReview.WaitingStatus, StringComparison.Ordinal)
        || string.Equals(status, "Waiting for review", StringComparison.Ordinal);

    private static string NoteWithReopened(string? note, string[] ids)
    {
        var line = "Reopened " + string.Join(", ", ids);
        var existing = (note ?? "").TrimEnd();
        return existing.Length == 0 ? line : existing + "\n" + line;
    }

    private static string? Canonical(string? verdict)
    {
        var text = (verdict ?? "").Trim().ToLowerInvariant();
        return text is "continue" or "fix" or "done" ? text : null;
    }

    private static string QueuePath(string swarmId, string? dataDirectory) =>
        Path.Combine(DataRoot(dataDirectory), "grokbot-review-queue", swarmId + ".json");

    private static string DataRoot(string? dataDirectory) =>
        string.IsNullOrWhiteSpace(dataDirectory) ? XaiDeskIdentity.DataDirectory() : dataDirectory;

    private static List<SwarmReviewDelivery> ReadQueue(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            var items = JsonSerializer.Deserialize<List<SwarmReviewDelivery>>(File.ReadAllText(path), QueueJson) ?? [];
            if (items.Any(item => item is null || string.IsNullOrWhiteSpace(item.Text)))
                throw new IOException("Grokbot review queue is invalid.");
            return items;
        }
        catch (JsonException ex)
        {
            throw new IOException("Grokbot review queue could not be read.", ex);
        }
    }

    private static void WriteQueue(string path, List<SwarmReviewDelivery> items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(items, QueueJson));
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
