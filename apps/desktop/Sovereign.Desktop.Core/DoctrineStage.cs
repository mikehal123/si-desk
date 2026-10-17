using System.Text.RegularExpressions;

namespace Sovereign.Desktop.Core;

public static class DoctrinePhase
{
    public const string Disagreement = "disagreement";
    public const string Assumption = "assumption";
    public const string Rollback = "rollback";
    public const string Work = "work";
    public const string Decay = "decay";
    public const string Verification = "verification";
    public const string Interface = "interface";
    public const string Done = "done";

    public static readonly string[] Order =
    [
        Disagreement,
        Assumption,
        Rollback,
        Work,
        Decay,
        Verification,
        Interface,
        Done
    ];

    public static int IndexOf(string? phase)
    {
        var text = (phase ?? "").Trim();
        for (var i = 0; i < Order.Length; i++)
            if (string.Equals(Order[i], text, StringComparison.Ordinal)) return i;
        return -1;
    }
}

public static class DoctrineAnswer
{
    public static readonly string[] Disagreement = ["holds", "overruled"];
    public static readonly string[] Assumption = ["STATED", "INFERRED", "GUESSED", "OVERTURNED"];
    public static readonly string[] Rollback = ["REVERSIBLE", "RECOVERABLE", "IRREVERSIBLE"];
    public static readonly string[] Verification = ["pass", "fail"];
    public static readonly string[] Decay = ["check", "authorize"];
    public static readonly string[] Interface = ["report"];
    public static readonly string[] Done = ["done"];
    public static readonly string[] Work = ["claim"];

    public static readonly string[] VerificationSections =
    [
        "Existence audit",
        "Raw output",
        "Gap audit",
        "Deviations",
        "Unverified claims"
    ];

    public static readonly string[] InterfaceSections =
    [
        "Answer",
        "Failures",
        "Skips",
        "Open guesses",
        "State changes"
    ];
}

/// <summary>One doctrine attempt or one accepted log row. Human-only fields are set by the gate, not by a model role.</summary>
public sealed class DoctrineRow
{
    public string TaskId { get; init; } = "";
    public string Phase { get; init; } = "";
    public string Answer { get; init; } = "";
    public string Text { get; init; } = "";
    public string Paths { get; init; } = "";
    public string Actor { get; init; } = "";
    public string Read { get; init; } = "";
    public string Goal { get; init; } = "";
    public string Assignee { get; init; } = "";
    public bool Human { get; init; }
    public bool Riskiest { get; init; }
    public bool Tested { get; init; }
    public string Survives { get; init; } = "";
    public bool HumanApproved { get; init; }
    public bool UiFeature { get; init; }
    public bool HumanPath { get; init; }
    public string PinnedGoal { get; init; } = "";
}

public sealed class DoctrineContext
{
    public string BoardGoal { get; init; } = "";
    public IReadOnlyList<string> Files { get; init; } = [];
    public string Status { get; init; } = "";
    public string Assignee { get; init; } = "";
}

public readonly record struct DoctrineDecision(bool Allowed, string Reason);

/// <summary>
/// Closed-answer checks. SwarmBoard.Start, Submit, and Verify are the only doors that move a task.
/// </summary>
public static class DoctrineStage
{
    public static DoctrineDecision Evaluate(DoctrineRow candidate, DoctrineContext context, IReadOnlyList<DoctrineRow> accepted)
    {
        var phase = (candidate.Phase ?? "").Trim();
        var index = DoctrinePhase.IndexOf(phase);
        if (index < 0)
            return Block("Phase is not in the doctrine order.");
        if (!LegalAnswer(phase, candidate))
            return Block("Answer is not in the closed list for " + phase + ".");

        for (var i = 0; i < index; i++)
        {
            if (!Satisfied(DoctrinePhase.Order[i], accepted, context))
                return Block(DoctrinePhase.Order[i] + " has no passing row.");
        }

        var gate = PhaseGate(candidate, context, accepted);
        return gate ?? new DoctrineDecision(true, "advanced");
    }

    public static bool Satisfied(string phase, IReadOnlyList<DoctrineRow> accepted, DoctrineContext context)
    {
        var rows = accepted ?? [];
        switch (phase)
        {
            case DoctrinePhase.Disagreement:
                return rows.Any(row => row.Phase == DoctrinePhase.Disagreement && row.Human && row.Answer == "overruled" && row.Text.Trim().Length > 0);
            case DoctrinePhase.Assumption:
                return AssumptionClear(rows);
            case DoctrinePhase.Rollback:
                return RollbackClear(rows, context.Files);
            case DoctrinePhase.Work:
                return rows.Any(row => row.Phase == DoctrinePhase.Work && row.Assignee.Trim().Length > 0);
            case DoctrinePhase.Decay:
                return rows.Any(row => row.Phase == DoctrinePhase.Decay && string.Equals(row.Goal, Pin(rows, context.BoardGoal), StringComparison.Ordinal));
            case DoctrinePhase.Verification:
                return rows.Any(row => row.Phase == DoctrinePhase.Verification && row.Answer == "pass");
            case DoctrinePhase.Interface:
                return rows.Any(row => row.Phase == DoctrinePhase.Interface && row.Answer == "report" && row.Text.Trim().Length > 0);
            case DoctrinePhase.Done:
                return rows.Any(row => row.Phase == DoctrinePhase.Done && row.Answer == "done");
            default:
                return false;
        }
    }

    public static string Position(IReadOnlyList<DoctrineRow> accepted, DoctrineContext context)
    {
        var reached = "";
        foreach (var phase in DoctrinePhase.Order)
        {
            if (!Satisfied(phase, accepted, context)) break;
            reached = phase;
        }
        if (reached.Length == 0 && (accepted ?? []).Any(row => row.Phase == DoctrinePhase.Disagreement))
            return DoctrinePhase.Disagreement;
        return reached;
    }

    public static string Pin(IReadOnlyList<DoctrineRow> accepted, string boardGoal)
    {
        string? pin = null;
        foreach (var row in accepted ?? [])
        {
            if (pin is null && !string.IsNullOrWhiteSpace(row.PinnedGoal)) pin = row.PinnedGoal;
            if (row.Human && row.Phase == DoctrinePhase.Decay && row.Text.Trim().Length > 0)
                pin = row.Text.Trim();
        }
        return pin ?? (boardGoal ?? "");
    }

    public static string ClaimOwner(IReadOnlyList<DoctrineRow> accepted)
    {
        string owner = "";
        foreach (var row in accepted ?? [])
            if (row.Phase == DoctrinePhase.Work && row.Assignee.Trim().Length > 0)
                owner = row.Assignee.Trim();
        return owner;
    }

    public static bool UndoCovers(IReadOnlyList<DoctrineRow> accepted, string path)
    {
        var rows = accepted ?? [];
        var wanted = Norm(path);
        if (wanted.Length == 0) return false;
        foreach (var row in rows)
        {
            if (row.Phase != DoctrinePhase.Rollback) continue;
            if (!PathsMatch(row.Paths, wanted)) continue;
            if (row.Text.Trim().Length == 0) continue;
            if (row.Answer == "IRREVERSIBLE" && !Approved(rows, wanted)) continue;
            return true;
        }
        return false;
    }

    static DoctrineDecision? PhaseGate(DoctrineRow candidate, DoctrineContext context, IReadOnlyList<DoctrineRow> accepted)
    {
        var pin = Pin(accepted, context.BoardGoal);
        var acting = string.IsNullOrWhiteSpace(candidate.Goal) ? context.BoardGoal ?? "" : candidate.Goal;
        switch (candidate.Phase)
        {
            case DoctrinePhase.Disagreement:
                if (candidate.Answer == "overruled" && !candidate.Human)
                    return Block("Overruling a disagreement that holds is a human decision.");
                if (candidate.Text.Trim().Length == 0)
                    return Block("Disagreement needs the strongest case against the plan.");
                return null;
            case DoctrinePhase.Assumption:
                if (candidate.Text.Trim().Length == 0)
                    return Block("Assumption ledger row is empty.");
                if (candidate.Survives.Trim().Length > 0 && !candidate.Human)
                    return Block("Deciding what survives an OVERTURNED assumption is a human decision.");
                return null;
            case DoctrinePhase.Rollback:
                if (candidate.Text.Trim().Length == 0)
                    return Block("Rollback needs an undo. No undo, no write.");
                if (candidate.Paths.Trim().Length == 0)
                    return Block("Rollback needs the path it covers.");
                if (candidate.Answer == "IRREVERSIBLE" && candidate.HumanApproved && !candidate.Human)
                    return Block("Approving an IRREVERSIBLE rollback row is a human decision.");
                return null;
            case DoctrinePhase.Work:
                if (string.Equals(context.Status, "doing", StringComparison.Ordinal))
                {
                    var owner = context.Assignee.Trim().Length > 0 ? context.Assignee.Trim() : ClaimOwner(accepted);
                    if (owner.Length == 0) owner = "an unnamed helper";
                    return Block("Task " + candidate.TaskId + " is already claimed by " + owner + ".");
                }
                if (candidate.Assignee.Trim().Length == 0)
                    return Block("A claim needs the helper's name.");
                return null;
            case DoctrinePhase.Decay:
                if (candidate.Answer == "authorize")
                {
                    if (!candidate.Human)
                        return Block("Authorizing a pinned-goal change is a human decision.");
                    if (candidate.Text.Trim().Length == 0)
                        return Block("The human authorization needs the new goal text.");
                    return null;
                }
                if (!string.Equals(acting, pin, StringComparison.Ordinal))
                    return Block("Decay blocked. The goal changed without human authorization.");
                return null;
            case DoctrinePhase.Verification:
                if (!GoalHeld(candidate, context, pin))
                    return Block("Decay blocked. The goal changed without human authorization.");
                return Verification(candidate);
            case DoctrinePhase.Interface:
                if (!GoalHeld(candidate, context, pin))
                    return Block("Decay blocked. The goal changed without human authorization.");
                return Interface(candidate);
            case DoctrinePhase.Done:
                if (!GoalHeld(candidate, context, pin))
                    return Block("Decay blocked. The goal changed without human authorization.");
                return null;
            default:
                return Block("Phase is not in the doctrine order.");
        }
    }

    static bool GoalHeld(DoctrineRow candidate, DoctrineContext context, string pin)
    {
        var acting = string.IsNullOrWhiteSpace(candidate.Goal) ? context.BoardGoal ?? "" : candidate.Goal;
        return string.Equals(acting, pin, StringComparison.Ordinal);
    }

    static DoctrineDecision? Verification(DoctrineRow candidate)
    {
        if (!TrySections(candidate.Text, DoctrineAnswer.VerificationSections, out var sections, out var error))
            return Block("Verification report refused. " + error);
        if (candidate.Answer != "pass") return null;
        var existence = sections["Existence audit"];
        if (Regex.IsMatch(existence, @"\bMISSING\b"))
            return Block("Verification refused. A deliverable is MISSING.");
        if (!Regex.IsMatch(existence, @"artifact:\s*\S+", RegexOptions.IgnoreCase))
            return Block("Verification refused. A claim has no artifact pointer.");
        foreach (var line in existence.Split('\n'))
        {
            var trim = line.Trim();
            if (trim.StartsWith("claim:", StringComparison.OrdinalIgnoreCase)
                && trim.IndexOf("artifact:", StringComparison.OrdinalIgnoreCase) < 0)
                return Block("Verification refused. A claim has no artifact pointer.");
        }
        var gap = sections["Gap audit"];
        var skipped = gap.Contains("skipped", StringComparison.OrdinalIgnoreCase)
            || gap.Contains("not tested", StringComparison.OrdinalIgnoreCase);
        if (skipped && sections["Deviations"].Trim().Length == 0)
            return Block("Verification refused. Deviations is empty while something was skipped.");
        if (candidate.UiFeature
            && existence.Contains("unit test", StringComparison.OrdinalIgnoreCase)
            && !candidate.HumanPath)
            return Block("Verification refused. A unit test with no human path does not count as the existence check for a UI feature.");
        return null;
    }

    static DoctrineDecision? Interface(DoctrineRow candidate)
    {
        var text = candidate.Text ?? "";
        var first = "";
        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (line.Trim().Length == 0) continue;
            first = line.Trim();
            break;
        }
        if (!first.StartsWith("Answer:", StringComparison.OrdinalIgnoreCase))
            return Block("Interface refused. The first block is the answer.");
        if (!TrySections(text, DoctrineAnswer.InterfaceSections, out var sections, out var error))
            return Block("Interface refused. " + error);
        foreach (var heading in DoctrineAnswer.InterfaceSections)
        {
            if (sections[heading].Trim().Length == 0)
                return Block("Interface refused. The report drops " + heading + ".");
        }
        return null;
    }

    static bool LegalAnswer(string phase, DoctrineRow candidate)
    {
        var answer = candidate.Answer ?? "";
        var list = phase switch
        {
            DoctrinePhase.Disagreement => DoctrineAnswer.Disagreement,
            DoctrinePhase.Assumption => DoctrineAnswer.Assumption,
            DoctrinePhase.Rollback => DoctrineAnswer.Rollback,
            DoctrinePhase.Work => DoctrineAnswer.Work,
            DoctrinePhase.Decay => DoctrineAnswer.Decay,
            DoctrinePhase.Verification => DoctrineAnswer.Verification,
            DoctrinePhase.Interface => DoctrineAnswer.Interface,
            DoctrinePhase.Done => DoctrineAnswer.Done,
            _ => []
        };
        foreach (var item in list)
            if (string.Equals(item, answer, StringComparison.Ordinal)) return true;
        return false;
    }

    static bool AssumptionClear(IReadOnlyList<DoctrineRow> rows)
    {
        var ledger = rows.Where(row => row.Phase == DoctrinePhase.Assumption && row.Text.Trim().Length > 0).ToArray();
        if (ledger.Length == 0) return false;
        var riskiest = false;
        var overturned = false;
        var survives = false;
        foreach (var row in ledger)
        {
            if (row.Riskiest && (row.Tested || row.Answer == "OVERTURNED")) riskiest = true;
            if (row.Answer == "OVERTURNED") overturned = true;
            if (row.Human && row.Survives.Trim().Length > 0) survives = true;
        }
        if (!riskiest) return false;
        return !overturned || survives;
    }

    static bool RollbackClear(IReadOnlyList<DoctrineRow> rows, IReadOnlyList<string> files)
    {
        var covered = rows.Where(row => row.Phase == DoctrinePhase.Rollback && row.Text.Trim().Length > 0 && row.Paths.Trim().Length > 0).ToArray();
        if (covered.Length == 0) return false;
        foreach (var row in covered)
        {
            if (row.Answer != "IRREVERSIBLE") continue;
            if (!Approved(rows, Norm(row.Paths))) return false;
        }
        var needed = (files ?? []).Where(file => !string.IsNullOrWhiteSpace(file)).Select(Norm).ToArray();
        if (needed.Length == 0) return true;
        foreach (var path in needed)
        {
            var hit = false;
            foreach (var row in covered)
            {
                if (!PathsMatch(row.Paths, path)) continue;
                if (row.Answer == "IRREVERSIBLE" && !Approved(rows, path)) continue;
                hit = true;
                break;
            }
            if (!hit) return false;
        }
        return true;
    }

    static bool Approved(IReadOnlyList<DoctrineRow> rows, string path) =>
        rows.Any(row => row.Phase == DoctrinePhase.Rollback
            && row.Human
            && row.HumanApproved
            && row.Answer == "IRREVERSIBLE"
            && PathsMatch(row.Paths, path));

    public static bool TrySections(string? text, string[] headings, out Dictionary<string, string> sections, out string error)
    {
        sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var at = new int[headings.Length];
        for (var h = 0; h < headings.Length; h++)
        {
            var found = -1;
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (!line.StartsWith(headings[h] + ":", StringComparison.OrdinalIgnoreCase)) continue;
                found = i;
                break;
            }
            if (found < 0)
            {
                error = "missing " + headings[h];
                return false;
            }
            if (h > 0 && found < at[h - 1])
            {
                error = headings[h] + " is out of order";
                return false;
            }
            at[h] = found;
        }
        for (var h = 0; h < headings.Length; h++)
        {
            var start = at[h];
            var end = h + 1 < headings.Length ? at[h + 1] : lines.Length;
            var colon = lines[start].IndexOf(':');
            var body = colon >= 0 && colon + 1 < lines[start].Length ? lines[start][(colon + 1)..].Trim() : "";
            for (var i = start + 1; i < end; i++)
            {
                if (body.Length > 0) body += "\n";
                body += lines[i].Trim();
            }
            sections[headings[h]] = body.Trim();
        }
        error = "";
        return true;
    }

    static DoctrineDecision Block(string reason) => new(false, reason);

    static string Norm(string? path) => (path ?? "").Trim().Replace('/', '\\');

    static bool PathsMatch(string? left, string? right) =>
        string.Equals(Norm(left), Norm(right), StringComparison.OrdinalIgnoreCase);
}
