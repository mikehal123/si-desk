namespace Sovereign.Desktop.Core;

public static class WorkDoctrine
{
    public static string? Warning { get; private set; }

    public static string Text()
    {
        var loaded = Load(null, true);
        Warning = loaded.Warning;
        return loaded.Text;
    }

    public static string PromptPrefix()
    {
        var text = Text();
        var head = Warning is null ? "" : "Warning: " + Warning + "\n\n";
        return head
            + "Standing skill work-doctrine. The main agent loads it by default. Every subagent and every swarm role loads it by default. When you spawn a helper, put the Work Doctrine text below at the top of that helper's task.\n\n"
            + text + "\n\n";
    }

    public static (string Text, string? Warning) Load(string? start, bool searchParents)
    {
        var path = searchParents ? FindFile(start) : Direct(start);
        if (path is null)
            return (BuiltIn(), "work-doctrine skill was not found under .cursor/skills. Using the built-in copy.");
        try
        {
            var text = File.ReadAllText(path).Replace("\r\n", "\n").Trim();
            if (!text.StartsWith("# Work Doctrine", StringComparison.Ordinal))
                return (BuiltIn(), "work-doctrine skill file is not the expected skill. Using the built-in copy.");
            return (text, IndexWarning(path, "work-doctrine", "main", "subagent"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (BuiltIn(), "work-doctrine skill could not be read. Using the built-in copy.");
        }
    }

    public static string? FindFile(string? start = null)
    {
        var dir = new DirectoryInfo(string.IsNullOrWhiteSpace(start) ? AppContext.BaseDirectory : start);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, ".cursor", "skills", "work-doctrine", "SKILL.md");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    public static string? IndexWarning(string skillFile, string name, params string[] roles)
    {
        var indexPath = Path.Combine(Directory.GetParent(skillFile)!.Parent!.FullName, "index.json");
        if (!File.Exists(indexPath)) return "Skill index is missing. Using the skill file that was found.";
        string index;
        try { index = File.ReadAllText(indexPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "Skill index could not be read. Using the skill file that was found.";
        }
        if (!index.Contains("\"" + name + "\"", StringComparison.Ordinal))
            return name + " is not listed in the skill index. Using the skill file that was found.";
        foreach (var role in roles)
            if (!index.Contains("\"" + role + "\"", StringComparison.Ordinal))
                return name + " is not indexed for " + role + ". Using the skill file that was found.";
        return null;
    }

    private static string? Direct(string? start)
    {
        if (string.IsNullOrWhiteSpace(start)) return null;
        var candidate = Path.Combine(start, ".cursor", "skills", "work-doctrine", "SKILL.md");
        return File.Exists(candidate) ? candidate : null;
    }

    private static string BuiltIn()
    {
        var assembly = typeof(WorkDoctrine).Assembly;
        using var stream = assembly.GetManifestResourceStream("Sovereign.Desktop.Core.work-doctrine.md");
        if (stream is null) return Minimal;
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd().Replace("\r\n", "\n").Trim();
        return text.StartsWith("# Work Doctrine", StringComparison.Ordinal) ? text : Minimal;
    }

    private const string Minimal = """
        # Work Doctrine
        The skill file was not found. Follow rollback, report failures, and do not call work done without proof from the verify-result skill.
        """;
}
