using Sovereign.Desktop.Connector;
using Xunit;

namespace XaiDesk.Tests;

public sealed class SwarmScheduleTests
{
    [Fact]
    public void Comparison_goal_splits_and_runs_in_parallel_without_touching_the_old_board()
    {
        var goal = "Re" + "search 3 open-source Grok desktop apps and write a one-page comparison to F:\\xai-desk\\swarm-test\\compare.md.";
        Assert.True(SwarmSchedule.CanSplit(goal));
        Assert.True(SwarmSchedule.LookupAndWrite(goal));

        var root = Temp();
        try
        {
            var conversationId = Guid.NewGuid().ToString("N");
            var legacy = SwarmBoard.Create("old goal", [Item("t-old", SwarmRole.Engineer, "Old work")], root, conversationId);
            var legacyText = File.ReadAllText(legacy.BoardPath);
            var tasks = new[]
            {
                Item("t-a", SwarmRole.Researcher, "App A"),
                Item("t-b", SwarmRole.Researcher, "App B"),
                Item("t-c", SwarmRole.Scout, "App C"),
                Item("t-d", SwarmRole.Engineer, "Write compare.md", ["F:/xai-desk/swarm-test/compare.md"], ["t-a", "t-b", "t-c"]),
                Item("t-e", SwarmRole.Verifier, "Check compare.md", ["F:\\xai-desk\\swarm-test\\compare.md"], ["t-d"])
            };

            var run = SwarmSchedule.OpenRun(conversationId, goal, tasks, root);

            Assert.Equal(legacyText, File.ReadAllText(legacy.BoardPath));
            Assert.Equal(run.Id, File.ReadAllText(Path.Combine(root, conversationId, "current.txt")));
            Assert.Contains(Path.Combine("runs", run.Id, "board.json"), run.BoardPath);
            Assert.True(File.Exists(run.BoardPath));
            Assert.True(File.Exists(legacy.BoardPath));
            Assert.Throws<IOException>(() => SwarmBoard.Create(goal, [Item("t-again", SwarmRole.Engineer, "Again")], run.Root, run.Id));

            var ready = SwarmSchedule.Ready(run);
            Assert.Equal<string>(["t-a", "t-b", "t-c"], ready);
            Assert.False(SwarmSchedule.CanStart(run, "t-d"));

            run.Start("t-a", "researcher");
            run.Start("t-b", "researcher");
            run.Start("t-c", "scout");
            Assert.Throws<InvalidOperationException>(() => run.Start("t-d", "engineer"));
            Assert.Empty(SwarmSchedule.Ready(run));

            Finish(run, "t-a");
            Finish(run, "t-b");
            Finish(run, "t-c");
            Assert.Equal<string>(["t-d"], SwarmSchedule.Ready(run));

            run.Start("t-d", "engineer");
            Assert.False(SwarmSchedule.CanStart(run, "t-e"));
            Finish(run, "t-d");
            Assert.Equal<string>(["t-e"], SwarmSchedule.Ready(run));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void One_step_goal_stays_one_task()
    {
        const string goal = "Write one marker line";
        Assert.False(SwarmSchedule.CanSplit(goal));
        Assert.Throws<ArgumentException>(() => SwarmSchedule.CheckPlan(goal,
        [
            Item("t-1", SwarmRole.Engineer, "One"),
            Item("t-2", SwarmRole.Researcher, "Two"),
            Item("t-3", SwarmRole.Verifier, "Three")
        ]));
        SwarmSchedule.CheckPlan(goal, [Item("t-1", SwarmRole.Engineer, "Write marker")]);
    }

    [Fact]
    public void Same_file_blocks_a_second_worker()
    {
        var root = Temp();
        try
        {
            var board = SwarmBoard.Create("Edit the notes",
            [
                Item("t-1", SwarmRole.Engineer, "First notes", ["notes.txt"]),
                Item("t-2", SwarmRole.Engineer, "Second notes", ["notes.txt"])
            ], root);
            board.Start("t-1", "engineer");
            Assert.False(SwarmSchedule.CanStart(board, "t-2"));
            Assert.Throws<InvalidOperationException>(() => board.Start("t-2", "engineer"));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void Verifier_starts_when_its_engineer_is_in_review_unless_another_task_holds_the_file()
    {
        var root = Temp();
        try
        {
            const string file = "notes.txt";
            var board = SwarmBoard.Create("Edit the notes",
            [
                Item("eng", SwarmRole.Engineer, "Write notes", [file]),
                Item("eng-2", SwarmRole.Engineer, "Rewrite notes", [file]),
                Item("ver", SwarmRole.Verifier, "Check notes", [file], ["eng"])
            ], root);

            board.Start("eng", "engineer");
            Assert.False(SwarmSchedule.CanStart(board, "eng-2"));
            Assert.Throws<InvalidOperationException>(() => board.Start("eng-2", "engineer"));
            Assert.False(SwarmSchedule.CanStart(board, "ver"));

            board.Submit("eng", "wrote notes", "diff and test output");
            Assert.Equal(SwarmStatus.Review, board.TaskById("eng").Status);
            Assert.True(SwarmSchedule.CanStart(board, "ver"));
            Assert.Contains("ver", SwarmSchedule.Ready(board));

            board.TaskById("eng-2").Status = SwarmStatus.Doing;
            Assert.False(SwarmSchedule.CanStart(board, "ver"));
            Assert.DoesNotContain("ver", SwarmSchedule.Ready(board));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void Fifth_worker_waits()
    {
        var root = Temp();
        try
        {
            var tasks = Enumerable.Range(1, 5).Select(n => Item("t-" + n, SwarmRole.Engineer, "Task " + n)).ToList();
            var board = SwarmBoard.Create("Queue the extra hands", tasks, root);
            for (var n = 1; n <= 4; n++) board.Start("t-" + n, "engineer");
            Assert.False(SwarmSchedule.CanStart(board, "t-5"));
            Assert.Empty(SwarmSchedule.Ready(board));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void Open_run_cannot_be_replaced_while_a_task_is_open()
    {
        var root = Path.Combine(Path.GetTempPath(), "xai-desk-open-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var id = Guid.NewGuid().ToString("N");
            var board = SwarmBoard.Create("Read the apps", [new SwarmTask { Id = "t-wait", Title = "Read Grok-Desktop", Role = SwarmRole.Researcher, Status = "todo" }], root, id);
            Assert.True(SwarmSchedule.HasOpenWork(board));
            board.Goal = "A different goal";
            var ex = Assert.Throws<IOException>(() => board.Save());
            Assert.Contains("open task", ex.Message, StringComparison.OrdinalIgnoreCase);
            var reread = SwarmBoard.Load(id, root);
            Assert.Equal("Read the apps", reread.Goal);
            Assert.Equal("todo", reread.TaskById("t-wait").Status);
            reread.Start("t-wait", "researcher");
            Assert.Equal("doing", SwarmBoard.Load(id, root).TaskById("t-wait").Status);
            Assert.True(SwarmSection.IsSwarmWorker("swarm-" + id + "-t-wait", null, null, reread));
            Assert.Equal("t-wait", SwarmSection.MatchingTaskId(reread, "helper-1", "Read Grok-Desktop", null));
            Assert.False(SwarmSection.IsSwarmWorker("helper-9", "Unrelated helper", "Something else", reread));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Planned_board_never_keeps_a_grokbot_role()
    {
        var goal = "Re" + "search 3 apps and write compare.md";
        Assert.True(SwarmSchedule.LookupAndWrite(goal));
        var legacyLookup = "grokbot-" + "re" + "search";
        var planned = Assert.Throws<ArgumentException>(() => SwarmSchedule.CheckPlan(goal,
        [
            Item("t-a", SwarmRole.Researcher, "App A"),
            Item("t-b", legacyLookup, "App B"),
            Item("t-c", SwarmRole.Engineer, "Write compare.md", ["compare.md"], ["t-a", "t-b"]),
            Item("t-d", SwarmRole.Verifier, "Check compare.md", ["compare.md"], ["t-c"])
        ]));
        Assert.Contains("not a swarm worker", planned.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => SwarmSchedule.CheckPlan("Write one marker line", [Item("t-1", "Grokbot-Ops", "One")]));
        Assert.Throws<ArgumentException>(() => SwarmSchedule.CheckPlan("Write one marker line", [Item("t-1", " grok-bot ", "One")]));
        Assert.Throws<ArgumentException>(() => SwarmSchedule.CheckPlan("Write one marker line", [Item("t-1", "GROKBOT", "One")]));
        // Scout took the slot grokbot used to hold: a lookup-and-write plan needs engineer, researcher, scout, and verifier.
        var noScout = Assert.Throws<ArgumentException>(() => SwarmSchedule.CheckPlan(goal,
        [
            Item("t-a", SwarmRole.Researcher, "App A"),
            Item("t-b", SwarmRole.Researcher, "App B"),
            Item("t-c", SwarmRole.Engineer, "Write compare.md", ["compare.md"], ["t-a", "t-b"]),
            Item("t-d", SwarmRole.Verifier, "Check compare.md", ["compare.md"], ["t-c"])
        ]));
        Assert.Contains("scout", noScout.Message, StringComparison.Ordinal);
        SwarmSchedule.CheckPlan(goal,
        [
            Item("t-a", SwarmRole.Researcher, "App A"),
            Item("t-b", SwarmRole.Scout, "Cross-check App A"),
            Item("t-c", SwarmRole.Engineer, "Write compare.md", ["compare.md"], ["t-a", "t-b"]),
            Item("t-d", SwarmRole.Verifier, "Check compare.md", ["compare.md"], ["t-c"])
        ]);

        Assert.True(SwarmRole.IsLegacyGrokbot(legacyLookup));
        Assert.True(SwarmRole.IsLegacyGrokbot("Grokbot-Ops"));
        Assert.True(SwarmRole.IsLegacyGrokbot(" grok_bot "));
        Assert.False(SwarmRole.IsLegacyGrokbot(SwarmRole.Researcher));
        Assert.False(SwarmRole.IsLegacyGrokbot(SwarmRole.Scout));
        Assert.False(SwarmRole.IsLegacyGrokbot(null));
        Assert.Equal(SwarmRole.Scout, SwarmRole.Normalize(legacyLookup));
        Assert.Equal(SwarmRole.Scout, SwarmRole.Normalize("GrokBot-Ops"));
        Assert.Equal(SwarmRole.Engineer, SwarmRole.Normalize(" engineer "));

        var root = Temp();
        try
        {
            // A board the manager wrote by hand with legacy grokbot roles.
            var id = Guid.NewGuid().ToString("N");
            var path = Path.Combine(root, id, "board.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{\"id\":\"" + id + "\",\"goal\":\"g\",\"tasks\":[" +
                "{\"id\":\"t-1\",\"title\":\"Look\",\"role\":\"" + legacyLookup + "\",\"status\":\"todo\"}," +
                "{\"id\":\"t-2\",\"title\":\"Ops\",\"role\":\"Grokbot-Ops\",\"status\":\"doing\"}," +
                "{\"id\":\"t-3\",\"title\":\"Write\",\"role\":\"engineer\",\"status\":\"todo\",\"after\":[\"t-1\",\"t-2\"]}]}");
            var board = SwarmBoard.Read(path);
            Assert.All(board.Tasks, task => Assert.DoesNotContain("grokbot", task.Role, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(SwarmRole.Scout, board.TaskById("t-1").Role);
            Assert.Equal(SwarmRole.Scout, board.TaskById("t-2").Role);
            Assert.Equal(SwarmRole.Engineer, board.TaskById("t-3").Role);
            Assert.Equal<string>(["t-1", "t-2"], board.TaskById("t-3").After);
            Assert.Equal("doing", board.TaskById("t-2").Status);
            Assert.False(SwarmSchedule.CanStart(board, "t-3"));
            // The raw file is left alone by Read; the remap is in memory until the next Save.
            Assert.Contains(legacyLookup, File.ReadAllText(path), StringComparison.Ordinal);

            // Create goes through Copy and remaps too.
            var created = SwarmBoard.Create("g2", [Item("t-x", "grokbot-ops", "Ops"), Item("t-y", SwarmRole.Verifier, "Check", null, ["t-x"])], root);
            Assert.Equal(SwarmRole.Scout, created.TaskById("t-x").Role);
            Assert.Equal(SwarmRole.Scout, SwarmBoard.Read(created.BoardPath).TaskById("t-x").Role);
            Assert.DoesNotContain("grokbot", File.ReadAllText(created.BoardPath), StringComparison.OrdinalIgnoreCase);
        }
        finally { Delete(root); }
    }

    private static void Finish(SwarmBoard board, string taskId)
    {
        board.Submit(taskId, "result " + taskId, "proof " + taskId);
        board.Verify(taskId, true, "checked " + taskId);
    }

    private static SwarmTask Item(string id, string role, string title, IEnumerable<string>? files = null, IEnumerable<string>? after = null) => new()
    {
        Id = id,
        Title = title,
        Role = role,
        Files = files?.ToList() ?? [],
        After = after?.ToList() ?? []
    };

    private static string Temp()
    {
        var path = Path.Combine(Path.GetTempPath(), "xai-desk-schedule-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Delete(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }
}
