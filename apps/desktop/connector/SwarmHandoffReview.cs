// Grokbot reviews swarm handoffs; verdict continue/fix/done.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sovereign.Desktop.Connector;

public sealed record SwarmReviewDecision(string Verdict, string Issues, string Next);

public readonly record struct SwarmWakeSend(bool Ok, string Note, int? StatusCode = null);

public sealed record SwarmHandoffWatchResult(bool Started, string Status, string? Note, string? Instruction, string? Transcript = null);

public sealed record SwarmReviewTaskWake(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("assignee")] string Assignee,
    [property: JsonPropertyName("note")] string Note);

/// <summary>
/// Grokbot reviews a swarm handoff after the main agent finishes. The watch polls the run board.
/// It does not cancel the chat session and it does not mark a task done.
/// </summary>
public static class SwarmHandoffReview
{
    public const int DefaultCap = 3;
    public const string WaitingStatus = "Waiting for Grokbot";
    public const string WaitingDetail = "Waiting for Grokbot review";
    public const string ReviewedContinue = "Reviewed (continue)";
    public const string ReviewedFix = "Reviewed (fix)";
    public const string ReviewedDone = "Reviewed (done)";
    public const string TimedOutStatus = "Grokbot review timed out";
    public const string TimeoutNote = "Grokbot review timed out";
    public const string ReviewerInstruction =
        "After the verdict, call swarm_continue(swarm_id, run_id, verdict, notes). Call swarm_run_status(swarm_id, run_id) to confirm the swarm moved.";
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(20);
    public static readonly TimeSpan DefaultPoll = TimeSpan.FromSeconds(1);

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static int LoopCap(int? loopCap) => loopCap is null or < 1 ? DefaultCap : loopCap.Value;

    public static bool KeepSwarmHelper(string? outcome, bool hold, bool isSwarmHelper) =>
        hold && isSwarmHelper && outcome != "stopped";

    public static bool ShouldStart(bool? reviewsHandoffs, string? reply, SwarmBoard? board, int? loopCap)
    {
        if (reviewsHandoffs != true) return false;
        if (HandoffText(reply) is null) return false;
        if (board?.Tasks is not { Count: > 0 }) return false;
        if (board.ReviewLoops >= LoopCap(loopCap)) return false;
        return SwarmSchedule.HasOpenWork(board) || TasksSettled(board);
    }

    public static string WaitingNote(SwarmBoard board)
    {
        ArgumentNullException.ThrowIfNull(board);
        var tasks = (board.Tasks ?? []).Where(task => task is not null).ToArray();
        var done = tasks.Count(task => SwarmSection.StatusLabel(task.Status) == SwarmStatus.Done);
        var goal = (board.Goal ?? "").Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
        return WaitingDetail
            + "\nGoal: " + goal
            + "\nTasks done: " + done + "/" + tasks.Length
            + "\nTests passed: " + TestsPassed(tasks)
            + "\nOpen risks: " + OpenRisks(tasks);
    }

    public static bool ContinueWithoutReview(SwarmBoard? board) =>
        board is not null && string.Equals(board.ReviewStatus, TimedOutStatus, StringComparison.Ordinal);

    public static string? HandoffText(string? reply)
    {
        if (string.IsNullOrEmpty(reply)) return null;
        const string startMark = "SOVEREIGN_HANDOFF";
        const string endMark = "END_SOVEREIGN_HANDOFF";
        var start = reply.IndexOf(startMark, StringComparison.Ordinal);
        if (start < 0) return null;
        var end = reply.IndexOf(endMark, start + startMark.Length, StringComparison.Ordinal);
        if (end < 0) return null;
        return reply[start..(end + endMark.Length)];
    }

    public static SwarmReviewDecision? Parse(string? result)
    {
        if (string.IsNullOrWhiteSpace(result)) return null;
        var trimmed = result.Trim();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var json = JsonDocument.Parse(trimmed);
                if (json.RootElement.ValueKind == JsonValueKind.Object
                    && Canonical(ReadJson(json.RootElement, "verdict")) is { } parsed)
                    return new SwarmReviewDecision(parsed, ReadJson(json.RootElement, "issues"), ReadJson(json.RootElement, "next"));
            }
            catch (JsonException) { }
        }
        string? verdict = null;
        var issues = "";
        var next = "";
        foreach (var raw in trimmed.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (key.Equals("verdict", StringComparison.OrdinalIgnoreCase)) verdict = value;
            else if (key.Equals("issues", StringComparison.OrdinalIgnoreCase)) issues = value;
            else if (key.Equals("next", StringComparison.OrdinalIgnoreCase)) next = value;
        }
        var canonical = Canonical(verdict);
        return canonical is null ? null : new SwarmReviewDecision(canonical, issues, next);
    }

    public static string? InstructionFor(SwarmReviewDecision? decision)
    {
        if (decision is null || decision.Verdict == "done") return null;
        if (decision.Verdict is not ("continue" or "fix")) return null;
        var issues = (decision.Issues ?? "").Trim();
        var next = (decision.Next ?? "").Trim();
        if (issues.Length == 0) return next.Length == 0 ? decision.Verdict : next;
        if (next.Length == 0) return issues;
        return issues + "\n\n" + next;
    }

    public static string StatusFor(string verdict) => verdict switch
    {
        "continue" => ReviewedContinue,
        "fix" => ReviewedFix,
        "done" => ReviewedDone,
        _ => ""
    };

    public static string WakeJson(SwarmBoard board, string? reply)
    {
        ArgumentNullException.ThrowIfNull(board);
        Identity(board, out var swarmId, out var runId);
        var handoff = Clip(HandoffText(reply) ?? "", 2000);
        var preview = GrokbotWake.Preview(handoff);
        if (preview.Length == 0) preview = GrokbotWake.Preview(board.Goal ?? "");
        var loop = board.ReviewLoops;
        var payload = new ReviewWakePayload(
            "review-" + (string.IsNullOrEmpty(runId) ? swarmId : runId) + "-" + loop.ToString(System.Globalization.CultureInfo.InvariantCulture),
            false,
            preview,
            "review",
            swarmId,
            runId,
            loop,
            Clip(board.Goal, 240),
            handoff,
            (board.Tasks ?? []).Select(TaskWake).ToArray(),
            ReviewerInstruction);
        return JsonSerializer.Serialize(payload, Json);
    }

    public static string Record(string? swarmId, string? runId, SwarmReviewDecision decision, string? root, string? taskId = null, DateTimeOffset? reviewedAt = null)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var board = Open(swarmId, runId, root, taskId);
        board.ReviewVerdict = decision.Verdict;
        board.ReviewIssues = decision.Issues ?? "";
        board.ReviewNext = decision.Next ?? "";
        board.ReviewStatus = StatusFor(decision.Verdict);
        board.ReviewNote = VerdictNote(decision, reviewedAt ?? DateTimeOffset.UtcNow);
        board.Save();
        return board.ReviewStatus;
    }

    public static async Task<SwarmHandoffWatchResult> WatchAsync(
        bool? reviewsHandoffs,
        string? reply,
        SwarmBoard? board,
        int? loopCap,
        Func<string, CancellationToken, Task<SwarmWakeSend>> send,
        Func<DateTimeOffset>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? timeout = null,
        TimeSpan? poll = null,
        TimeSpan? repeat = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(send);
        if (reviewsHandoffs != true || HandoffText(reply) is null || board is null || string.IsNullOrWhiteSpace(board.Id))
            return Idle();
        var path = board.BoardPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return Idle();
        var live = SwarmBoard.Read(path);
        if (!ShouldStart(reviewsHandoffs, reply, live, loopCap)) return Idle();

        live.ReviewLoops++;
        live.ReviewStatus = WaitingStatus;
        live.ReviewVerdict = "";
        live.ReviewIssues = "";
        live.ReviewNext = "";
        live.ReviewNote = WaitingNote(live);
        live.Save();

        var json = WakeJson(live, reply);
        var now = clock ?? (() => DateTimeOffset.UtcNow);
        var wait = delay ?? ((span, token) => Task.Delay(span, token));
        var limit = timeout ?? DefaultTimeout;
        var interval = poll ?? DefaultPoll;
        var repeatEvery = repeat ?? SwarmWakeRepeat.Interval;
        if (limit < TimeSpan.Zero) limit = TimeSpan.Zero;
        if (interval < TimeSpan.Zero) interval = TimeSpan.Zero;
        if (repeatEvery < TimeSpan.Zero) repeatEvery = TimeSpan.Zero;

        var sent = await DispatchWake(send, json, wait, cancellationToken).ConfigureAwait(false);
        if (!sent.Ok)
        {
            var failed = SwarmBoard.Read(path);
            if (ReadDecision(failed) is null)
            {
                var note = WakeFailure(sent.Note);
                failed.ReviewNote = note;
                failed.ReviewStatus = note;
                failed.Save();
                return new SwarmHandoffWatchResult(true, note, note, null);
            }
            return Finish(failed);
        }

        var deadline = now().Add(limit);
        var lastWake = now();
        var steps = 0;
        var maxSteps = interval <= TimeSpan.Zero ? 2 : (int)Math.Min(100000, limit.Ticks / Math.Max(1, interval.Ticks) + 3);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = SwarmBoard.Read(path);
            if (ReadDecision(current) is not null) return Finish(current);
            if (now() >= deadline || steps >= maxSteps) break;
            if (SwarmWakeRepeat.Due(lastWake, now(), repeatEvery)
                && string.Equals(current.ReviewStatus, WaitingStatus, StringComparison.Ordinal))
            {
                var again = await DispatchWake(send, json, wait, cancellationToken).ConfigureAwait(false);
                lastWake = now();
                current = SwarmBoard.Read(path);
                if (ReadDecision(current) is not null) return Finish(current);
                if (!again.Ok)
                {
                    var note = WakeFailure(again.Note);
                    current.ReviewNote = note;
                    current.ReviewStatus = note;
                    current.Save();
                    return new SwarmHandoffWatchResult(true, note, note, null);
                }
            }
            var remaining = deadline - now();
            var step = interval < remaining ? interval : remaining;
            if (step < TimeSpan.Zero) step = TimeSpan.Zero;
            steps++;
            await wait(step, cancellationToken).ConfigureAwait(false);
        }

        var timed = SwarmBoard.Read(path);
        if (ReadDecision(timed) is not null) return Finish(timed);
        timed.ReviewStatus = TimedOutStatus;
        timed.ReviewNote = TimeoutNote;
        timed.Save();
        await DispatchWake(send, json, wait, cancellationToken).ConfigureAwait(false);
        return new SwarmHandoffWatchResult(true, TimedOutStatus, TimeoutNote, null, TimeoutNote);
    }

    private static async Task<SwarmWakeSend> DispatchWake(
        Func<string, CancellationToken, Task<SwarmWakeSend>> send,
        string json,
        Func<TimeSpan, CancellationToken, Task> wait,
        CancellationToken cancellationToken)
    {
        var sent = await SendWake(send, json, cancellationToken).ConfigureAwait(false);
        if (SwarmWakeRepeat.NextAuthRetry(sent.StatusCode, false) is not { } pause) return sent;
        await wait(pause, cancellationToken).ConfigureAwait(false);
        return await SendWake(send, json, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<SwarmWakeSend> SendWake(
        Func<string, CancellationToken, Task<SwarmWakeSend>> send,
        string json,
        CancellationToken cancellationToken)
    {
        try
        {
            return await send(json, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new SwarmWakeSend(false, "Wake failed.");
        }
    }

    private static SwarmHandoffWatchResult Finish(SwarmBoard board)
    {
        var decision = ReadDecision(board);
        var status = StatusFor(decision?.Verdict ?? "");
        if (status.Length == 0) status = board.ReviewStatus ?? "";
        return new SwarmHandoffWatchResult(true, status, board.ReviewNote, InstructionFor(decision));
    }

    private static SwarmHandoffWatchResult Idle() => new(false, "", null, null);

    private static SwarmReviewDecision? ReadDecision(SwarmBoard board)
    {
        var verdict = Canonical(board.ReviewVerdict);
        return verdict is null ? null : new SwarmReviewDecision(verdict, board.ReviewIssues ?? "", board.ReviewNext ?? "");
    }

    private static SwarmBoard Open(string? swarmId, string? runId, string? root, string? taskId)
    {
        if (!string.IsNullOrWhiteSpace(runId))
        {
            if (string.IsNullOrWhiteSpace(swarmId)) throw new ArgumentException("Swarm id is required.");
            var pinned = SwarmBoard.RunBoardPath(swarmId, runId, root);
            if (!File.Exists(pinned)) throw new KeyNotFoundException("That swarm board is not on disk.");
            return SwarmBoard.Read(pinned);
        }
        if (!string.IsNullOrWhiteSpace(swarmId))
        {
            var active = SwarmSection.ActiveBoardFile(swarmId, root);
            if (active is null || !File.Exists(active)) throw new KeyNotFoundException("That swarm board is not on disk.");
            return SwarmBoard.Read(active);
        }
        if (!string.IsNullOrWhiteSpace(taskId))
            return SwarmBoard.RequireTask(taskId, root, out _, null, null);
        throw new ArgumentException("Swarm id is required.");
    }

    public static void Identity(SwarmBoard board, out string swarmId, out string? runId)
    {
        swarmId = board.Id ?? "";
        runId = null;
        var root = (board.Root ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Path.GetFileName(root).Equals("runs", StringComparison.OrdinalIgnoreCase)) return;
        runId = board.Id;
        var parent = Path.GetFileName(Path.GetDirectoryName(root) ?? "");
        if (!string.IsNullOrEmpty(parent)) swarmId = parent;
    }

    private static SwarmReviewTaskWake TaskWake(SwarmTask task)
    {
        var raw = string.IsNullOrWhiteSpace(task.Proof) ? task.Result : task.Proof;
        return new SwarmReviewTaskWake(
            task.Id ?? "",
            SwarmSection.StatusLabel(task.Status),
            task.Assignee ?? "",
            GrokbotWake.Preview(raw ?? ""));
    }

    private static string Clip(string? text, int max)
    {
        var scrubbed = GrokbotWake.Scrub(text ?? "").Trim();
        return scrubbed.Length <= max ? scrubbed : scrubbed[..max];
    }

    private static string WakeFailure(string? note)
    {
        var text = GrokbotWake.Scrub(string.IsNullOrWhiteSpace(note) ? "Wake failed." : note.Trim());
        return text.Length <= 200 ? text : text[..200];
    }

    private static bool TasksSettled(SwarmBoard board) =>
        board.Tasks.Count > 0
        && board.Tasks.All(task => SwarmSection.StatusLabel(task.Status) is SwarmStatus.Done or SwarmStatus.Failed);

    private static string TestsPassed(SwarmTask[] tasks)
    {
        foreach (var task in tasks)
        {
            if (MentionsTestsPassed(task.Proof) || MentionsTestsPassed(task.Result)) return "tests passed";
        }
        return "not recorded";
    }

    private static bool MentionsTestsPassed(string? text) =>
        !string.IsNullOrEmpty(text) && text.Contains("tests passed", StringComparison.OrdinalIgnoreCase);

    private static string OpenRisks(SwarmTask[] tasks)
    {
        var ids = tasks
            .Where(task => SwarmSection.StatusLabel(task.Status) == SwarmStatus.Failed && !string.IsNullOrWhiteSpace(task.Id))
            .Select(task => task.Id)
            .ToArray();
        return ids.Length == 0 ? "none" : string.Join(", ", ids);
    }

    private static string VerdictNote(SwarmReviewDecision decision, DateTimeOffset utc)
    {
        var issues = (decision.Issues ?? "").Trim();
        var next = (decision.Next ?? "").Trim();
        var stamp = utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        if (issues.Length == 0 && next.Length == 0) return stamp;
        if (issues.Length == 0) return next + "\n" + stamp;
        if (next.Length == 0) return issues + "\n" + stamp;
        return issues + "\n" + next + "\n" + stamp;
    }

    private static string? Canonical(string? verdict)
    {
        var text = (verdict ?? "").Trim().ToLowerInvariant();
        return text is "continue" or "fix" or "done" ? text : null;
    }

    private static string ReadJson(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return "";
        return value.GetString() ?? "";
    }

    private sealed record ReviewWakePayload(
        [property: JsonPropertyName("message_id")] string MessageId,
        [property: JsonPropertyName("act")] bool Act,
        [property: JsonPropertyName("preview")] string Preview,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("swarm_id")] string SwarmId,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [property: JsonPropertyName("run_id")] string? RunId,
        [property: JsonPropertyName("review_loop")] int ReviewLoop,
        [property: JsonPropertyName("goal")] string Goal,
        [property: JsonPropertyName("handoff")] string Handoff,
        [property: JsonPropertyName("board")] SwarmReviewTaskWake[] Board,
        [property: JsonPropertyName("reviewer")] string Reviewer);
}
