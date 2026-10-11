using System.Text.Json;

namespace Sovereign.Desktop.Client;

public sealed record SavedChatProject(string Id, string Name, DateTimeOffset Created, bool Collapsed = false, string? Folder = null);

/// <summary>Profile-local project index. Stored beside chats, never inside chats, so ChatArchive.Load cannot treat it as a transcript.</summary>
public sealed class ChatProjectCatalog(string profile)
{
    private readonly string _path = Path.Combine(profile, "chat-projects.json");

    public IReadOnlyList<SavedChatProject> Load(Action<string>? warning = null)
    {
        if (!File.Exists(_path)) return [];
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(_path));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Version", out var versionElement) ||
                !versionElement.TryGetInt32(out var version) || version != 1 ||
                !root.TryGetProperty("Projects", out var projectsElement) || projectsElement.ValueKind != JsonValueKind.Array)
            {
                warning?.Invoke(Path.GetFileName(_path) + ": Invalid project catalog.");
                return [];
            }
            var accepted = new List<SavedChatProject>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in projectsElement.EnumerateArray())
            {
                SavedChatProject? project = null;
                if (item.ValueKind == JsonValueKind.Object)
                {
                    try { project = item.Deserialize<SavedChatProject>(); }
                    catch (JsonException) { project = null; }
                }
                if (project is null || !Guid.TryParseExact(project.Id, "N", out _))
                { warning?.Invoke(Path.GetFileName(_path) + ": Skipped a project with an invalid identity."); continue; }
                if (string.IsNullOrWhiteSpace(project.Name))
                { warning?.Invoke(Path.GetFileName(_path) + ": Skipped a project with a blank name."); continue; }
                var name = project.Name.Trim();
                if (name.Length > 80 || name.Any(c => char.IsControl(c) || c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|'))
                { warning?.Invoke(Path.GetFileName(_path) + ": Skipped a project with an invalid name."); continue; }
                if (!ids.Add(project.Id))
                { warning?.Invoke(Path.GetFileName(_path) + ": Skipped a duplicate project identity."); continue; }
                if (!names.Add(name))
                { warning?.Invoke(Path.GetFileName(_path) + ": Skipped a duplicate project name."); continue; }
                if (project.Folder is not null && (!Path.IsPathFullyQualified(project.Folder) || project.Folder.Any(char.IsControl)))
                { warning?.Invoke(Path.GetFileName(_path) + ": Skipped a project with an invalid folder."); continue; }
                accepted.Add(project with { Name = name });
            }
            return accepted.OrderBy(p => p.Created).ThenBy(p => p.Id, StringComparer.Ordinal).ToArray();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            warning?.Invoke(Path.GetFileName(_path) + ": " + ex.Message);
            return [];
        }
    }

    public void Save(IReadOnlyList<SavedChatProject> projects)
    {
        var list = new List<SavedChatProject>(projects.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        {
            if (!Guid.TryParseExact(project.Id, "N", out _)) throw new ArgumentException("Invalid project identity.");
            if (!TryNormalizeName(project.Name, [], null, out var name, out _)) throw new ArgumentException("Invalid project name.");
            if (project.Folder is not null && (!Path.IsPathFullyQualified(project.Folder) || project.Folder.Any(char.IsControl))) throw new ArgumentException("Invalid project folder.");
            if (!ids.Add(project.Id)) throw new ArgumentException("Invalid project identity.");
            if (!names.Add(name)) throw new ArgumentException("Invalid project name.");
            list.Add(project with { Name = name });
        }
        Directory.CreateDirectory(profile);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(new { Version = 1, Projects = list })); File.Move(temp, _path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static bool TryNormalizeName(string? name, IReadOnlyList<SavedChatProject> existing, string? exceptId, out string normalized, out string? error)
    {
        normalized = (name ?? "").Trim();
        if (normalized.Length == 0) { normalized = ""; error = "Enter a project name."; return false; }
        if (normalized.Length > 80) { normalized = ""; error = "Use 80 characters or fewer."; return false; }
        if (normalized.Any(c => char.IsControl(c) || c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|'))
        { normalized = ""; error = "Use a name without file symbols."; return false; }
        var candidate = normalized;
        if (existing.Any(project => !string.Equals(project.Id, exceptId, StringComparison.Ordinal) &&
            string.Equals(project.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        { normalized = ""; error = "A project with that name already exists."; return false; }
        error = null;
        return true;
    }

    public static bool TryCreate(string? name, IReadOnlyList<SavedChatProject> existing, DateTimeOffset now, out SavedChatProject? project, out string? error)
    {
        project = null;
        if (!TryNormalizeName(name, existing, null, out var normalized, out error)) return false;
        project = new SavedChatProject(Guid.NewGuid().ToString("N"), normalized, now);
        return true;
    }

    public static bool TryRename(SavedChatProject project, string? name, IReadOnlyList<SavedChatProject> existing, out SavedChatProject? renamed, out string? error)
    {
        renamed = null;
        if (!TryNormalizeName(name, existing, project.Id, out var normalized, out error)) return false;
        renamed = project with { Name = normalized };
        return true;
    }
}

public static class ChatProjectAssignment
{
    public static SavedChat Move(SavedChat chat, string? projectId)
    {
        if (projectId is not null && !Guid.TryParseExact(projectId, "N", out _)) throw new ArgumentException("Invalid project identity.");
        return chat with { ProjectId = projectId };
    }

    public static IReadOnlyList<SavedChat> Unfile(IReadOnlyList<SavedChat> chats, string projectId)
    {
        if (!Guid.TryParseExact(projectId, "N", out _)) throw new ArgumentException("Invalid project identity.");
        return chats.Select(chat => chat.ProjectId == projectId ? chat with { ProjectId = null } : chat).ToArray();
    }
}
