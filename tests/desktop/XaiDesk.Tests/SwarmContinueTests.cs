using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Sovereign.Desktop.Connector;
using Xunit;

namespace XaiDesk.Tests;

public sealed class SwarmContinueTests
{
    [Fact]
    public void Continue_resumes_the_manager()
    {
        var fx = Open(SwarmStatus.Doing);
        try
        {
            var result = SwarmContinue.Apply(fx.Desk, fx.SwarmId, fx.RunId, "continue", "  Ship the listener.  ", fx.Data);
            Assert.Equal("posted", result.Outcome);
            Assert.True(result.Resume);
            Assert.Equal("Grokbot review: continue. Ship the listener.", result.Text);
            Assert.Equal(fx.SwarmId, fx.Desk.DeliveredId);
            Assert.Equal("Grok Bot", Assert.Single(fx.Desk.Authors));
            Assert.Equal(result.Text, Assert.Single(fx.Desk.Messages));
            Assert.Equal("continue", fx.Desk.Composer);
            Assert.Equal(1, fx.Desk.SendCount);
            var saved = SwarmBoard.Read(fx.Board.BoardPath);
            Assert.Equal(1, saved.ReviewHandled);
            Assert.Equal(1, saved.ReviewLoops);
            Assert.False(File.Exists(Queue(fx)));
            AssertCurrent(fx);
        }
        finally { Delete(fx); }
    }

    [Fact]
    public void Fix_reopens_the_reviewed_task_and_backs_up_the_board()
    {
        var fx = Open(SwarmStatus.Review);
        try
        {
            var result = SwarmContinue.Apply(fx.Desk, fx.SwarmId, fx.RunId, "fix", "The proof is thin.", fx.Data);
            Assert.Equal("posted", result.Outcome);
            Assert.True(result.Resume);
            Assert.Equal("Grokbot review: fix. The proof is thin.", result.Text);
            Assert.Equal(1, fx.Desk.SendCount);
            Assert.Equal("continue", fx.Desk.Composer);
            var bak = fx.Board.BoardPath + ".bak";
            Assert.True(File.Exists(bak));
            Assert.Equal(SwarmStatus.Review, SwarmBoard.Read(bak).TaskById("task-1").Status);
            var saved = SwarmBoard.Read(fx.Board.BoardPath);
            Assert.Equal(SwarmStatus.Todo, saved.TaskById("task-1").Status);
            Assert.Equal(SwarmStatus.Done, saved.TaskById("task-2").Status);
            Assert.Equal(1, saved.ReviewHandled);
            var again = SwarmContinue.Apply(fx.Desk, fx.SwarmId, fx.RunId, "fix", "The proof is thin.", fx.Data);
            Assert.Equal("ignored", again.Outcome);
            Assert.Equal(SwarmStatus.Todo, SwarmBoard.Read(fx.Board.BoardPath).TaskById("task-1").Status);
            Assert.Equal(1, fx.Desk.SendCount);
            Assert.Single(fx.Desk.Messages);
            AssertCurrent(fx);
        }
        finally { Delete(fx); }
    }

    [Fact]
    public void Done_does_not_resume()
    {
        var fx = Open(SwarmStatus.Doing);
        try
        {
            var result = SwarmContinue.Apply(fx.Desk, fx.SwarmId, fx.RunId, "done", "   ", fx.Data);
            Assert.Equal("posted", result.Outcome);
            Assert.False(result.Resume);
            Assert.Equal("Grokbot review: done.", result.Text);
            Assert.Equal(result.Text, Assert.Single(fx.Desk.Messages));
            Assert.Equal("Grok Bot", Assert.Single(fx.Desk.Authors));
            Assert.Equal(0, fx.Desk.SendCount);
            Assert.Null(fx.Desk.Composer);
            Assert.Equal(1, SwarmBoard.Read(fx.Board.BoardPath).ReviewHandled);
            AssertCurrent(fx);
        }
        finally { Delete(fx); }
    }

    [Fact]
    public void Wrong_run_id_is_rejected()
    {
        var fx = Open(SwarmStatus.Review);
        try
        {
            var missing = Guid.NewGuid().ToString("N");
            var error = Assert.Throws<KeyNotFoundException>(() => SwarmContinue.Apply(fx.Desk, fx.SwarmId, missing, "continue", "no", fx.Data));
            Assert.Contains("not on disk", error.Message, StringComparison.Ordinal);
            Assert.Empty(fx.Desk.Messages);
            Assert.Equal(0, fx.Desk.SendCount);
            Assert.Equal(0, SwarmBoard.Read(fx.Board.BoardPath).ReviewHandled);
            Assert.Equal(SwarmStatus.Review, SwarmBoard.Read(fx.Board.BoardPath).TaskById("task-1").Status);
            Assert.False(File.Exists(fx.Board.BoardPath + ".bak"));
            Assert.False(File.Exists(Queue(fx)));
            AssertCurrent(fx);
        }
        finally { Delete(fx); }
    }

    [Fact]
    public void Duplicate_call_is_ignored()
    {
        var fx = Open(SwarmStatus.Doing);
        try
        {
            var first = SwarmContinue.Apply(fx.Desk, fx.SwarmId, fx.RunId, "continue", "Ship the listener.", fx.Data);
            var second = SwarmContinue.Apply(fx.Desk, fx.SwarmId, fx.RunId, "continue", "Ship the listener again.", fx.Data);
            Assert.Equal("posted", first.Outcome);
            Assert.Equal("ignored", second.Outcome);
            Assert.Equal("Grokbot review: continue. Ship the listener.", Assert.Single(fx.Desk.Messages));
            Assert.Equal(1, fx.Desk.SendCount);
            Assert.Equal(1, SwarmBoard.Read(fx.Board.BoardPath).ReviewHandled);
            AssertCurrent(fx);
        }
        finally { Delete(fx); }
    }

    [Fact]
    public void Closed_chat_queues_and_deliver_returns_it_on_open()
    {
        var fx = Open(SwarmStatus.Doing, open: false);
        try
        {
            var result = SwarmContinue.Apply(fx.Desk, fx.SwarmId, fx.RunId, "continue", "Ship the listener.", fx.Data);
            Assert.Equal("queued", result.Outcome);
            Assert.Empty(fx.Desk.Messages);
            Assert.Null(fx.Desk.DeliveredId);
            Assert.Equal(0, fx.Desk.SendCount);
            var queue = Queue(fx);
            Assert.True(File.Exists(queue));
            Assert.EndsWith(Path.Combine("grokbot-review-queue", fx.SwarmId + ".json"), queue, StringComparison.OrdinalIgnoreCase);
            var delivered = SwarmContinue.Deliver(fx.SwarmId, fx.Data);
            var item = Assert.Single(delivered);
            Assert.Equal("Grokbot review: continue. Ship the listener.", item.Text);
            Assert.True(item.Resume);
            Assert.Equal(1, item.ReviewId);
            Assert.False(File.Exists(queue));
            Assert.Empty(SwarmContinue.Deliver(fx.SwarmId, fx.Data));
            Assert.Equal(1, SwarmBoard.Read(fx.Board.BoardPath).ReviewHandled);
            AssertCurrent(fx);
        }
        finally { Delete(fx); }
    }

    [Fact]
    public void Status_is_read_only_and_reviews_off_and_no_open_review_are_rejected()
    {
        var fx = Open(SwarmStatus.Review);
        try
        {
            var text = SwarmContinue.Status(fx.Desk, fx.SwarmId, fx.RunId);
            Assert.Contains("run_id\t" + fx.RunId, text, StringComparison.Ordinal);
            Assert.Contains("review_status\tWaiting for review", text, StringComparison.Ordinal);
            Assert.Contains("task-1\treview", text, StringComparison.Ordinal);
            Assert.Contains("task-2\tdone", text, StringComparison.Ordinal);
            Assert.False(File.Exists(fx.Board.BoardPath + ".bak"));
            Assert.Equal(0, SwarmBoard.Read(fx.Board.BoardPath).ReviewHandled);
            AssertCurrent(fx);

            fx.Desk.Reviews = false;
            var off = Assert.Throws<InvalidOperationException>(() => SwarmContinue.Apply(fx.Desk, fx.SwarmId, fx.RunId, "continue", "no", fx.Data));
            Assert.Equal("Grokbot reviews handoffs is off.", off.Message);
            Assert.Throws<InvalidOperationException>(() => SwarmContinue.Status(fx.Desk, fx.SwarmId, fx.RunId));
            Assert.Equal(0, fx.Desk.SendCount);
            AssertCurrent(fx);
        }
        finally { Delete(fx); }

        var idle = Open(SwarmStatus.Review, loops: 0);
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() => SwarmContinue.Apply(idle.Desk, idle.SwarmId, idle.RunId, "fix", "no", idle.Data));
            Assert.Equal("No handoff review is open.", error.Message);
            Assert.Equal(0, idle.Desk.SendCount);
            Assert.Equal(SwarmStatus.Review, SwarmBoard.Read(idle.Board.BoardPath).TaskById("task-1").Status);
            AssertCurrent(idle);
        }
        finally { Delete(idle); }
    }

    [Fact]
    public void Fix_names_the_reopened_task_and_keeps_the_review_note()
    {
        var fx = Open(SwarmStatus.Review);
        try
        {
            var decision = new SwarmReviewDecision("fix", "The proof is thin.", "Add the check.");
            var stamp = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
            Assert.Equal("Reviewed (fix)", SwarmHandoffReview.Record(fx.SwarmId, fx.RunId, decision, fx.Root, reviewedAt: stamp));
            var result = SwarmContinue.Apply(fx.Desk, fx.SwarmId, fx.RunId, "fix", "The proof is thin.", fx.Data);
            Assert.Equal("posted", result.Outcome);
            Assert.Equal("Grokbot review: fix. The proof is thin.", result.Text);
            var saved = SwarmBoard.Read(fx.Board.BoardPath);
            Assert.Equal("Reviewed (fix)", saved.ReviewStatus);
            Assert.Contains("The proof is thin.", saved.ReviewNote, StringComparison.Ordinal);
            Assert.Contains("2026-03-04T05:06:07Z", saved.ReviewNote, StringComparison.Ordinal);
            Assert.Contains("Reopened task-1", saved.ReviewNote, StringComparison.Ordinal);
            Assert.Equal(SwarmStatus.Todo, saved.TaskById("task-1").Status);
            Assert.Equal(SwarmStatus.Done, saved.TaskById("task-2").Status);
            AssertCurrent(fx);
        }
        finally { Delete(fx); }
    }

    [Fact]
    public void Fix_without_a_review_task_does_not_resume()
    {
        var fx = Open(SwarmStatus.Doing);
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() => SwarmContinue.Apply(fx.Desk, fx.SwarmId, fx.RunId, "fix", "nothing to reopen", fx.Data));
            Assert.Equal("No task is in review.", error.Message);
            Assert.Equal(0, fx.Desk.SendCount);
            Assert.Empty(fx.Desk.Messages);
            Assert.Equal(0, SwarmBoard.Read(fx.Board.BoardPath).ReviewHandled);
            Assert.Equal(SwarmStatus.Doing, SwarmBoard.Read(fx.Board.BoardPath).TaskById("task-1").Status);
            AssertCurrent(fx);
        }
        finally { Delete(fx); }
    }

    [Fact]
    public void Wake_json_tells_the_reviewer_to_call_swarm_continue()
    {
        var fx = Open(SwarmStatus.Doing);
        try
        {
            var json = SwarmHandoffReview.WakeJson(fx.Board, "SOVEREIGN_HANDOFF\nverified: ready\nEND_SOVEREIGN_HANDOFF");
            using var document = JsonDocument.Parse(json);
            Assert.Equal(SwarmHandoffReview.ReviewerInstruction, document.RootElement.GetProperty("reviewer").GetString());
            Assert.Contains("swarm_continue(swarm_id, run_id, verdict, notes)", json, StringComparison.Ordinal);
            Assert.Contains("swarm_run_status(swarm_id, run_id)", json, StringComparison.Ordinal);
            Assert.DoesNotContain(fx.Root, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("current.txt", json, StringComparison.Ordinal);
        }
        finally { Delete(fx); }
    }

    [Fact]
    public async Task Connector_calls_status_without_saving_and_continue_resumes()
    {
        var fx = Open(SwarmStatus.Review);
        var audit = Path.Combine(fx.Root, "audit.log");
        await using var host = new ConnectorHost(new FixedSecrets("swarm-continue-test"), fx.Desk, new ConnectorAudit(audit));
        try
        {
            var error = await host.ListenForChecksAsync(IPAddress.Loopback, 0, ConnectorBindMode.Loopback);
            Assert.Null(error);
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "swarm-continue-test");
            var list = await http.PostAsync(host.Url, Rpc("tools/list", null));
            var listed = await list.Content.ReadAsStringAsync();
            Assert.Contains("\"swarm_continue\"", listed, StringComparison.Ordinal);
            Assert.Contains("\"swarm_run_status\"", listed, StringComparison.Ordinal);

            var status = await Call(http, host.Url!, "swarm_run_status", new { swarm_id = fx.SwarmId, run_id = fx.RunId });
            Assert.Contains("task-1\treview", status, StringComparison.Ordinal);
            Assert.False(File.Exists(fx.Board.BoardPath + ".bak"));

            var continued = await Call(http, host.Url!, "swarm_continue", new { swarm_id = fx.SwarmId, run_id = fx.RunId, verdict = "fix", notes = "The proof is thin." });
            Assert.StartsWith("posted\tGrokbot review: fix. The proof is thin.", continued, StringComparison.Ordinal);
            Assert.Equal(SwarmStatus.Todo, SwarmBoard.Read(fx.Board.BoardPath).TaskById("task-1").Status);
            Assert.True(File.Exists(fx.Board.BoardPath + ".bak"));
            Assert.Equal(1, fx.Desk.SendCount);
            Assert.Equal("continue", fx.Desk.Composer);
            AssertCurrent(fx);
        }
        finally { Delete(fx); }
    }

    private static async Task<string> Call(HttpClient http, string url, string tool, object arguments)
    {
        var response = await http.PostAsync(url, Rpc("tools/call", new { name = tool, arguments }));
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        var result = json.RootElement.GetProperty("result");
        Assert.False(result.GetProperty("isError").GetBoolean());
        return result.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
    }

    private static StringContent Rpc(string method, object? parameters) =>
        new(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = parameters }), Encoding.UTF8, "application/json");

    private static string Queue(Fixture fx) => Path.Combine(fx.Data, "grokbot-review-queue", fx.SwarmId + ".json");

    private static void AssertCurrent(Fixture fx)
    {
        Assert.Equal(fx.CurrentBytes, File.ReadAllBytes(fx.Current));
        Assert.Single(Directory.GetFiles(fx.Root, "current.txt", SearchOption.AllDirectories));
        if (Directory.Exists(fx.Data))
            Assert.Empty(Directory.GetFiles(fx.Data, "current.txt", SearchOption.AllDirectories));
    }

    private static Fixture Open(string taskStatus, int loops = 1, bool open = true, bool reviews = true)
    {
        var root = Path.Combine(Path.GetTempPath(), "xai-desk-continue-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(Path.GetTempPath(), "xai-desk-continue-data-" + Guid.NewGuid().ToString("N"));
        var swarmId = Guid.NewGuid().ToString("N");
        var runId = Guid.NewGuid().ToString("N");
        var board = SwarmBoard.Create("Review the handoff",
        [
            new SwarmTask { Id = "task-1", Title = "Write the panel", Status = taskStatus, Assignee = "worker-a" },
            new SwarmTask { Id = "task-2", Title = "Leave this", Status = SwarmStatus.Done }
        ], Path.Combine(root, swarmId, "runs"), runId);
        if (loops > 0)
        {
            board.ReviewLoops = loops;
            board.ReviewStatus = "Waiting for review";
            board.Save();
        }
        var bak = board.BoardPath + ".bak";
        if (File.Exists(bak)) File.Delete(bak);
        var current = Path.Combine(root, swarmId, "current.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(current)!);
        File.WriteAllText(current, "keep-current");
        return new Fixture(root, data, current, File.ReadAllBytes(current), swarmId, runId, board, new ReviewDesk { Root = root, Open = open, Reviews = reviews });
    }

    private static void Delete(Fixture fx)
    {
        if (Directory.Exists(fx.Root)) Directory.Delete(fx.Root, true);
        if (Directory.Exists(fx.Data)) Directory.Delete(fx.Data, true);
    }

    private sealed record Fixture(string Root, string Data, string Current, byte[] CurrentBytes, string SwarmId, string RunId, SwarmBoard Board, ReviewDesk Desk);

    private sealed class FixedSecrets(string token) : IConnectorSecrets
    {
        public string? Current() => token;
        public string Generate() => token;
        public void Revoke() { }
    }

    private sealed class ReviewDesk : IGrokBotDesk
    {
        public string Root { get; init; } = "";
        public bool Open { get; set; } = true;
        public bool Reviews { get; set; } = true;
        public string? DeliveredId { get; private set; }
        public List<string> Messages { get; } = [];
        public List<string> Authors { get; } = [];
        public int SendCount { get; private set; }
        public string? Composer { get; private set; }
        public bool GrokbotReviewsHandoffs(string conversationId) => Reviews;
        public bool ConversationOpen(string conversationId) => Open;
        public void DeliverSwarmReview(string conversationId, string text, bool resume)
        {
            if (!Open) throw new KeyNotFoundException("That chat is not open.");
            DeliveredId = conversationId;
            Authors.Add("Grok Bot");
            Messages.Add(text);
            if (!resume) return;
            Composer = "continue";
            SendCount++;
        }
        public string? SwarmBoardRoot() => Root;
        public GrokBotStatus Status() => new("0.3.60", "Cursor", "grok-4.7", false);
        public IReadOnlyList<GrokBotChat> ListChats() => [];
        public IReadOnlyList<GrokBotMessage> ReadChat(string? conversation, int limit) => [];
        public void SendToChat(string conversation, string text) { }
        public IReadOnlyList<GrokBotMessage> ReadSharedRoom(int limit) => [];
        public void PostSharedRoom(string text) { }
        public IReadOnlyList<GrokBotSubagent> ListSubagents(string conversation) => [];
        public GrokBotSubagent? ReadSubagent(string id) => null;
        public void NoteGrokbotPoll() { }
        public IReadOnlyList<GrokbotMail> ListGrokbotInbox(string? since) => [];
        public GrokbotMail ReplyGrokbotInbox(string messageId, string text) => new(messageId, text, false, GrokbotMailStatus.Answered, DateTimeOffset.UtcNow, text, DateTimeOffset.UtcNow);
        public GrokbotPng CaptureGrokbotScreenshot() => new("off", []);
        public string GrokbotUiTree() => "";
        public string GrokbotClick(string? controlId, int? x, int? y) => "";
        public string GrokbotType(string controlId, string text) => "";
        public string GrokbotKey(string keys) => "";
        public string GrokbotNavigate(string page) => "";
        public void RecordGrokbotTool(string tool, string args, string result) { }
        public string ReadSwarmBoard(string? swarmId, string? runId = null) => "";
        public string SubmitSwarmResult(string taskId, string result, string proof, string? swarmId = null, string? runId = null) => "";
    }
}
