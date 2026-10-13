using Sovereign.Desktop.Connector;
using Xunit;

namespace XaiDesk.Tests;

public sealed class SwarmSectionTests
{
    [Fact]
    public void Missing_board_stays_hidden()
    {
        var root = Temp();
        try
        {
            var id = Guid.NewGuid().ToString("N");
            var path = SwarmSection.BoardFile(id, root);
            Assert.NotNull(path);
            Assert.EndsWith(Path.Combine(id, "board.json"), path, StringComparison.OrdinalIgnoreCase);
            Assert.Null(SwarmSection.TryLoad(id, root));
            Assert.Null(SwarmSection.BoardFile("continue-checks", root));
            Assert.Null(SwarmSection.TryLoad(null, root));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void Reload_reads_the_conversation_board_after_it_reaches_done()
    {
        var root = Temp();
        try
        {
            var id = Guid.NewGuid().ToString("N");
            var board = SwarmBoard.Create("Write a marker", [new SwarmTask { Id = "t-1", Title = "Write marker", Role = "engineer" }], root, id);
            var first = SwarmSection.TryLoad(id, root);
            Assert.NotNull(first);
            Assert.Equal("todo", SwarmSection.StatusLabel(first!.TaskById("t-1").Status));
            Assert.Equal("queued", SwarmSection.PillState("todo"));

            board.Start("t-1", "engineer");
            board.Submit("t-1", "marker", "line");
            var note = "Claim: the marker is on disk\nHow tested: read the board\nEvidence: status done\nPaths covered: success\nVerdict: pass because the marker task is done";
            board.Verify("t-1", true, note);
            var again = SwarmSection.TryLoad(id, root);
            Assert.NotNull(again);
            var task = again!.TaskById("t-1");
            Assert.Equal("done", task.Status);
            Assert.Equal(0, task.Retries);
            Assert.Equal("completed", SwarmSection.PillState(task.Status));
            Assert.Equal("running", SwarmSection.PillState("doing"));
            Assert.Equal("blocked", SwarmSection.PillState("review"));
            Assert.Equal("failed", SwarmSection.PillState("failed"));
            var read = SwarmSection.ReadNote(task.VerifierNote);
            Assert.Equal("the marker is on disk", read.Claim);
            Assert.Equal("pass because the marker task is done", read.Verdict);
            Assert.Equal("status done", read.Evidence);
            Assert.Equal(SwarmLoop.View(again, task).SessionId, SwarmSection.WorkerSessionId(id, "t-1"));
            Assert.Null(SwarmSection.TryLoad(Guid.NewGuid().ToString("N"), root));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void Progress_feed_and_note_describe_a_live_board()
    {
        var progress = SwarmSection.Measure(
        [
            new SwarmTask { Status = "done" },
            new SwarmTask { Status = "doing" },
            new SwarmTask { Status = "todo" }
        ]);
        Assert.Equal(1, progress.Done);
        Assert.Equal(3, progress.Total);
        Assert.Equal(1, progress.Doing);
        Assert.Equal(1, progress.Todo);
        Assert.Equal(1.0 / 3, progress.Fraction, 3);
        Assert.Contains("done 1", progress.Counts);
        Assert.Equal("engineer started Write hello.txt", SwarmSection.FeedLine("todo", new SwarmTask { Role = "engineer", Title = "Write hello.txt", Status = "doing" }));
        Assert.Equal("verifier passed t-hello", SwarmSection.FeedLine("review", new SwarmTask { Id = "t-hello", Role = "verifier", Title = "Check", Status = "done" }));
        Assert.Equal("Grok Bot returned result", SwarmSection.FeedLine("doing", new SwarmTask { Role = "grokbot-research", Title = "Look", Status = "review" }));
        Assert.Null(SwarmSection.FeedLine("doing", new SwarmTask { Role = "engineer", Title = "Write hello.txt", Status = "doing" }));
        Assert.Equal("0:05", SwarmSection.FormatElapsed(TimeSpan.FromSeconds(5)));
        Assert.Equal("1:02:03", SwarmSection.FormatElapsed(new TimeSpan(1, 2, 3)));
        var note = SwarmSection.ReadNote("Claim: a\nHow tested: read\nEvidence: b\nPaths covered: c\nVerdict: pass because c");
        Assert.Equal("a", note.Claim);
        Assert.Equal("b", note.Evidence);
        Assert.Equal("pass because c", note.Verdict);
        Assert.Equal("wrote the file", SwarmSection.LatestLine(new SwarmTask { Result = "wrote the file\nmore", Status = "doing" }));
    }

    private static string Temp()
    {
        var path = Path.Combine(Path.GetTempPath(), "xai-desk-swarm-section-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Delete(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }
}
