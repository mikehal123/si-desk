using System.Text.Json;
using Sovereign.Desktop.Connector;
using Xunit;

namespace XaiDesk.Tests;

public sealed class SwarmWakeRepeatTests
{
    private const string SampleUrl = "https://wake.example/hook";
    private const string SampleKey = "wake-sender-key";
    private const string Handoff = "SOVEREIGN_HANDOFF\nverified: ready\nEND_SOVEREIGN_HANDOFF";

    [Fact]
    public void Repeat_is_not_due_at_three_minutes_fifty_nine_seconds_and_is_due_at_four()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.False(SwarmWakeRepeat.Due(start, start.AddMinutes(3).AddSeconds(59)));
        Assert.True(SwarmWakeRepeat.Due(start, start.AddMinutes(4)));
        Assert.Equal(TimeSpan.FromMinutes(4), SwarmWakeRepeat.Interval);
    }

    [Fact]
    public void Wake_identity_contains_the_run_id_and_loop()
    {
        var (root, _, runId, board) = OpenRun();
        try
        {
            board.ReviewLoops = 2;
            board.Save();
            var json = SwarmHandoffReview.WakeJson(board, Handoff);
            using var document = JsonDocument.Parse(json);
            Assert.Equal(runId, document.RootElement.GetProperty("run_id").GetString());
            Assert.Equal(2, document.RootElement.GetProperty("review_loop").GetInt32());
            var identity = SwarmWakeRepeat.Identity(runId, 2);
            Assert.Contains(runId, identity, StringComparison.Ordinal);
            Assert.Contains("2", identity, StringComparison.Ordinal);
            Assert.Contains(runId, json, StringComparison.Ordinal);
            Assert.Contains("\"review_loop\":2", json, StringComparison.Ordinal);
            Assert.Equal(json, SwarmHandoffReview.WakeJson(board, Handoff));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void Audit_line_records_run_loop_attempt_and_status_without_url_or_key()
    {
        var folder = Path.Combine(Path.GetTempPath(), "xai-desk-wake-audit-" + Guid.NewGuid().ToString("N"), "grokbot");
        try
        {
            var at = new DateTimeOffset(2026, 10, 14, 3, 41, 0, TimeSpan.Zero);
            var line = SwarmWakeRepeat.Audit(at, "run-42", 3, 2, 204, SampleUrl, SampleKey);
            Assert.Equal("wake", line.Tool);
            Assert.Contains("run-42", line.Args, StringComparison.Ordinal);
            Assert.Contains("loop=3", line.Args, StringComparison.Ordinal);
            Assert.Contains("attempt=2", line.Args, StringComparison.Ordinal);
            Assert.Contains("204", line.Result, StringComparison.Ordinal);
            Assert.DoesNotContain(SampleUrl, line.Args, StringComparison.Ordinal);
            Assert.DoesNotContain(SampleKey, line.Args, StringComparison.Ordinal);
            Assert.DoesNotContain(SampleUrl, line.Result, StringComparison.Ordinal);
            Assert.DoesNotContain(SampleKey, line.Result, StringComparison.Ordinal);
            SwarmWakeRepeat.AppendAudit(folder, line, SampleUrl, SampleKey);
            var stored = File.ReadAllText(Path.Combine(folder, "audit.jsonl"));
            Assert.Contains("run-42", stored, StringComparison.Ordinal);
            Assert.Contains("loop=3", stored, StringComparison.Ordinal);
            Assert.Contains("attempt=2", stored, StringComparison.Ordinal);
            Assert.Contains("204", stored, StringComparison.Ordinal);
            Assert.Contains("\"tool\":\"wake\"", stored, StringComparison.Ordinal);
            Assert.DoesNotContain(SampleUrl, stored, StringComparison.Ordinal);
            Assert.DoesNotContain(SampleKey, stored, StringComparison.Ordinal);
        }
        finally
        {
            var parent = Directory.GetParent(folder)?.FullName;
            if (parent is not null && Directory.Exists(parent)) Directory.Delete(parent, true);
        }
    }

    [Fact]
    public void Auth_failure_schedules_one_sixty_second_retry_and_the_warning()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), SwarmWakeRepeat.NextAuthRetry(400, false));
        Assert.Equal(TimeSpan.FromSeconds(60), SwarmWakeRepeat.NextAuthRetry(401, false));
        Assert.Equal("check wake key", SwarmWakeRepeat.Warning(400));
        Assert.Equal("check wake key", SwarmWakeRepeat.Warning(401));
        Assert.Null(SwarmWakeRepeat.NextAuthRetry(400, true));
        Assert.Null(SwarmWakeRepeat.NextAuthRetry(401, true));
        Assert.Null(SwarmWakeRepeat.NextAuthRetry(429, false));
        Assert.Null(SwarmWakeRepeat.NextAuthRetry(500, false));
        Assert.Null(SwarmWakeRepeat.Warning(204));
    }

    [Fact]
    public async Task Watch_retries_http_400_once_after_sixty_seconds()
    {
        var (root, _, _, board) = OpenRun();
        try
        {
            var codes = new Queue<int>([400, 400]);
            var delays = new List<TimeSpan>();
            var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var result = await SwarmHandoffReview.WatchAsync(true, Handoff, board, 3, (_, _) =>
            {
                var code = codes.Dequeue();
                return Task.FromResult(new SwarmWakeSend(false, "Wake failed. HTTP " + code + ".", code));
            }, () => now, (span, _) =>
            {
                delays.Add(span);
                now = now.Add(span);
                return Task.CompletedTask;
            }, TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(4));
            Assert.Equal([TimeSpan.FromSeconds(60)], delays);
            Assert.Empty(codes);
            Assert.Contains("400", result.Status, StringComparison.Ordinal);
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task Verdict_schedules_no_further_wake()
    {
            var (root, swarmId, runId, board) = OpenRun();
        try
        {
            var sent = 0;
            var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            await SwarmHandoffReview.WatchAsync(true, Handoff, board, 3, (_, _) =>
            {
                sent++;
                var decision = SwarmHandoffReview.Parse("verdict: continue\nissues: Ship it.\nnext: Bind.");
                Assert.NotNull(decision);
                SwarmHandoffReview.Record(swarmId, runId, decision!, root);
                return Task.FromResult(new SwarmWakeSend(true, "Grokbot pinged", 200));
            }, () => now, (span, _) =>
            {
                now = now.Add(span);
                return Task.CompletedTask;
            }, TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(4));
            Assert.Equal(1, sent);
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task Timeout_requests_one_final_wake()
    {
        var (root, _, _, board) = OpenRun();
        try
        {
            var sent = 0;
            var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var result = await SwarmHandoffReview.WatchAsync(true, Handoff, board, 3, (_, _) =>
            {
                sent++;
                return Task.FromResult(new SwarmWakeSend(true, "Grokbot pinged", 204));
            }, () => now, (span, _) =>
            {
                now = now.Add(span);
                return Task.CompletedTask;
            }, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), TimeSpan.FromHours(1));
            Assert.Equal(2, sent);
            Assert.Equal("Grokbot review timed out", result.Status);
            Assert.Equal("Grokbot review timed out", result.Transcript);
            Assert.Null(result.Instruction);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void Panel_text_uses_the_wake_clock_and_status()
    {
        var at = new DateTimeOffset(2026, 10, 14, 3, 41, 8, TimeSpan.Zero);
        Assert.Equal("Wake sent 03:41, HTTP 204", SwarmWakeRepeat.Panel(at, 204));
        Assert.Equal("Wake sent 03:41, HTTP none", SwarmWakeRepeat.Panel(at, null));
    }

    private static (string Root, string SwarmId, string RunId, SwarmBoard Board) OpenRun()
    {
        var root = Path.Combine(Path.GetTempPath(), "xai-desk-wake-repeat-" + Guid.NewGuid().ToString("N"));
        var swarmId = Guid.NewGuid().ToString("N");
        var runId = Guid.NewGuid().ToString("N");
        var board = SwarmBoard.Create("Review the handoff",
            [new SwarmTask { Id = "task-1", Title = "Write the panel", Status = SwarmStatus.Doing }],
            Path.Combine(root, swarmId, "runs"), runId, DoctrineFixture.Case);
        return (root, swarmId, runId, board);
    }

    private static void Delete(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }
}
