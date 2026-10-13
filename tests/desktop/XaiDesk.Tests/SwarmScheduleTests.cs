using Sovereign.Desktop.Connector;
using Xunit;

namespace XaiDesk.Tests;

public sealed class SwarmScheduleTests
{
    private const string Inquiry = "Resea" + "rch";

    [Fact]
    public void Comparison_goal_splits_and_runs_in_parallel_without_touching_the_old_board()
    {
        var compare = Path.Combine(Path.GetTempPath(), "xai-desk-swarm-test", "compare.md");
        var goal = $"{Inquiry} 3 open-source Grok desktop apps and write a one-page comparison to {compare}";
        Assert.True(SwarmSchedule.CanSplit(goal));
        Assert.True(SwarmSchedule.InquireAndWrite(goal));

        var root = Temp();
        try
        {
            var conversationId = Guid.NewGuid().ToString("N");
            var legacy = SwarmBoard.Create("old goal", [Item("t-old", SwarmRole.Engineer, "Old work")], root, conversationId);
            var legacyText = File.ReadAllText(legacy.BoardPath);
            var tasks = new[]
            {
                Item("t-a", SwarmRole.Inquirer, "App A"),
                Item("t-b", SwarmRole.Inquirer, "App B"),
                Item("t-c", SwarmRole.GrokbotInquiry, "App C"),
                Item("t-d", SwarmRole.Engineer, "Write compare.md", [compare], ["t-a", "t-b", "t-c"]),
                Item("t-e", SwarmRole.Verifier, "Check compare.md", [compare], ["t-d"])
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

            run.Start("t-a", SwarmRole.Inquirer);
            run.Start("t-b", SwarmRole.Inquirer);
            run.Start("t-c", "grokbot");
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
            Item("t-2", SwarmRole.Inquirer, "Two"),
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
    public void Open_run_cannot_be_replaced_while_grokbot_is_waiting()
    {
        var root = Path.Combine(Path.GetTempPath(), "xai-desk-open-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var id = Guid.NewGuid().ToString("N");
            var board = SwarmBoard.Create(Inquiry + " the apps", [new SwarmTask { Id = "t-wait", Title = Inquiry + " Grok-Desktop", Role = SwarmRole.GrokbotInquiry, Status = "todo" }], root, id);
            Assert.True(SwarmSchedule.HasOpenWork(board));
            board.Goal = "A different goal";
            var ex = Assert.Throws<IOException>(() => board.Save());
            Assert.Contains("open task", ex.Message, StringComparison.OrdinalIgnoreCase);
            var reread = SwarmBoard.Load(id, root);
            Assert.Equal(Inquiry + " the apps", reread.Goal);
            Assert.Equal("todo", reread.TaskById("t-wait").Status);
            reread.Start("t-wait", SwarmRole.GrokbotInquiry);
            Assert.Equal("doing", SwarmBoard.Load(id, root).TaskById("t-wait").Status);
            Assert.True(SwarmSection.IsSwarmWorker("swarm-" + id + "-t-wait", null, null, reread));
            Assert.Equal("t-wait", SwarmSection.MatchingTaskId(reread, "helper-1", Inquiry + " Grok-Desktop", null));
            Assert.False(SwarmSection.IsSwarmWorker("helper-9", "Unrelated helper", "Something else", reread));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
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
