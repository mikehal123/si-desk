using Sovereign.Desktop.Connector;
using Sovereign.Desktop.Core;

namespace XaiDesk.Tests;

static class DoctrineFixture
{
    public const string Case = "Two helpers can write the same file.";

    public static void ReadyForWork(SwarmBoard board, string taskId)
    {
        DoctrineGate.Overrule(board, taskId);
        DoctrineGate.AppendAssumption(board, taskId, "STATED", "The goal names the deliverable.");
        var files = board.TaskById(taskId).Files.Where(file => !string.IsNullOrWhiteSpace(file)).ToArray();
        if (files.Length == 0) files = ["(none)"];
        foreach (var file in files)
            DoctrineGate.AppendRollback(board, taskId, "REVERSIBLE", "Revert " + file + " from the board backup.", file);
        DoctrineGate.AppendDecay(board, taskId);
    }

    public static string ArtifactFile(SwarmBoard board, string taskId)
    {
        var path = Path.Combine(Path.GetDirectoryName(board.BoardPath)!, "artifact-" + taskId + ".txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) File.WriteAllText(path, "artifact " + taskId);
        return path;
    }

    public static void RecordPass(SwarmBoard board, string taskId)
    {
        var artifact = ArtifactFile(board, taskId);
        DoctrineGate.Record(board, new DoctrineLogRow
        {
            TaskId = taskId,
            Phase = DoctrinePhase.Verification,
            Verdict = "pass",
            Text = Report(artifact),
            Read = artifact
        });
        DoctrineGate.Record(board, new DoctrineLogRow
        {
            TaskId = taskId,
            Phase = DoctrinePhase.Interface,
            Verdict = "report",
            Text = Delivery(artifact),
            Read = artifact
        });
    }

    public static void MarkDone(SwarmBoard board, string taskId, string? proof = null)
    {
        RecordPass(board, taskId);
        board.Submit(taskId, "result " + taskId, proof ?? "proof " + taskId);
        board.Verify(taskId, true, "Claim: " + taskId + " is done\nHow tested: the doctrine log\nEvidence: the row\nPaths covered: success\nVerdict: pass because the gate let verify through");
    }

    public static void FinishFromTodo(SwarmBoard board, string taskId, string assignee = "worker", string? proof = null)
    {
        ReadyForWork(board, taskId);
        board.Start(taskId, assignee);
        MarkDone(board, taskId, proof);
    }

    public static void FailFromTodo(SwarmBoard board, string taskId, string assignee = "worker")
    {
        ReadyForWork(board, taskId);
        for (var n = 0; n < 3; n++)
        {
            board.Start(taskId, assignee);
            board.Submit(taskId, "finding", "source: local note");
            board.Verify(taskId, false, "still missing");
        }
    }

    public static string Report(string artifact) =>
        "Existence audit: claim: the deliverable is on disk artifact: " + artifact + "\n"
        + "Raw output: exit 0\n"
        + "Gap audit: looked at the file\n"
        + "Deviations: persistence was not rerun\n"
        + "Unverified claims: none beyond the named skip in deviations";

    public static string Delivery(string artifact) =>
        "Answer: the deliverable is " + artifact + "\n"
        + "Failures: none\n"
        + "Skips: persistence\n"
        + "Open guesses: none remain\n"
        + "State changes: REVERSIBLE " + artifact;
}
