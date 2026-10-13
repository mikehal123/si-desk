using System.Net;
using System.Text;
using System.Text.Json;
using Sovereign.Desktop.Connector;
using Xunit;

namespace XaiDesk.Tests;

public sealed class SwarmRunHandbackTests
{
    [Fact]
    public void Submit_lands_on_a_newer_run_when_current_is_an_older_open_run()
    {
        var root = Temp();
        try
        {
            var swarm = Guid.NewGuid().ToString("N");
            var older = Open(root, swarm, "older-task");
            var newer = Open(root, swarm, "newer-task");
            Stamp(older, newer);
            var current = Path.Combine(root, swarm, "current.txt");
            File.WriteAllText(current, older.Id, new UTF8Encoding(false));
            var before = File.ReadAllText(current);

            var board = SwarmBoard.RequireTask("newer-task", root, out _, swarm);
            board.Submit("newer-task", "The note is on the newer run.", "proof: newer-task");

            var savedNewer = SwarmBoard.Read(SwarmBoard.RunBoardPath(swarm, newer.Id, root));
            var savedOlder = SwarmBoard.Read(SwarmBoard.RunBoardPath(swarm, older.Id, root));
            Assert.Equal(SwarmStatus.Review, savedNewer.TaskById("newer-task").Status);
            Assert.Equal("proof: newer-task", savedNewer.TaskById("newer-task").Proof);
            Assert.Equal(SwarmStatus.Todo, savedOlder.TaskById("older-task").Status);
            Assert.Equal(before, File.ReadAllText(current));
            Assert.Equal(older.Id, before.Trim());
        }
        finally { Delete(root); }
    }

    [Fact]
    public void Submit_with_run_id_writes_that_board_even_when_a_newer_run_exists()
    {
        var root = Temp();
        try
        {
            var swarm = Guid.NewGuid().ToString("N");
            var older = Open(root, swarm, "shared-task");
            var newer = Open(root, swarm, "shared-task");
            Stamp(older, newer);
            var current = Path.Combine(root, swarm, "current.txt");
            var before = File.ReadAllText(current);

            var board = SwarmBoard.RequireTask("shared-task", root, out _, swarm, older.Id);
            board.Submit("shared-task", "Pinned to the named run.", "proof: shared-task");

            var savedOlder = SwarmBoard.Read(SwarmBoard.RunBoardPath(swarm, older.Id, root));
            var savedNewer = SwarmBoard.Read(SwarmBoard.RunBoardPath(swarm, newer.Id, root));
            Assert.Equal(SwarmStatus.Review, savedOlder.TaskById("shared-task").Status);
            Assert.Equal(SwarmStatus.Todo, savedNewer.TaskById("shared-task").Status);
            Assert.Equal(before, File.ReadAllText(current));
            var named = SwarmBoard.ReadText(swarm, root, older.Id);
            Assert.Contains("\"status\": \"review\"", named, StringComparison.Ordinal);
            Assert.Contains(older.Id, named, StringComparison.Ordinal);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void Unknown_task_returns_the_same_error()
    {
        var root = Temp();
        try
        {
            var swarm = Guid.NewGuid().ToString("N");
            var run = Open(root, swarm, "known-task");
            var missing = Assert.Throws<KeyNotFoundException>(() => SwarmBoard.RequireTask("missing-task", root, out _, swarm));
            var pinned = Assert.Throws<KeyNotFoundException>(() => SwarmBoard.RequireTask("missing-task", root, out _, swarm, run.Id));
            Assert.Equal("That swarm task is not on a board.", missing.Message);
            Assert.Equal("That swarm task is not on a board.", pinned.Message);
            Assert.Equal(SwarmStatus.Todo, SwarmBoard.Read(SwarmBoard.RunBoardPath(swarm, run.Id, root)).TaskById("known-task").Status);
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task Wake_payload_includes_the_run_id()
    {
        var root = Temp();
        try
        {
            var swarm = Guid.NewGuid().ToString("N");
            var run = Open(root, swarm, "wake-task");
            using var json = JsonDocument.Parse(SwarmLoop.WakeJson(run, run.Tasks.Single()));
            Assert.Equal(swarm, json.RootElement.GetProperty("swarm_id").GetString());
            Assert.Equal(run.Id, json.RootElement.GetProperty("run_id").GetString());
            Assert.Equal("wake-task", json.RootElement.GetProperty("task_id").GetString());

            using var handler = new CaptureHandler();
            var receipt = await GrokbotWake.DeliverAsync(handler, "https://wake.example/hook", "wake-key", "wake-task", false, "Hand back", CancellationToken.None, _ => TimeSpan.Zero, swarm, "wake-task", "Hand back", "details", run.Id);
            Assert.Equal("Grokbot pinged", receipt.Note);
            using var posted = JsonDocument.Parse(handler.Body);
            Assert.Equal(run.Id, posted.RootElement.GetProperty("run_id").GetString());
            Assert.Equal(swarm, posted.RootElement.GetProperty("swarm_id").GetString());
        }
        finally { Delete(root); }
    }

    private static SwarmBoard Open(string root, string swarm, string taskId) =>
        SwarmSchedule.OpenRun(swarm, "Hand the result back", [new SwarmTask { Id = taskId, Title = "Hand the result back", Role = SwarmRole.GrokbotResearch, Status = SwarmStatus.Todo }], root);

    private static void Stamp(SwarmBoard older, SwarmBoard newer)
    {
        File.SetLastWriteTimeUtc(older.BoardPath, DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(newer.BoardPath, DateTime.UtcNow);
    }

    private static string Temp()
    {
        var path = Path.Combine(Path.GetTempPath(), "xai-desk-handback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Delete(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string Body { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
