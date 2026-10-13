using Sovereign.Desktop.Connector;
using Xunit;

namespace XaiDesk.Tests;

public sealed class SwarmInterruptTests
{
    [Fact]
    public void Interrupt_respawns_once_with_a_narrower_task_then_fails_and_moves_on()
    {
        var root = Path.Combine(Path.GetTempPath(), "xai-desk-swarm-interrupt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var board = SwarmBoard.Create("Trace two files",
            [
                new SwarmTask { Id = "t-a", Title = "Trace title", Role = SwarmRole.Investigator, Details = "Read title.txt and every neighbor.", Status = SwarmStatus.Doing, Assignee = "helper-1" },
                new SwarmTask { Id = "t-b", Title = "Leave this task", Role = SwarmRole.Researcher, Status = SwarmStatus.Todo }
            ], root);

            var first = board.Interrupt("t-a", "helper stopped");
            Assert.Equal(SwarmStatus.Todo, first.Status);
            Assert.Equal(1, first.InterruptRetries);
            Assert.Equal("", first.Assignee);
            Assert.Contains("Interrupted once. helper stopped", first.Result, StringComparison.Ordinal);
            Assert.Contains("Narrowed after an interrupt: do only \"Trace title\". Drop side work.", first.Details, StringComparison.Ordinal);
            Assert.Equal(SwarmStatus.Todo, board.TaskById("t-b").Status);
            Assert.Equal("investigator interrupted Trace title. Respawn once, narrower.", SwarmSection.FeedLine("doing", first));

            var parked = board.Interrupt("t-a", "still parked");
            Assert.Equal(SwarmStatus.Todo, parked.Status);
            Assert.Equal(1, parked.InterruptRetries);

            board.Start("t-a", "helper-2");
            var second = board.Interrupt("t-a", "stopped again");
            Assert.Equal(SwarmStatus.Failed, second.Status);
            Assert.Equal("stopped again", second.Result);
            Assert.Equal(SwarmStatus.Todo, board.TaskById("t-b").Status);

            var again = SwarmBoard.Create("Trace one file",
            [
                new SwarmTask { Id = "t-z", Title = "Trace title", Role = SwarmRole.Investigator, Status = SwarmStatus.Doing, Assignee = "helper-z" }
            ], root, "second-board");
            var narrowed = again.Respawn("t-z", "Respawn requested for this helper only.");
            Assert.Equal(SwarmStatus.Todo, narrowed.Status);
            Assert.Equal(1, narrowed.InterruptRetries);
            var failed = again.Respawn("t-z", "Respawn requested for this helper only.");
            Assert.Equal(SwarmStatus.Failed, failed.Status);
            Assert.Equal(SwarmStatus.Todo, board.TaskById("t-b").Status);

            var saved = SwarmBoard.Load(board.Id, root).TaskById("t-a");
            Assert.Equal(SwarmStatus.Failed, saved.Status);
            Assert.Equal(1, saved.InterruptRetries);
            Assert.Contains("decide once", SwarmRoles.Manager(board.BoardPath), StringComparison.Ordinal);
            Assert.Contains("respawn it once with a narrower scope", SwarmRoles.Manager(board.BoardPath), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Respawn_on_todo_restarts_once_then_fails_and_leaves_the_sibling()
    {
        var root = Path.Combine(Path.GetTempPath(), "xai-desk-swarm-respawn-todo-" + Guid.NewGuid().ToString("N"));
        try
        {
            var board = SwarmBoard.Create("Restart a parked task",
            [
                new SwarmTask { Id = "t-a", Title = "Trace title", Role = SwarmRole.Investigator, Details = "Read title.txt and every neighbor.", Status = SwarmStatus.Todo, Assignee = "helper-1", Result = "waiting" },
                new SwarmTask { Id = "t-b", Title = "Leave this task", Role = SwarmRole.Researcher, Status = SwarmStatus.Todo, Assignee = "keep", Result = "untouched", Details = "leave me" }
            ], root);

            var first = board.Respawn("t-a", "helper stopped");
            Assert.Equal(SwarmStatus.Todo, first.Status);
            Assert.Equal(1, first.InterruptRetries);
            Assert.Equal("", first.Assignee);
            Assert.Equal("Interrupted once. helper stopped", first.Result);
            Assert.Contains("Read title.txt and every neighbor.", first.Details, StringComparison.Ordinal);
            Assert.Contains("Narrowed after an interrupt: do only \"Trace title\". Drop side work. Reason: helper stopped", first.Details, StringComparison.Ordinal);
            AssertSiblingUntouched(board);

            var saved = SwarmBoard.Load(board.Id, root).TaskById("t-a");
            Assert.Equal(SwarmStatus.Todo, saved.Status);
            Assert.Equal(1, saved.InterruptRetries);
            Assert.Equal("", saved.Assignee);
            Assert.Equal("Interrupted once. helper stopped", saved.Result);

            var failed = board.Respawn("t-a", "stop the retry");
            Assert.Equal(SwarmStatus.Failed, failed.Status);
            Assert.Equal("stop the retry", failed.Result);
            Assert.Equal(1, failed.InterruptRetries);
            AssertSiblingUntouched(board);
            Assert.Equal(SwarmStatus.Failed, SwarmBoard.Load(board.Id, root).TaskById("t-a").Status);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Respawn_on_review_restarts_once_then_fails_and_leaves_the_sibling()
    {
        var root = Path.Combine(Path.GetTempPath(), "xai-desk-swarm-respawn-review-" + Guid.NewGuid().ToString("N"));
        try
        {
            var board = SwarmBoard.Create("Restart a review task",
            [
                new SwarmTask { Id = "t-a", Title = "Trace title", Role = SwarmRole.Investigator, Status = SwarmStatus.Review, Assignee = "helper-1", Result = "needs another look" },
                new SwarmTask { Id = "t-b", Title = "Leave this task", Role = SwarmRole.Researcher, Status = SwarmStatus.Todo, Assignee = "keep", Result = "untouched", Details = "leave me" }
            ], root);

            var first = board.Respawn("t-a", "review stopped");
            Assert.Equal(SwarmStatus.Todo, first.Status);
            Assert.Equal(1, first.InterruptRetries);
            Assert.Equal("", first.Assignee);
            Assert.Equal("Interrupted once. review stopped", first.Result);
            Assert.Equal("Narrowed after an interrupt: do only \"Trace title\". Drop side work. Reason: review stopped", first.Details);
            AssertSiblingUntouched(board);

            var saved = SwarmBoard.Load(board.Id, root).TaskById("t-a");
            Assert.Equal(SwarmStatus.Todo, saved.Status);
            Assert.Equal(1, saved.InterruptRetries);
            Assert.Equal("Interrupted once. review stopped", saved.Result);

            var failed = board.Respawn("t-a", "stop the retry");
            Assert.Equal(SwarmStatus.Failed, failed.Status);
            Assert.Equal("stop the retry", failed.Result);
            Assert.Equal(1, failed.InterruptRetries);
            AssertSiblingUntouched(board);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Respawn_on_done_leaves_it_done()
    {
        var root = Path.Combine(Path.GetTempPath(), "xai-desk-swarm-respawn-done-" + Guid.NewGuid().ToString("N"));
        try
        {
            var board = SwarmBoard.Create("Leave a finished task",
            [
                new SwarmTask { Id = "t-a", Title = "Finished", Role = SwarmRole.Investigator, Status = SwarmStatus.Done, Assignee = "helper-1", Result = "shipped", Details = "closed" },
                new SwarmTask { Id = "t-b", Title = "Leave this task", Role = SwarmRole.Researcher, Status = SwarmStatus.Todo, Assignee = "keep", Result = "untouched", Details = "leave me" }
            ], root);

            var same = board.Respawn("t-a", "should not apply");
            Assert.Equal(SwarmStatus.Done, same.Status);
            Assert.Equal("shipped", same.Result);
            Assert.Equal("helper-1", same.Assignee);
            Assert.Equal("closed", same.Details);
            Assert.Equal(0, same.InterruptRetries);
            AssertSiblingUntouched(board);

            var saved = SwarmBoard.Load(board.Id, root).TaskById("t-a");
            Assert.Equal(SwarmStatus.Done, saved.Status);
            Assert.Equal("shipped", saved.Result);
            Assert.Equal(0, saved.InterruptRetries);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Respawn_on_failed_leaves_it_failed()
    {
        var root = Path.Combine(Path.GetTempPath(), "xai-desk-swarm-respawn-failed-" + Guid.NewGuid().ToString("N"));
        try
        {
            var board = SwarmBoard.Create("Leave a failed task",
            [
                new SwarmTask { Id = "t-a", Title = "Failed", Role = SwarmRole.Investigator, Status = SwarmStatus.Failed, Assignee = "helper-1", Result = "stopped", Details = "closed", InterruptRetries = 1 },
                new SwarmTask { Id = "t-b", Title = "Leave this task", Role = SwarmRole.Researcher, Status = SwarmStatus.Todo, Assignee = "keep", Result = "untouched", Details = "leave me" }
            ], root);

            var same = board.Respawn("t-a", "should not apply");
            Assert.Equal(SwarmStatus.Failed, same.Status);
            Assert.Equal("stopped", same.Result);
            Assert.Equal("helper-1", same.Assignee);
            Assert.Equal("closed", same.Details);
            Assert.Equal(1, same.InterruptRetries);
            AssertSiblingUntouched(board);

            var saved = SwarmBoard.Load(board.Id, root).TaskById("t-a");
            Assert.Equal(SwarmStatus.Failed, saved.Status);
            Assert.Equal("stopped", saved.Result);
            Assert.Equal(1, saved.InterruptRetries);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void AssertSiblingUntouched(SwarmBoard board)
    {
        var sibling = board.TaskById("t-b");
        Assert.Equal(SwarmStatus.Todo, sibling.Status);
        Assert.Equal(0, sibling.InterruptRetries);
        Assert.Equal("keep", sibling.Assignee);
        Assert.Equal("untouched", sibling.Result);
        Assert.Equal("leave me", sibling.Details);
    }
}
