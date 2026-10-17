using Sovereign.Desktop.Connector;
using Xunit;

namespace XaiDesk.Tests;

public sealed class SwarmRunArchiveTests
{
    [Fact]
    public void Move_archives_the_live_board_and_clears_current()
    {
        var root = Temp();
        try
        {
            var conversationId = Guid.NewGuid().ToString("N");
            var board = SwarmSchedule.OpenRun(conversationId, "Write one marker line",
                [new SwarmTask { Id = "t-1", Title = "Write marker", Role = SwarmRole.Engineer }], root, DoctrineFixture.Case);
            var folder = Path.Combine(root, conversationId);
            var live = File.ReadAllBytes(board.BoardPath);
            var bakBytes = new byte[] { 4, 5, 6 };
            File.WriteAllBytes(board.BoardPath + ".bak", bakBytes);

            var result = SwarmRunArchive.Move(folder, board.Id);

            var archived = Path.Combine(folder, "runs", board.Id, "archived", "board.json");
            var current = Path.Combine(folder, "current.txt");
            Assert.True(result.Moved);
            Assert.Equal("moved", result.Reason);
            Assert.Equal(live, File.ReadAllBytes(archived));
            Assert.Equal(bakBytes, File.ReadAllBytes(archived + ".bak"));
            Assert.False(File.Exists(board.BoardPath));
            Assert.False(File.Exists(board.BoardPath + ".bak"));
            Assert.True(Directory.Exists(Path.GetDirectoryName(board.BoardPath)));
            Assert.True(File.Exists(current));
            Assert.Equal("", File.ReadAllText(current));
            Assert.Null(SwarmSection.TryLoad(conversationId, root));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void Move_refuses_when_the_archived_board_already_exists()
    {
        var root = Temp();
        try
        {
            var conversationId = Guid.NewGuid().ToString("N");
            var board = SwarmSchedule.OpenRun(conversationId, "Write one marker line",
                [new SwarmTask { Id = "t-1", Title = "Write marker", Role = SwarmRole.Engineer }], root, DoctrineFixture.Case);
            var folder = Path.Combine(root, conversationId);
            var archived = Path.Combine(folder, "runs", board.Id, "archived", "board.json");
            Directory.CreateDirectory(Path.GetDirectoryName(archived)!);
            var archivedBytes = new byte[] { 9, 9, 9 };
            File.WriteAllBytes(archived, archivedBytes);
            var live = File.ReadAllBytes(board.BoardPath);
            var currentPath = Path.Combine(folder, "current.txt");
            var current = File.ReadAllText(currentPath);

            var result = SwarmRunArchive.Move(folder, board.Id);

            Assert.False(result.Moved);
            Assert.Equal("destination exists", result.Reason);
            Assert.Equal(live, File.ReadAllBytes(board.BoardPath));
            Assert.Equal(archivedBytes, File.ReadAllBytes(archived));
            Assert.Equal(current, File.ReadAllText(currentPath));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void Move_reports_a_missing_board_and_leaves_current()
    {
        var root = Temp();
        try
        {
            var conversationId = Guid.NewGuid().ToString("N");
            var runId = Guid.NewGuid().ToString("N");
            var folder = Path.Combine(root, conversationId);
            Directory.CreateDirectory(folder);
            var currentPath = Path.Combine(folder, "current.txt");
            File.WriteAllText(currentPath, runId);
            var archived = Path.Combine(folder, "runs", runId, "archived", "board.json");

            var result = SwarmRunArchive.Move(folder, runId);

            Assert.False(result.Moved);
            Assert.Equal("missing board", result.Reason);
            Assert.False(File.Exists(archived));
            Assert.False(Directory.Exists(Path.GetDirectoryName(archived)!));
            Assert.Equal(runId, File.ReadAllText(currentPath));
        }
        finally { Delete(root); }
    }

    private static string Temp()
    {
        var path = Path.Combine(Path.GetTempPath(), "xai-desk-swarm-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Delete(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }
}
