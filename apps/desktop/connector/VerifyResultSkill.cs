using Sovereign.Desktop.Core;

namespace Sovereign.Desktop.Connector;

public sealed record VerifyReview(bool Pass, string Note);

public static class VerifyResultSkill
{
    public static string? Warning { get; private set; }

    public static string Text()
    {
        var loaded = Load(null, true);
        Warning = loaded.Warning;
        return loaded.Text;
    }

    public static (string Text, string? Warning) Load(string? start, bool searchParents)
    {
        var path = searchParents ? FindFile(start) : Direct(start);
        if (path is null)
            return (BuiltIn(), "verify-result skill was not found under .cursor/skills. Using the built-in copy.");
        try
        {
            var text = File.ReadAllText(path).Replace("\r\n", "\n").Trim();
            if (!text.Contains("name: verify-result", StringComparison.Ordinal))
                return (BuiltIn(), "verify-result skill file is not the expected skill. Using the built-in copy.");
            return (text, WorkDoctrine.IndexWarning(path, "verify-result", "verifier", "manager"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (BuiltIn(), "verify-result skill could not be read. Using the built-in copy.");
        }
    }

    public static string? FindFile(string? start = null)
    {
        var dir = new DirectoryInfo(start ?? AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, ".cursor", "skills", "verify-result", "SKILL.md");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static string? Direct(string? start)
    {
        if (string.IsNullOrWhiteSpace(start)) return null;
        var candidate = Path.Combine(start, ".cursor", "skills", "verify-result", "SKILL.md");
        return File.Exists(candidate) ? candidate : null;
    }

    private static string BuiltIn()
    {
        var assembly = typeof(VerifyResultSkill).Assembly;
        using var stream = assembly.GetManifestResourceStream("Sovereign.Desktop.Connector.verify-result.md");
        if (stream is null) return Minimal;
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd().Replace("\r\n", "\n").Trim();
        return text.Contains("name: verify-result", StringComparison.Ordinal) ? text : Minimal;
    }

    private const string Minimal = """
        ---
        name: verify-result
        description: "Prove a result before calling it done."
        ---
        # Verify Result
        The skill file was not found. Missing or weak proof is a fail. Fill in Claim, How tested, Evidence, Paths covered, and Verdict.
        """;

    public static string Format(string claim, string how, string evidence, string paths, string verdict) =>
        "Claim: " + claim.Trim() + "\n"
        + "How tested: " + how.Trim() + "\n"
        + "Evidence: " + evidence.Trim() + "\n"
        + "Paths covered: " + paths.Trim() + "\n"
        + "Verdict: " + verdict.Trim();

    public static VerifyReview Review(string? proof)
    {
        var raw = (proof ?? "").Replace("\r\n", "\n").Trim();
        if (raw.Length == 0)
            return new(false, Format(
                "(missing)",
                "Reviewed the submitted proof against verify-result. Nothing was rerun.",
                "(missing)",
                "None. Every path was skipped because no proof was submitted.",
                "fail because the proof is missing"));

        var sections = Parse(raw);
        string Body(string key) => sections.TryGetValue(key, out var value) ? value.Trim() : "";
        var missing = Headings.Where(heading => Body(heading).Length == 0).ToArray();
        var evidence = Body("Evidence");
        var weakEvidence = evidence.Length > 0 && IsWeak(evidence);
        var weakWhole = IsWeak(raw);
        if (missing.Length > 0 || weakEvidence || weakWhole)
        {
            var why = missing.Length > 0
                ? "the proof is missing " + string.Join(", ", missing)
                : "Evidence is not proof a skeptical reviewer would accept";
            if (weakEvidence || weakWhole) why += ". \"It builds\" or \"it opens\" is not proof";
            return new(false, Format(
                Body("Claim").Length == 0 ? "(missing)" : Body("Claim"),
                Body("How tested").Length == 0 ? "Reviewed the submitted proof against verify-result." : Body("How tested"),
                evidence.Length == 0 ? (weakWhole ? raw : "(missing)") : evidence,
                Body("Paths covered").Length == 0 ? "None. Skipped because the proof did not cover a real path." : Body("Paths covered"),
                "fail because " + why));
        }

        var verdict = Body("Verdict");
        if (!VerdictAccepts(verdict, out var verdictReason))
            return new(false, Format(Body("Claim"), Body("How tested"), evidence, Body("Paths covered"), "fail because " + verdictReason));

        return new(true, Format(Body("Claim"), Body("How tested"), evidence, Body("Paths covered"), verdict));
    }

    private static readonly string[] Headings = ["Claim", "How tested", "Evidence", "Paths covered", "Verdict"];

    private static Dictionary<string, string> Parse(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        var buffer = new System.Text.StringBuilder();
        void Flush()
        {
            if (current is null) return;
            map[current] = buffer.ToString().Trim();
            buffer.Clear();
        }
        foreach (var raw in text.Split('\n'))
        {
            var heading = MatchHeading(raw.Trim());
            if (heading is not null)
            {
                Flush();
                current = heading;
                var colon = raw.IndexOf(':');
                if (colon >= 0 && colon + 1 < raw.Length) buffer.Append(raw[(colon + 1)..].Trim());
                continue;
            }
            if (current is null) continue;
            if (buffer.Length > 0) buffer.Append('\n');
            buffer.Append(raw.Trim());
        }
        Flush();
        return map;
    }

    private static string? MatchHeading(string line)
    {
        var trimmed = line.Trim().TrimStart('#', '*', '-', ' ');
        foreach (var heading in Headings)
        {
            if (!trimmed.StartsWith(heading, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = trimmed[heading.Length..].TrimStart();
            if (rest.Length == 0 || rest.StartsWith(':')) return heading;
        }
        return null;
    }

    private static bool IsWeak(string text)
    {
        var compact = string.Join(" ", text.Split([' ', '\n', '\r', '\t', '"', '\'', '`'], StringSplitOptions.RemoveEmptyEntries))
            .Trim()
            .TrimEnd('.', '!', '?')
            .ToLowerInvariant();
        return compact is "it builds" or "it opens" or "it works" or "builds" or "opens" or "done" or "ok" or "success" or "pass";
    }

    private static bool VerdictAccepts(string verdict, out string reason)
    {
        var lower = string.Join(" ", verdict.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        if (lower.StartsWith("fail") || lower.Contains(" fail"))
        {
            reason = "the verdict is fail";
            return false;
        }
        if (!lower.StartsWith("pass"))
        {
            reason = "the verdict must start with pass or fail and give a reason";
            return false;
        }
        var rest = lower.Length > 4 ? lower[4..].Trim().TrimStart(':', '-', ' ') : "";
        if (rest.Length < 8)
        {
            reason = "the verdict has no reason";
            return false;
        }
        reason = "";
        return true;
    }
}
