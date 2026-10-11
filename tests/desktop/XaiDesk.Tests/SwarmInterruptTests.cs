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
}
