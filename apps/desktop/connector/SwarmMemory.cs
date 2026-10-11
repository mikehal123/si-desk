using System.Text.Json;

namespace Sovereign.Desktop.Connector;

/// <summary>
/// Project handoff notes for swarm runs. Findings stay unverified.
/// A note is verified only when a verifier task on that run passed.
/// </summary>
public static class SwarmMemory
{
    public const string Decision = "decision";
    public const string Constraint = "constraint";
    public const string Reference = "reference";
    public const string Pitfall = "pitfall";
    public const string Finding = "finding";
    public const string Verified = "verified";

    public static string Label(string? kind) => kind switch
    {
        Verified => "Verified",
        Finding => "Finding (unverified)",
        Decision => "Decision",
        Constraint => "Constraint",
        Reference => "Reference",
        Pitfall => "Pitfall",
        _ => kind ?? ""
    };

    public static string Brief(IReadOnlyList<Note> notes)
    {
        var body = notes.Count == 0
            ? "None saved."
            : string.Join("\n", notes.Select(note => "- " + Label(note.Kind) + ": " + note.Text));
        return "Project memory (saved notes for this folder, not proof). Read the current files before relying on them. "
            + "Findings are unverified. Use verified only for a check the verifier passed.\n"
            + body;
    }

    public static IReadOnlyList<Note> Load(string profile, string folder)
    {
        ImportTeam(profile, folder);
        return Read(profile).NotesFor(folder);
    }

    public static bool RememberRun(string profile, string folder, SwarmBoard board, string? managerReply)
    {
        if (board.Tasks.Count == 0 || board.Tasks.Any(task => task.Status is not (SwarmStatus.Done or SwarmStatus.Failed)))
            return false;
        var passed = VerifierPassed(board);
        var incoming = new List<Note>();
        incoming.AddRange(NotesFromTasks(board));
        incoming.AddRange(ReadHandoff(managerReply, "swarm/" + board.Id + "/reply", passed));
        return Merge(profile, folder, incoming);
    }

    public static bool AppendReply(string profile, string folder, string? reply, string source, bool verifierPassed) =>
        Merge(profile, folder, ReadHandoff(reply, source, verifierPassed));

    public static int ImportTeam(string profile, string folder)
    {
        var path = Path.Combine(profile, "team", "team.json");
        if (!File.Exists(path)) return 0;
        TeamFile? document;
        try { document = JsonSerializer.Deserialize<TeamFile>(File.ReadAllText(path)); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return 0; }
        var project = document?.Projects?.FirstOrDefault(item => item?.Folder is not null && SameFolder(item.Folder, folder));
        var notes = project?.Handoff?.Notes;
        if (notes is null || notes.Length == 0) return 0;
        var incoming = new List<Note>();
        foreach (var note in notes)
        {
            if (note is null || !Known(note.Kind) || string.IsNullOrWhiteSpace(note.Text)) continue;
            var kind = note.Kind == Verified ? Verified : note.Kind!;
            incoming.Add(new Note(string.IsNullOrWhiteSpace(note.Id) ? Guid.NewGuid().ToString("N") : note.Id!, kind, Clip(note.Text), note.Source ?? "team"));
        }
        var before = Read(profile).NotesFor(folder).Count;
        Merge(profile, folder, incoming);
        return Math.Max(0, Read(profile).NotesFor(folder).Count - before);
    }

    public static IReadOnlyList<Note> ReadHandoff(string? reply, string source, bool verifierPassed)
    {
        if (string.IsNullOrEmpty(reply)) return [];
        var start = reply.IndexOf("SOVEREIGN_HANDOFF", StringComparison.Ordinal);
        if (start < 0) return [];
        var end = reply.IndexOf("END_SOVEREIGN_HANDOFF", start + "SOVEREIGN_HANDOFF".Length, StringComparison.Ordinal);
        if (end < 0) return [];
        var notes = new List<Note>();
        foreach (var raw in reply[(start + "SOVEREIGN_HANDOFF".Length)..end].Split('\n'))
        {
            var line = raw.Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var kind = line[..colon].Trim().ToLowerInvariant();
            if (!Known(kind)) continue;
            var text = Clip(line[(colon + 1)..]);
            if (text.Length == 0) continue;
            if (kind == Verified && !verifierPassed) kind = Finding;
            notes.Add(new Note(Guid.NewGuid().ToString("N"), kind, text, source));
        }
        return notes;
    }

    public static bool VerifierPassed(SwarmBoard board)
    {
        foreach (var task in board.Tasks)
        {
            if (!string.Equals(task.Role, SwarmRole.Verifier, StringComparison.OrdinalIgnoreCase)) continue;
            if (task.Status != SwarmStatus.Done) continue;
            var note = SwarmSection.ReadNote(task.VerifierNote);
            if (PassVerdict(note.Verdict)) return true;
        }
        return false;
    }

    public static bool PassVerdict(string? verdict)
    {
        var lower = string.Join(" ", (verdict ?? "").Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        if (!lower.StartsWith("pass") || lower.StartsWith("fail") || lower.Contains(" fail")) return false;
        var rest = lower.Length > 4 ? lower[4..].Trim().TrimStart(':', '-', ' ') : "";
        return rest.Length >= 8;
    }

    private static IEnumerable<Note> NotesFromTasks(SwarmBoard board)
    {
        foreach (var task in board.Tasks)
        {
            var source = "swarm/" + board.Id + "/task/" + task.Id;
            if (string.Equals(task.Role, SwarmRole.Verifier, StringComparison.OrdinalIgnoreCase))
            {
                if (task.Status != SwarmStatus.Done) continue;
                var note = SwarmSection.ReadNote(task.VerifierNote);
                if (!PassVerdict(note.Verdict)) continue;
                var text = Clip(string.IsNullOrWhiteSpace(note.Claim) ? note.Verdict : note.Claim);
                if (text.Length == 0) continue;
                yield return new Note(Guid.NewGuid().ToString("N"), Verified, text, source);
                continue;
            }
            if (task.Status != SwarmStatus.Done) continue;
            var body = Clip(string.IsNullOrWhiteSpace(task.Result) ? task.Proof : task.Result);
            if (body.Length == 0) continue;
            var title = string.IsNullOrWhiteSpace(task.Title) ? task.Id : task.Title.Trim();
            yield return new Note(Guid.NewGuid().ToString("N"), Finding, Clip(title + ": " + body), source);
        }
    }

    private static bool Merge(string profile, string folder, IReadOnlyList<Note> incoming)
    {
        if (incoming.Count == 0 || string.IsNullOrWhiteSpace(folder)) return false;
        var file = Read(profile);
        var notes = file.NotesFor(folder).ToList();
        var added = false;
        foreach (var note in incoming)
        {
            if (!Known(note.Kind) || string.IsNullOrWhiteSpace(note.Text)) continue;
            if (notes.Any(item => item.Kind == note.Kind && item.Text == note.Text)) continue;
            notes.Add(note);
            added = true;
        }
        if (!added) return false;
        if (notes.Count > 24) notes = notes.Skip(notes.Count - 24).ToList();
        file.Put(folder, notes);
        file.Write(profile);
        return true;
    }

    private static bool Known(string? kind) =>
        kind is Decision or Constraint or Reference or Pitfall or Finding or Verified;

    private static string Clip(string? text)
    {
        var value = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return value.Length <= 240 ? value : value[..240];
    }

    private static bool SameFolder(string left, string right)
    {
        try
        {
            var a = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var b = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException) { return false; }
    }

    private static Store Read(string profile)
    {
        var path = StorePath(profile);
        if (!File.Exists(path)) return new Store();
        try
        {
            return JsonSerializer.Deserialize<Store>(File.ReadAllText(path)) ?? new Store();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new Store(); }
    }

    private static string StorePath(string profile) => Path.Combine(profile, "swarm-memory", "projects.json");

    public sealed record Note(string Id, string Kind, string Text, string Source);

    private sealed class Store
    {
        public List<Project> Projects { get; set; } = [];

        public List<Note> NotesFor(string folder) =>
            Projects.FirstOrDefault(item => item.Folder is not null && SameFolder(item.Folder, folder))?.Notes ?? [];

        public void Put(string folder, List<Note> notes)
        {
            var key = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var existing = Projects.FirstOrDefault(item => item.Folder is not null && SameFolder(item.Folder, key));
            if (existing is null) Projects.Add(new Project { Folder = key, Notes = notes });
            else existing.Notes = notes;
        }

        public void Write(string profile)
        {
            var path = StorePath(profile);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this));
            File.Move(temp, path, true);
        }
    }

    private sealed class Project
    {
        public string? Folder { get; set; }
        public List<Note> Notes { get; set; } = [];
    }

    private sealed class TeamFile
    {
        public TeamProjectFile[]? Projects { get; set; }
    }

    private sealed class TeamProjectFile
    {
        public string? Folder { get; set; }
        public TeamHandoffFile? Handoff { get; set; }
    }

    private sealed class TeamHandoffFile
    {
        public TeamNoteFile[]? Notes { get; set; }
    }

    private sealed class TeamNoteFile
    {
        public string? Id { get; set; }
        public string? Kind { get; set; }
        public string? Text { get; set; }
        public string? Source { get; set; }
    }
}
