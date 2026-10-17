using Sovereign.Desktop.Connector;
using Sovereign.Desktop.Core;

namespace XaiDesk.Tests;

static class DoctrineFixture
{
    public static void ReadyForWork(SwarmBoard board, string taskId)
    {
        var goal = board.Goal ?? "";
        Row(board, taskId, DoctrinePhase.Disagreement, "overruled", "The strongest case against this plan is that two helpers write the same file.");
        Row(board, taskId, DoctrinePhase.Assumption, "STATED", "The goal names the deliverable.");
        var files = board.TaskById(taskId).Files.Where(file => !string.IsNullOrWhiteSpace(file)).ToArray();
        if (files.Length == 0) files = ["(none)"];
        foreach (var file in files)
            Row(board, taskId, DoctrinePhase.Rollback, "REVERSIBLE", "Revert " + file + " from the board backup.", file);
        Row(board, taskId, DoctrinePhase.Decay, "check", goal, pinned: goal);
    }

    public static void RecordPass(SwarmBoard board, string taskId)
    {
        DoctrineGate.Record(board, new DoctrineLogRow
        {
            TaskId = taskId,
            Phase = DoctrinePhase.Verification,
            Verdict = "pass",
            Text = Report("notes.txt"),
            Read = "notes.txt"
        });
        DoctrineGate.Record(board, new DoctrineLogRow
        {
            TaskId = taskId,
            Phase = DoctrinePhase.Interface,
            Verdict = "report",
            Text = Delivery("notes.txt"),
            Read = "notes.txt"
        });
    }

    public static void MarkDone(SwarmBoard board, string taskId)
    {
        RecordPass(board, taskId);
        board.Submit(taskId, "result " + taskId, "proof " + taskId);
        board.Verify(taskId, true, "Claim: " + taskId + " is done\nHow tested: the doctrine log\nEvidence: the row\nPaths covered: success\nVerdict: pass because the gate let verify through");
    }

    public static string Report(string artifact) =>
        "Existence audit: claim: the deliverable is on disk artifact: " + artifact + "\n"
        + "Raw output: exit 0\n"
        + "Gap audit: skipped persistence\n"
        + "Deviations: persistence was not rerun\n"
        + "Unverified claims: none";

    public static string Delivery(string artifact) =>
        "Answer: the deliverable is " + artifact + "\n"
        + "Failures: none\n"
        + "Skips: persistence\n"
        + "Open guesses: none remain\n"
        + "State changes: REVERSIBLE " + artifact;

    static void Row(SwarmBoard board, string taskId, string phase, string verdict, string text, string paths = "", string pinned = "") =>
        DoctrineGate.Record(board, new DoctrineLogRow
        {
            TaskId = taskId,
            Phase = phase,
            Verdict = verdict,
            Text = text,
            Paths = paths,
            PinnedGoal = pinned,
            Read = pinned.Length > 0 ? pinned : text
        });
}
