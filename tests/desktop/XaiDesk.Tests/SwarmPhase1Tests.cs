using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Sovereign.Desktop.Connector;
using Xunit;

namespace XaiDesk.Tests;

public sealed class SwarmPhase1Tests
{
    [Fact]
    public async Task Engineer_task_moves_todo_doing_review_done_with_real_proof()
    {
        var root = Temp();
        var work = Path.Combine(root, "work");
        try
        {
            var board = SwarmLoop.OpenGoal("Write the phase 1 proof file", "Write proof.txt", SwarmRole.Engineer, "One line in proof.txt", root);
            var id = board.Tasks.Single().Id;
            Assert.Equal(SwarmStatus.Todo, board.TaskById(id).Status);
            board.TaskById(id).Files.Add(Path.Combine(work, "proof.txt"));
            board.Save();
            DoctrineFixture.ReadyForWork(board, id);
            var started = SwarmLoop.Begin(board, id, "engineer");
            Assert.Equal("running", started.State);
            Assert.Contains("swarm engineer", started.RolePrompt, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(SwarmStatus.Doing, SwarmBoard.Load(board.Id, root).TaskById(id).Status);

            var submitted = await SwarmLoop.EngineerProofAsync(board, id, work);
            Assert.Equal(SwarmStatus.Review, submitted.Status);
            Assert.Contains("--- a/proof.txt", submitted.Result, StringComparison.Ordinal);
            Assert.Contains("exit 0", submitted.Proof, StringComparison.Ordinal);
            Assert.Contains("swarm phase 1 " + id, submitted.Proof, StringComparison.Ordinal);
            Assert.NotEqual(SwarmStatus.Done, submitted.Status);

            var verified = await SwarmLoop.VerifyProofAsync(board, id, work);
            Assert.Equal(SwarmStatus.Done, verified.Status);
            Assert.Contains("Claim:", verified.VerifierNote, StringComparison.Ordinal);
            Assert.Contains("How tested:", verified.VerifierNote, StringComparison.Ordinal);
            Assert.Contains("Evidence:", verified.VerifierNote, StringComparison.Ordinal);
            Assert.Contains("Paths covered:", verified.VerifierNote, StringComparison.Ordinal);
            Assert.Contains("Verdict: pass because the file contained the task line", verified.VerifierNote, StringComparison.Ordinal);
            Assert.Contains("swarm phase 1 " + id, verified.VerifierNote, StringComparison.Ordinal);
            var saved = SwarmBoard.Load(board.Id, root).TaskById(id);
            Assert.Equal(SwarmStatus.Done, saved.Status);
            Assert.False(string.IsNullOrWhiteSpace(saved.Proof));
            Assert.Contains(Path.Combine(root, board.Id, "board.json"), board.BoardPath, StringComparison.OrdinalIgnoreCase);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void Verifier_fail_returns_to_todo_twice_then_failed()
    {
        var root = Temp();
        try
        {
            var board = SwarmLoop.OpenGoal("Check a claim", "Check it", SwarmRole.Researcher, "sources required", root);
            var id = board.Tasks.Single().Id;
            DoctrineFixture.ReadyForWork(board, id);
            board.Start(id, "researcher");
            board.Submit(id, "finding", "source: local note");
            var first = board.Verify(id, false, "source missing");
            Assert.Equal(SwarmStatus.Todo, first.Status);
            Assert.Equal(1, first.Retries);
            Assert.Equal("source missing", first.VerifierNote);

            board.Start(id, "researcher");
            board.Submit(id, "finding", "source: local note");
            var second = board.Verify(id, false, "still missing");
            Assert.Equal(SwarmStatus.Todo, second.Status);
            Assert.Equal(2, second.Retries);

            board.Start(id, "researcher");
            board.Submit(id, "finding", "source: local note");
            var third = board.Verify(id, false, "gave up");
            Assert.Equal(SwarmStatus.Failed, third.Status);
            Assert.Equal("gave up", third.VerifierNote);
            Assert.Throws<InvalidOperationException>(() => board.Verify(id, true, "too late"));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void Empty_proof_cannot_reach_review_or_done()
    {
        var root = Temp();
        try
        {
            var board = SwarmLoop.OpenGoal("Need proof", "Do the work", SwarmRole.Engineer, "show the command", root);
            var id = board.Tasks.Single().Id;
            DoctrineFixture.ReadyForWork(board, id);
            board.Start(id, "engineer");
            Assert.Throws<ArgumentException>(() => board.Submit(id, "diff", " "));
            Assert.Equal(SwarmStatus.Doing, board.TaskById(id).Status);
            board.Submit(id, "diff", "real output");
            board.TaskById(id).Proof = "";
            Assert.Throws<InvalidOperationException>(() => board.Verify(id, true, "no"));
            Assert.Equal(SwarmStatus.Review, board.TaskById(id).Status);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void Verifier_and_manager_load_the_verify_result_skill()
    {
        var path = VerifyResultSkill.FindFile();
        Assert.NotNull(path);
        Assert.EndsWith(Path.Combine(".cursor", "skills", "verify-result", "SKILL.md"), path, StringComparison.OrdinalIgnoreCase);
        var skill = VerifyResultSkill.Text();
        Assert.Contains("name: verify-result", skill, StringComparison.Ordinal);
        Assert.Contains("Claim, How tested, Evidence, Paths covered", skill, StringComparison.Ordinal);
        Assert.Contains("risky, costly, or irreversible actions", skill, StringComparison.OrdinalIgnoreCase);
        var index = File.ReadAllText(Path.Combine(Directory.GetParent(path)!.Parent!.FullName, "index.json"));
        Assert.Contains("\"verify-result\"", index, StringComparison.Ordinal);
        Assert.Contains("\"verifier\"", index, StringComparison.Ordinal);
        Assert.Contains("\"manager\"", index, StringComparison.Ordinal);

        var verifier = SwarmRoles.For(SwarmRole.Verifier);
        Assert.Contains("Follow the verify-result skill on every task", verifier, StringComparison.Ordinal);
        Assert.Contains("# Verify Result", verifier, StringComparison.Ordinal);
        Assert.Contains("Verdict with reason", verifier, StringComparison.Ordinal);

        var manager = SwarmRoles.Manager(Path.Combine(Path.GetTempPath(), "board"));
        Assert.Contains("Before you tell the user anything is done", manager, StringComparison.Ordinal);
        Assert.Contains("# Verify Result", manager, StringComparison.Ordinal);
        Assert.Contains("Weak or missing proof is not done", manager, StringComparison.Ordinal);
    }

    [Fact]
    public void Single_model_manager_brief_omits_verifier_must_differ_and_mixed_keeps_it()
    {
        var board = Path.Combine(Path.GetTempPath(), "board");
        const string rule = "The verifier modelId must differ from the engineer modelId";
        var single = SwarmRoles.Manager(board, mixedModels: false);
        var mixed = SwarmRoles.Manager(board, mixedModels: true);
        Assert.Equal(single, SwarmRoles.Manager(board));
        Assert.DoesNotContain(rule, single, StringComparison.Ordinal);
        Assert.DoesNotContain("must differ", single, StringComparison.Ordinal);
        Assert.Contains(rule, mixed, StringComparison.Ordinal);
        Assert.DoesNotContain("grokbot-", single, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("grokbot-", mixed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Manager_brief_never_offers_grokbot_as_a_worker_but_still_asks_for_the_handoff_block()
    {
        var board = Path.Combine(Path.GetTempPath(), "board");
        foreach (var brief in new[] { SwarmRoles.Manager(board), SwarmRoles.Manager(board, mixedModels: true), SwarmRoles.Manager(board, "Project memory: none") })
        {
            Assert.DoesNotContain("grokbot-", brief, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("wake ping", brief, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("wake role", brief, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("swarm_submit_result", brief, StringComparison.Ordinal);
            Assert.DoesNotContain("stay wake roles", brief, StringComparison.Ordinal);
            Assert.DoesNotContain("waiting on Grok Bot", brief, StringComparison.Ordinal);
            Assert.Contains("Grok Bot is not a worker and is never on the board", brief, StringComparison.Ordinal);
            Assert.Contains("grandparent above you", brief, StringComparison.Ordinal);
            Assert.Contains("swarm_continue", brief, StringComparison.Ordinal);
            Assert.Contains("all four roles: engineer, researcher, scout, and verifier", brief, StringComparison.Ordinal);
            Assert.Contains("scout", brief, StringComparison.Ordinal);
            Assert.Contains("SOVEREIGN_HANDOFF", brief, StringComparison.Ordinal);
            Assert.Contains("END_SOVEREIGN_HANDOFF", brief, StringComparison.Ordinal);
        }
        foreach (var role in new[] { SwarmRole.Engineer, SwarmRole.Researcher, SwarmRole.Scout, SwarmRole.Investigator, SwarmRole.Verifier, "worker", "grokbot-" + "re" + "search" })
            Assert.DoesNotContain("You are Grok Bot", SwarmRoles.For(role), StringComparison.Ordinal);
        var scout = SwarmRoles.For(SwarmRole.Scout);
        Assert.Contains("second and independent lookup", scout, StringComparison.Ordinal);
        Assert.Contains("Cross-check", scout, StringComparison.Ordinal);
        Assert.Contains("source", scout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verifier_skill_passes_a_rerun_and_fails_weak_proof()
    {
        var root = Temp();
        var work = Path.Combine(root, "work");
        try
        {
            var passBoard = SwarmLoop.OpenGoal("Prove the line is on disk", "Write proof.txt", SwarmRole.Engineer, "One line in proof.txt", root);
            var passId = passBoard.Tasks.Single().Id;
            passBoard.TaskById(passId).Files.Add(Path.Combine(work, "proof.txt"));
            passBoard.Save();
            DoctrineFixture.ReadyForWork(passBoard, passId);
            passBoard.Start(passId, "engineer");
            await SwarmLoop.EngineerProofAsync(passBoard, passId, work);
            var passed = await SwarmLoop.VerifyProofAsync(passBoard, passId, work);
            Assert.Equal(SwarmStatus.Done, passed.Status);
            Assert.Contains("Claim: proof.txt contains swarm phase 1 " + passId, passed.VerifierNote, StringComparison.Ordinal);
            Assert.Contains("How tested: Read the current proof.txt in this process at ", passed.VerifierNote, StringComparison.Ordinal);
            Assert.Contains("Evidence: exit 0", passed.VerifierNote, StringComparison.Ordinal);
            Assert.Contains("Paths covered: Success: the file exists and the task line is in it.", passed.VerifierNote, StringComparison.Ordinal);
            Assert.Contains("Verdict: pass because the file contained the task line", passed.VerifierNote, StringComparison.Ordinal);

            var failBoard = SwarmLoop.OpenGoal("Do not accept a slogan", "Claim it builds", SwarmRole.Engineer, "No command output", root);
            var failId = failBoard.Tasks.Single().Id;
            DoctrineFixture.ReadyForWork(failBoard, failId);
            failBoard.Start(failId, "engineer");
            failBoard.Submit(failId, "finished", "it builds");
            var failed = await SwarmLoop.VerifyProofAsync(failBoard, failId, work);
            Assert.Equal(SwarmStatus.Todo, failed.Status);
            Assert.NotEqual(SwarmStatus.Done, failed.Status);
            Assert.Equal(1, failed.Retries);
            Assert.Contains("Verdict: fail because the proof is missing Claim, How tested, Evidence, Paths covered, Verdict", failed.VerifierNote, StringComparison.Ordinal);
            Assert.Contains("\"It builds\" or \"it opens\" is not proof", failed.VerifierNote, StringComparison.Ordinal);
            Assert.Contains("Claim:", failed.VerifierNote, StringComparison.Ordinal);
            Assert.Contains("How tested:", failed.VerifierNote, StringComparison.Ordinal);
            Assert.Contains("Evidence:", failed.VerifierNote, StringComparison.Ordinal);
            Assert.Contains("Paths covered:", failed.VerifierNote, StringComparison.Ordinal);
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task Grokbot_cannot_submit_a_worker_result_but_can_still_read_the_board()
    {
        var root = Temp();
        try
        {
            var board = SwarmLoop.OpenGoal("Look up the note", "Read the desk note", SwarmRole.Researcher, "The note is on the board only.", root);
            var task = board.Tasks.Single();
            DoctrineFixture.ReadyForWork(board, task.Id);
            board.Start(task.Id, "researcher");
            Assert.Equal(SwarmStatus.Doing, board.TaskById(task.Id).Status);

            var desk = new SwarmDesk(root);
            var secrets = new MemoryConnectorSecrets();
            var token = secrets.Generate();
            var audit = Path.Combine(root, "audit.log");
            await using var host = new ConnectorHost(secrets, desk, new ConnectorAudit(audit));
            var error = await host.ListenForChecksAsync(IPAddress.Loopback, 0, ConnectorBindMode.Loopback);
            Assert.Null(error);
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var call = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tools/call",
                @params = new
                {
                    name = "swarm_submit_result",
                    arguments = new { task_id = task.Id, swarm_id = board.Id, result = "The note is local.", proof = "source: board " + task.Id }
                }
            });
            var response = await http.PostAsync(host.Url, new StringContent(call, Encoding.UTF8, "application/json"));
            var body = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using (var json = JsonDocument.Parse(body))
            {
                var result = json.RootElement.GetProperty("result");
                Assert.True(result.GetProperty("isError").GetBoolean());
                Assert.Contains("Grok Bot is not a swarm worker", result.GetProperty("content")[0].GetProperty("text").GetString(), StringComparison.Ordinal);
            }
            Assert.DoesNotContain(token, body, StringComparison.Ordinal);
            var unchanged = SwarmBoard.Load(board.Id, root).TaskById(task.Id);
            Assert.Equal(SwarmStatus.Doing, unchanged.Status);
            Assert.Equal("", unchanged.Result);
            Assert.Equal("", unchanged.Proof);
            Assert.DoesNotContain(token, File.ReadAllText(audit), StringComparison.Ordinal);

            // The tool list still carries the review tools and says the task path is closed.
            var list = await http.PostAsync(host.Url, new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 3, method = "tools/list" }), Encoding.UTF8, "application/json"));
            var tools = await list.Content.ReadAsStringAsync();
            Assert.Contains("Grok Bot is not a swarm worker", tools, StringComparison.Ordinal);
            Assert.Contains("\"swarm_continue\"", tools, StringComparison.Ordinal);
            Assert.Contains("\"swarm_run_status\"", tools, StringComparison.Ordinal);

            // The reviewer still reads live runs.
            var listed = await http.PostAsync(host.Url, new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "tools/call",
                @params = new { name = "read_swarm_board", arguments = new { swarm_id = board.Id } }
            }), Encoding.UTF8, "application/json"));
            var boardText = await listed.Content.ReadAsStringAsync();
            Assert.Contains(board.Id, boardText, StringComparison.Ordinal);
            Assert.Contains("doing", boardText, StringComparison.Ordinal);
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task Empty_task_id_records_review_on_the_run_and_does_not_mark_done()
    {
        var swarmId = Guid.NewGuid().ToString("N");
        var runId = Guid.NewGuid().ToString("N");
        var swarmFolder = Path.Combine(SwarmBoard.DefaultRoot(), swarmId);
        var deskRoot = Temp();
        try
        {
            var board = SwarmBoard.Create("Review the handoff",
            [
                new SwarmTask
                {
                    Id = "task-1",
                    Title = "Write the panel",
                    Status = SwarmStatus.Doing,
                    Assignee = "worker-a"
                }
            ], Path.Combine(SwarmBoard.DefaultRoot(), swarmId, "runs"), runId, DoctrineFixture.Case);
            Assert.Equal(SwarmStatus.Doing, board.TaskById("task-1").Status);

            var desk = new SwarmDesk(deskRoot);
            var secrets = new MemoryConnectorSecrets();
            var token = secrets.Generate();
            var audit = Path.Combine(deskRoot, "audit.log");
            await using var host = new ConnectorHost(secrets, desk, new ConnectorAudit(audit));
            var error = await host.ListenForChecksAsync(IPAddress.Loopback, 0, ConnectorBindMode.Loopback);
            Assert.Null(error);
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var call = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tools/call",
                @params = new
                {
                    name = "swarm_submit_result",
                    arguments = new
                    {
                        task_id = "",
                        swarm_id = swarmId,
                        run_id = runId,
                        result = "verdict: continue\nissues: The port is closed.\nnext: Bind the listener.",
                        proof = ""
                    }
                }
            });
            var response = await http.PostAsync(host.Url, new StringContent(call, Encoding.UTF8, "application/json"));
            var body = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("Reviewed (continue)", body, StringComparison.Ordinal);
            Assert.DoesNotContain(SwarmStatus.Done, body, StringComparison.Ordinal);

            var saved = SwarmBoard.Read(board.BoardPath);
            Assert.Equal("continue", saved.ReviewVerdict);
            Assert.Equal("The port is closed.", saved.ReviewIssues);
            Assert.Equal("Bind the listener.", saved.ReviewNext);
            Assert.Equal("Reviewed (continue)", saved.ReviewStatus);
            Assert.Equal(SwarmStatus.Doing, saved.TaskById("task-1").Status);
            Assert.Equal("", saved.TaskById("task-1").Result);
            Assert.Equal("", saved.TaskById("task-1").Proof);
            Assert.NotEqual(SwarmStatus.Done, saved.TaskById("task-1").Status);
        }
        finally
        {
            Delete(deskRoot);
            if (Directory.Exists(swarmFolder)) Directory.Delete(swarmFolder, true);
        }
    }

    private static string Temp()
    {
        var path = Path.Combine(Path.GetTempPath(), "xai-desk-swarm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Delete(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }

    private sealed class SwarmDesk(string root) : IGrokBotDesk
    {
        public string ReadSwarmBoard(string? swarmId, string? runId = null) => SwarmBoard.ReadText(string.IsNullOrWhiteSpace(swarmId) ? null : swarmId, root, string.IsNullOrWhiteSpace(runId) ? null : runId);
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
        public bool GrokbotReviewsHandoffs(string conversationId) => false;
        public bool ConversationOpen(string conversationId) => false;
        public void DeliverSwarmReview(string conversationId, string text, bool resume) { }
        public string? SwarmBoardRoot() => root;
    }
}
