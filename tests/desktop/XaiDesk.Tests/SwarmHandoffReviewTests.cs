using System.Text.Json;
using Sovereign.Desktop.Client;
using Sovereign.Desktop.Connector;
using Xunit;

namespace XaiDesk.Tests;

public sealed class SwarmHandoffReviewTests
{
    private const string SecretKey = "topsecretvalue";
    private const string SecretToken = "sk-live-abcdef";
    private const string Handoff =
        "SOVEREIGN_HANDOFF\nfinding: " + SecretToken + " api_key=" + SecretKey + "\nverified: the title matches\nEND_SOVEREIGN_HANDOFF";

    [Fact]
    public async Task Wake_json_includes_review_fields_scrubs_secrets_and_setting_off_sends_nothing()
    {
        var (root, swarmId, runId, board) = OpenRun("api_key=" + SecretKey);
        try
        {
            var json = SwarmHandoffReview.WakeJson(board, "Done.\n" + Handoff + "\n");
            Assert.DoesNotContain(SecretKey, json, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretToken, json, StringComparison.Ordinal);
            Assert.DoesNotContain("do-not-leak-file.txt", json, StringComparison.Ordinal);
            Assert.DoesNotContain("FILE-BODY-DO-NOT-SEND", json, StringComparison.Ordinal);
            Assert.Contains("[redacted]", json, StringComparison.Ordinal);
            using var document = JsonDocument.Parse(json);
            var payload = document.RootElement;
            Assert.False(payload.GetProperty("act").GetBoolean());
            Assert.Equal("review", payload.GetProperty("kind").GetString());
            Assert.Equal(swarmId, payload.GetProperty("swarm_id").GetString());
            Assert.Equal(runId, payload.GetProperty("run_id").GetString());
            Assert.Equal("Review the handoff", payload.GetProperty("goal").GetString());
            Assert.Contains("SOVEREIGN_HANDOFF", payload.GetProperty("handoff").GetString(), StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("message_id").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("preview").GetString()));
            var row = Assert.Single(payload.GetProperty("board").EnumerateArray());
            Assert.Equal("task-1", row.GetProperty("id").GetString());
            Assert.Equal("doing", row.GetProperty("status").GetString());
            Assert.Equal("worker-a", row.GetProperty("assignee").GetString());
            Assert.Contains("[redacted]", row.GetProperty("note").GetString(), StringComparison.Ordinal);
            Assert.False(row.TryGetProperty("files", out _));

            var sent = 0;
            var idle = await SwarmHandoffReview.WatchAsync(false, "Done.\n" + Handoff, board, null, (_, _) =>
            {
                sent++;
                return Task.FromResult(new SwarmWakeSend(true, "Grokbot pinged"));
            });
            Assert.Equal(0, sent);
            Assert.False(idle.Started);
            Assert.Null(idle.Instruction);
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task Verdict_routes_issues_and_next_and_done_does_not_queue()
    {
        Assert.Null(SwarmHandoffReview.Parse("The note is local."));
        var json = SwarmHandoffReview.Parse("{\"verdict\":\"continue\",\"issues\":\"Port is closed.\",\"next\":\"Bind the listener.\"}");
        Assert.Equal("Port is closed.\n\nBind the listener.", SwarmHandoffReview.InstructionFor(json));

        var fix = await WatchVerdict("verdict: fix\nissues: The proof is thin.\nnext: Add the missing check.");
        Assert.Equal("The proof is thin.\n\nAdd the missing check.", fix.Result.Instruction);
        Assert.Equal("Reviewed (fix)", fix.Result.Status);
        Assert.Equal(SwarmStatus.Doing, fix.TaskStatus);
        Assert.NotEqual(SwarmStatus.Done, fix.TaskStatus);

        var done = await WatchVerdict("verdict: done\nissues: none\nnext: stop");
        Assert.Null(done.Result.Instruction);
        Assert.Equal("Reviewed (done)", done.Result.Status);
        Assert.Equal(SwarmStatus.Doing, done.TaskStatus);
    }

    [Fact]
    public void Keep_swarm_helper_holds_completed_swarm_helpers_only()
    {
        Assert.True(SwarmHandoffReview.KeepSwarmHelper("completed", true, true));
        Assert.False(SwarmHandoffReview.KeepSwarmHelper("stopped", true, true));
        Assert.False(SwarmHandoffReview.KeepSwarmHelper("completed", false, true));
        Assert.False(SwarmHandoffReview.KeepSwarmHelper("completed", true, false));
    }

    [Fact]
    public async Task Continue_verdict_returns_issues_and_next_through_watch()
    {
        var continued = await WatchVerdict("verdict: continue\nissues: The port is closed.\nnext: Bind the listener.");
        Assert.Equal("The port is closed.\n\nBind the listener.", continued.Result.Instruction);
        Assert.NotNull(continued.Result.Instruction);
        Assert.Equal("Reviewed (continue)", continued.Result.Status);
        Assert.Equal(SwarmStatus.Doing, continued.TaskStatus);
    }

    [Fact]
    public void Saved_chat_omits_null_review_fields_and_keeps_them_when_set()
    {
        var bare = JsonSerializer.Serialize(ChatArchive.New());
        Assert.DoesNotContain("GrokbotReviewsHandoffs", bare, StringComparison.Ordinal);
        Assert.DoesNotContain("GrokbotReviewLoopCap", bare, StringComparison.Ordinal);

        var json = JsonSerializer.Serialize(ChatArchive.New() with { GrokbotReviewsHandoffs = true, GrokbotReviewLoopCap = 4 });
        Assert.Contains("\"GrokbotReviewsHandoffs\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"GrokbotReviewLoopCap\":4", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timeout_records_review_timed_out_and_does_not_queue()
    {
        var (root, _, _, board) = OpenRun("proof line");
        try
        {
            var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var sent = 0;
            string? waiting = null;
            var result = await SwarmHandoffReview.WatchAsync(true, Handoff, board, 3, (_, _) =>
            {
                sent++;
                if (sent == 1)
                {
                    var started = SwarmBoard.Read(board.BoardPath);
                    waiting = started.ReviewNote;
                    Assert.Equal("Waiting for Grokbot", started.ReviewStatus);
                }
                return Task.FromResult(new SwarmWakeSend(true, "Grokbot pinged"));
            }, () => now, (span, _) =>
            {
                now = now.Add(span);
                return Task.CompletedTask;
            }, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(2));
            Assert.Equal(4, sent);
            Assert.True(result.Started);
            Assert.Contains("Waiting for Grokbot review", waiting, StringComparison.Ordinal);
            Assert.Equal("Grokbot review timed out", result.Status);
            Assert.Equal("Grokbot review timed out", result.Note);
            Assert.Null(result.Instruction);
            var saved = SwarmBoard.Read(board.BoardPath);
            Assert.Equal("Grokbot review timed out", saved.ReviewStatus);
            Assert.Equal("Grokbot review timed out", saved.ReviewNote);
            Assert.Equal("", saved.ReviewVerdict);
            Assert.Equal(1, saved.ReviewLoops);
            Assert.Equal(SwarmStatus.Doing, saved.TaskById("task-1").Status);
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task Timeout_at_the_default_twenty_minutes_does_not_continue()
    {
        Assert.Equal(TimeSpan.FromMinutes(20), SwarmHandoffReview.DefaultTimeout);
        var (root, _, _, board) = OpenRun("proof line");
        try
        {
            var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var now = start;
            var result = await SwarmHandoffReview.WatchAsync(true, Handoff, board, 3, (_, _) =>
                Task.FromResult(new SwarmWakeSend(true, "Grokbot pinged")),
                () => now, (span, _) =>
                {
                    now = now.Add(span);
                    return Task.CompletedTask;
                }, poll: TimeSpan.FromMinutes(20));
            Assert.Equal(start.AddMinutes(20), now);
            Assert.True(result.Started);
            Assert.Equal("Grokbot review timed out", result.Status);
            Assert.Equal("Grokbot review timed out", result.Note);
            Assert.Null(result.Instruction);
            var saved = SwarmBoard.Read(board.BoardPath);
            Assert.Equal("Grokbot review timed out", saved.ReviewStatus);
            Assert.Equal("", saved.ReviewVerdict);
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task Finished_run_enters_the_wait_and_writes_the_summary()
    {
        var (root, swarmId, runId, board) = OpenFinished(testsPassed: true, failed: true);
        var quiet = OpenFinished(testsPassed: false, failed: false);
        try
        {
            var quietNote = SwarmHandoffReview.WaitingNote(quiet.Board);
            Assert.Contains("Tasks done: 1/1", quietNote, StringComparison.Ordinal);
            Assert.Contains("Tests passed: not recorded", quietNote, StringComparison.Ordinal);
            Assert.Contains("Open risks: none", quietNote, StringComparison.Ordinal);
            var fromResult = SwarmHandoffReview.WaitingNote(new SwarmBoard
            {
                Goal = "g",
                Tasks = [new SwarmTask { Id = "t", Status = SwarmStatus.Done, Proof = "", Result = "tests passed" }]
            });
            Assert.Contains("Tests passed: tests passed", fromResult, StringComparison.Ordinal);

            string? waiting = null;
            var result = await SwarmHandoffReview.WatchAsync(true, "Done.\n" + Handoff, board, 3, (_, _) =>
            {
                var started = SwarmBoard.Read(board.BoardPath);
                waiting = started.ReviewNote;
                Assert.Equal("Waiting for Grokbot", started.ReviewStatus);
                Assert.Equal(1, started.ReviewLoops);
                var decision = SwarmHandoffReview.Parse("verdict: continue\nissues: Ship it.\nnext: Bind.");
                Assert.NotNull(decision);
                SwarmHandoffReview.Record(swarmId, runId, decision, root, reviewedAt: new DateTimeOffset(2026, 5, 6, 7, 8, 9, TimeSpan.Zero));
                return Task.FromResult(new SwarmWakeSend(true, "Grokbot pinged"));
            }, () => DateTimeOffset.UnixEpoch, (_, _) => Task.CompletedTask, TimeSpan.FromMinutes(20), TimeSpan.FromSeconds(1));
            Assert.NotNull(waiting);
            Assert.Contains("Waiting for Grokbot review", waiting, StringComparison.Ordinal);
            Assert.Contains("Goal: Review the handoff", waiting, StringComparison.Ordinal);
            Assert.Contains("Tasks done: 1/2", waiting, StringComparison.Ordinal);
            Assert.Contains("Tests passed: tests passed", waiting, StringComparison.Ordinal);
            Assert.Contains("Open risks: task-2", waiting, StringComparison.Ordinal);
            Assert.Equal("Reviewed (continue)", result.Status);
            Assert.Equal("Ship it.\n\nBind.", result.Instruction);
            var saved = SwarmBoard.Read(board.BoardPath);
            Assert.Contains("Ship it.", saved.ReviewNote, StringComparison.Ordinal);
            Assert.Contains("2026-05-06T07:08:09Z", saved.ReviewNote, StringComparison.Ordinal);
            Assert.NotEqual("", saved.ReviewNote);
        }
        finally
        {
            Delete(root);
            Delete(quiet.Root);
        }
    }

    [Fact]
    public async Task Finished_run_continue_fix_and_done_keep_exact_status()
    {
        foreach (var verdict in new[] { "continue", "fix", "done" })
        {
            var (root, swarmId, runId, board) = OpenFinished(testsPassed: false, failed: false);
            try
            {
                var decision = SwarmHandoffReview.Parse("verdict: " + verdict + "\nissues: Noted.\nnext: Next step.");
                Assert.NotNull(decision);
                var result = await SwarmHandoffReview.WatchAsync(true, Handoff, board, 3, (_, _) =>
                {
                    SwarmHandoffReview.Record(swarmId, runId, decision, root, reviewedAt: new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero));
                    return Task.FromResult(new SwarmWakeSend(true, "Grokbot pinged"));
                }, () => DateTimeOffset.UnixEpoch, (_, _) => Task.CompletedTask, TimeSpan.FromMinutes(20), TimeSpan.FromSeconds(1));
                var expected = verdict switch
                {
                    "continue" => "Reviewed (continue)",
                    "fix" => "Reviewed (fix)",
                    "done" => "Reviewed (done)",
                    _ => ""
                };
                Assert.Equal(expected, result.Status);
                if (verdict == "done") Assert.Null(result.Instruction);
                else Assert.Equal("Noted.\n\nNext step.", result.Instruction);
                var saved = SwarmBoard.Read(board.BoardPath);
                Assert.Equal(expected, saved.ReviewStatus);
                Assert.Contains("Noted.", saved.ReviewNote, StringComparison.Ordinal);
                Assert.Contains("2026-02-03T04:05:06Z", saved.ReviewNote, StringComparison.Ordinal);
                Assert.Equal(SwarmStatus.Done, saved.TaskById("task-1").Status);
            }
            finally { Delete(root); }
        }
    }

    [Fact]
    public async Task Setting_off_does_not_start_or_send_when_every_task_is_done()
    {
        var (root, _, _, board) = OpenFinished(testsPassed: false, failed: false);
        try
        {
            var before = File.ReadAllBytes(board.BoardPath);
            var sent = 0;
            var idle = await SwarmHandoffReview.WatchAsync(false, Handoff, board, 3, (_, _) =>
            {
                sent++;
                return Task.FromResult(new SwarmWakeSend(true, "Grokbot pinged"));
            });
            Assert.Equal(0, sent);
            Assert.False(idle.Started);
            Assert.Null(idle.Instruction);
            Assert.Equal(before, File.ReadAllBytes(board.BoardPath));
            Assert.Equal(0, SwarmBoard.Read(board.BoardPath).ReviewLoops);
            Assert.False(SwarmHandoffReview.ShouldStart(false, Handoff, board, 3));
            Assert.False(SwarmHandoffReview.ShouldStart(true, Handoff, new SwarmBoard { Id = "abc", Goal = "g", Tasks = [] }, 3));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void Continue_without_review_does_nothing_unless_the_review_timed_out()
    {
        var (root, _, _, board) = OpenRun("proof line");
        try
        {
            board.ReviewStatus = "Waiting for Grokbot";
            board.ReviewNote = "keep";
            board.Save();
            var waiting = File.ReadAllBytes(board.BoardPath);
            Assert.False(SwarmHandoffReview.ContinueWithoutReview(SwarmBoard.Read(board.BoardPath)));
            Assert.False(SwarmHandoffReview.ContinueWithoutReview(null));
            Assert.Equal(waiting, File.ReadAllBytes(board.BoardPath));

            board.ReviewStatus = "Reviewed (continue)";
            board.Save();
            var reviewed = File.ReadAllBytes(board.BoardPath);
            Assert.False(SwarmHandoffReview.ContinueWithoutReview(SwarmBoard.Read(board.BoardPath)));
            Assert.Equal(reviewed, File.ReadAllBytes(board.BoardPath));

            var timed = SwarmBoard.Read(board.BoardPath);
            timed.ReviewStatus = "Grokbot review timed out";
            timed.ReviewVerdict = "";
            timed.Save();
            var timedBytes = File.ReadAllBytes(timed.BoardPath);
            Assert.True(SwarmHandoffReview.ContinueWithoutReview(SwarmBoard.Read(timed.BoardPath)));
            Assert.Equal(timedBytes, File.ReadAllBytes(timed.BoardPath));
            var after = SwarmBoard.Read(timed.BoardPath);
            Assert.Equal("Grokbot review timed out", after.ReviewStatus);
            Assert.Equal("", after.ReviewVerdict);
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task Loop_cap_blocks_another_wake()
    {
        var (root, _, _, board) = OpenRun("proof line");
        try
        {
            board.ReviewLoops = 3;
            board.Save();
            var sent = 0;
            var result = await SwarmHandoffReview.WatchAsync(true, Handoff, board, 3, (_, _) =>
            {
                sent++;
                return Task.FromResult(new SwarmWakeSend(true, "Grokbot pinged"));
            });
            Assert.Equal(0, sent);
            Assert.False(result.Started);
            Assert.Equal(3, SwarmBoard.Read(board.BoardPath).ReviewLoops);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void Save_writes_board_json_bak_and_does_not_touch_current_txt()
    {
        var root = Path.Combine(Path.GetTempPath(), "xai-desk-review-" + Guid.NewGuid().ToString("N"));
        try
        {
            var board = SwarmBoard.Create("Ship the panel", [new SwarmTask { Id = "t1", Title = "Panel", Status = SwarmStatus.Todo }], root);
            var before = File.ReadAllText(board.BoardPath);
            Assert.False(File.Exists(board.BoardPath + ".bak"));
            board.ReviewLoops = 1;
            board.ReviewStatus = "Waiting for review";
            board.Save();
            Assert.True(File.Exists(board.BoardPath + ".bak"));
            Assert.Equal(before, File.ReadAllText(board.BoardPath + ".bak"));
            var saved = SwarmBoard.Read(board.BoardPath);
            Assert.Equal(1, saved.ReviewLoops);
            Assert.Equal("Waiting for review", saved.ReviewStatus);
            Assert.Empty(Directory.GetFiles(root, "current.txt", SearchOption.AllDirectories));
        }
        finally { Delete(root); }
    }

    private static async Task<(SwarmHandoffWatchResult Result, string TaskStatus)> WatchVerdict(string resultText)
    {
        var (root, swarmId, runId, board) = OpenRun("proof line");
        try
        {
            var decision = SwarmHandoffReview.Parse(resultText);
            Assert.NotNull(decision);
            var result = await SwarmHandoffReview.WatchAsync(true, Handoff, board, 3, (_, _) =>
            {
                SwarmHandoffReview.Record(swarmId, runId, decision, root, reviewedAt: new DateTimeOffset(2026, 4, 5, 6, 7, 8, TimeSpan.Zero));
                return Task.FromResult(new SwarmWakeSend(true, "Grokbot pinged"));
            }, () => DateTimeOffset.UnixEpoch, (_, _) => Task.CompletedTask, TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(1));
            var saved = SwarmBoard.Read(board.BoardPath);
            Assert.Contains(decision.Issues.Trim(), saved.ReviewNote, StringComparison.Ordinal);
            Assert.Contains("2026-04-05T06:07:08Z", saved.ReviewNote, StringComparison.Ordinal);
            return (result, saved.TaskById("task-1").Status);
        }
        finally { Delete(root); }
    }

    private static (string Root, string SwarmId, string RunId, SwarmBoard Board) OpenRun(string proof)
    {
        var root = Path.Combine(Path.GetTempPath(), "xai-desk-review-" + Guid.NewGuid().ToString("N"));
        var swarmId = Guid.NewGuid().ToString("N");
        var runId = Guid.NewGuid().ToString("N");
        var board = SwarmBoard.Create("Review the handoff",
        [
            new SwarmTask
            {
                Id = "task-1",
                Title = "Write the panel",
                Status = SwarmStatus.Doing,
                Assignee = "worker-a",
                Proof = proof,
                Files = ["do-not-leak-file.txt"],
                Details = "FILE-BODY-DO-NOT-SEND"
            }
        ], Path.Combine(root, swarmId, "runs"), runId);
        return (root, swarmId, runId, board);
    }

    private static (string Root, string SwarmId, string RunId, SwarmBoard Board) OpenFinished(bool testsPassed, bool failed)
    {
        var root = Path.Combine(Path.GetTempPath(), "xai-desk-review-" + Guid.NewGuid().ToString("N"));
        var swarmId = Guid.NewGuid().ToString("N");
        var runId = Guid.NewGuid().ToString("N");
        var tasks = new List<SwarmTask>
        {
            new()
            {
                Id = "task-1",
                Title = "Write the panel",
                Status = SwarmStatus.Done,
                Assignee = "worker-a",
                Proof = testsPassed ? "tests passed" : "shipped"
            }
        };
        if (failed)
            tasks.Add(new SwarmTask { Id = "task-2", Title = "Check risks", Status = SwarmStatus.Failed, Result = "broke" });
        var board = SwarmBoard.Create("Review the handoff", tasks, Path.Combine(root, swarmId, "runs"), runId);
        return (root, swarmId, runId, board);
    }

    private static void Delete(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }
}
