using System.Text.Json;

namespace Sovereign.Desktop.Client;

/// <summary>
/// Opt-in Team mode. Roles are stable instruction profiles. Each delegation is a separate
/// assignment keyed by the runtime helper session, stored beside the chat archive.
/// </summary>
public static class TeamRoles
{
    public const int StoreVersion = 1;
    public const string TaskMarker = "SOVEREIGN_TEAM role=";
    public static TeamRole Investigator { get; } = new("investigator", "Investigator",
        "Trace the problem and return file-level evidence. Read the current files before using older notes. Report paths and the lines you observed. Do not implement the change.");
    public static TeamRole Builder { get; } = new("builder", "Builder",
        "Implement the requested change. Read the current files before using older notes. Stay inside this chat's existing permission policy. Report the files you changed and the checks you actually ran.");
    public static TeamRole Reviewer { get; } = new("reviewer", "Reviewer",
        "Inspect the change and the reported verification, then report defects you can actually see. Read the current files before using older notes. Do not treat earlier notes as proof.");
    public static IReadOnlyList<TeamRole> BuiltIn { get; } = [Investigator, Builder, Reviewer];
    public static bool TryGet(string? id, out TeamRole role)
    {
        foreach (var item in BuiltIn)
            if (item.Id == id) { role = item; return true; }
        role = null!;
        return false;
    }
}

public sealed record TeamRole(string Id, string Name, string Instructions);

public sealed record TeamAssignment(string Id, string RoleId, string RoleName, string RoleInstructions, string ChatId,
    int TurnIndex, string SessionId, string Task, string State, DateTimeOffset StartedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt, string? Failure, string ResultRef, bool Unconfirmed = false,
    DateTimeOffset? RuntimeStartedAt = null, string? ResultOutput = null);

public sealed record TeamNote(string Id, string Kind, string Text, string Source);

public sealed record TeamHandoff(TeamNote[] Notes);

public sealed record TeamLimitation(string Id, string SessionId, string ChatId, int TurnIndex, string Reason, DateTimeOffset At);

public sealed record TeamProject(string Folder, TeamAssignment[] Assignments, TeamHandoff Handoff, TeamLimitation[] Limitations);

public sealed record TeamMapResult(TeamAssignment? Assignment, string? Limitation);

public sealed record TeamRoleBoard(string RoleId, string Name, string Status, TeamAssignment? Current, TeamAssignment[] Previous);

public sealed record TeamBoard(TeamRoleBoard[] Roles, TeamLimitation[] Unassigned, TeamNote[] Notes, string? Warning);

public static class TeamPrompt
{
    public static string Compose(bool enabled, string userText, TeamProject? project) =>
        enabled ? Compose(userText, project ?? Empty()) : userText;

    public static string Compose(string userText, TeamProject project)
    {
        var notes = project.Handoff.Notes.Length == 0
            ? "None saved."
            : string.Join("\n", project.Handoff.Notes.Select(note => "- " + Label(note.Kind) + ": " + note.Text + " [" + note.Source + "]"));
        var roles = string.Join("\n\n", TeamRoles.BuiltIn.Select(role =>
            "Role id: " + role.Id + "\nDisplay name: " + role.Name + "\nInstructions: " + role.Instructions));
        return "SOVEREIGN_TEAM_CONTEXT v" + TeamRoles.StoreVersion + "\n"
            + "Team mode is on. You remain the person's conversational contact. The three roles below are reusable instruction profiles for native helpers. "
            + "A role name never grants authority, a new tool, or a different permission policy. Helpers inherit this chat's mode and permissions. "
            + "Use the existing helper mechanism only: at most three direct helpers at once, no nested helpers, the runtime's advertised helper type, and no model override.\n"
            + "Do not claim a role ran unless a helper was actually started. If you cannot delegate, say so.\n"
            + "Saved notes are project memory, not a claim that a role has learned the project. Read the current files before relying on them. Findings are not verified facts.\n\n"
            + roles + "\n\nProject handoff:\n" + notes + "\n\n"
            + "When you delegate, the helper task must start with this exact first line, using only investigator, builder, or reviewer:\n"
            + "SOVEREIGN_TEAM role=<role id>\n"
            + "Put the role instructions and the specific task on the following lines. Do not invent roles. "
            + "If the user names one of these roles, including a correction such as asking the reviewer to check something, delegate that role with the same first line.\n"
            + "After helpers finish, you may append a handoff block for items you can point at. Use finding for an observation and verified only when a check actually succeeded. Omit the block when nothing was confirmed.\n"
            + "SOVEREIGN_HANDOFF\n"
            + "finding: observation\n"
            + "verified: check that succeeded\n"
            + "END_SOVEREIGN_HANDOFF\n"
            + "Do not describe a role as having learned from earlier tasks.\n\n"
            + "CURRENT USER MESSAGE:\n" + userText;
    }

    public static bool TryReadRole(string? task, out string roleId, out string body)
    {
        roleId = "";
        body = task ?? "";
        if (string.IsNullOrEmpty(task)) return false;
        var split = task.IndexOfAny(['\r', '\n']);
        var first = (split < 0 ? task : task[..split]).Trim();
        if (!first.StartsWith(TeamRoles.TaskMarker, StringComparison.Ordinal)) return false;
        roleId = first[TeamRoles.TaskMarker.Length..].Trim();
        if (!TeamRoles.TryGet(roleId, out _)) return false;
        body = split < 0 ? "" : task[(split + 1)..].Trim().TrimStart('\r', '\n');
        return true;
    }

    public static TeamNote[] ReadHandoff(string? reply, string source)
    {
        if (string.IsNullOrEmpty(reply)) return [];
        var start = reply.IndexOf("SOVEREIGN_HANDOFF", StringComparison.Ordinal);
        if (start < 0) return [];
        var end = reply.IndexOf("END_SOVEREIGN_HANDOFF", start + "SOVEREIGN_HANDOFF".Length, StringComparison.Ordinal);
        if (end < 0) return [];
        var notes = new List<TeamNote>();
        foreach (var raw in reply[(start + "SOVEREIGN_HANDOFF".Length)..end].Split('\n'))
        {
            var line = raw.Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var kind = line[..colon].Trim().ToLowerInvariant();
            if (kind is not ("decision" or "constraint" or "reference" or "pitfall" or "finding" or "verified")) continue;
            var text = line[(colon + 1)..].Trim();
            if (text.Length == 0) continue;
            if (text.Length > 240) text = text[..240];
            notes.Add(new(Guid.NewGuid().ToString("N"), kind, text, source));
        }
        return notes.ToArray();
    }

    public static string? MapRuntimeState(string? runtime) => runtime switch
    {
        "running" or "pending" or "queued" or "waiting" => "working",
        "completed" => "completed",
        "failed" => "failed",
        "cancelled" or "canceled" or "disconnected" or "stop requested" => "interrupted",
        _ => null
    };

    public static TeamBoard BuildBoard(TeamProject project, string? warning = null)
    {
        var roles = TeamRoles.BuiltIn.Select(role =>
        {
            var mine = project.Assignments.Where(item => item.RoleId == role.Id).OrderBy(item => item.StartedAt).ThenBy(item => item.Id).ToArray();
            var current = mine.LastOrDefault(item => item.State == "working") ?? mine.LastOrDefault();
            var previous = mine.Where(item => current is null || item.Id != current.Id).ToArray();
            var status = current is null ? "Idle" : current.State switch
            {
                "working" => "Working", "completed" => "Completed", "failed" => "Failed", "interrupted" => "Interrupted", _ => current.State
            };
            return new TeamRoleBoard(role.Id, role.Name, status, current, previous);
        }).ToArray();
        return new(roles, project.Limitations, project.Handoff.Notes, warning);
    }

    private static string Label(string kind) => kind switch
    {
        "verified" => "Verified",
        "finding" => "Finding (not verified)",
        "decision" => "Decision",
        "constraint" => "Constraint",
        "reference" => "Reference",
        "pitfall" => "Pitfall",
        _ => kind
    };

    private static TeamProject Empty() => new("", [], new([]), []);
}

public sealed class TeamStore(string profile)
{
    private readonly string _path = Path.Combine(profile, "team", "team.json");
    public string PathToFile => _path;

    public (TeamProject Project, string? Warning, bool SaveBlocked) Load(string folder)
    {
        var empty = new TeamProject(Normalize(folder), [], new([]), []);
        if (!File.Exists(_path)) return (empty, null, false);
        try
        {
            var info = new FileInfo(_path);
            if (info.Length > 2 * 1024 * 1024) return (empty, "Team metadata is too large to load. Conversations are unchanged.", true);
            var document = JsonSerializer.Deserialize<TeamDocument>(File.ReadAllText(_path)) ?? throw new JsonException("Empty Team metadata.");
            if (document.Version != TeamRoles.StoreVersion || document.Projects is null)
                return (empty, "Team metadata version is not supported (expected version " + TeamRoles.StoreVersion + "). Conversations are unchanged.", true);
            var match = document.Projects.FirstOrDefault(item => item is not null && FolderEquals(item.Folder, folder));
            if (match is null) return (empty, null, false);
            return Validate(match, empty);
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        { return (empty, "Team metadata could not be read (" + ex.Message + "). Conversations are unchanged.", true); }
    }

    public TeamProject Update(string folder, Func<TeamProject, TeamProject> change)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var document = ReadForUpdate();
            var stored = document.Projects ?? throw new IOException("Team metadata is missing projects.");
            var projects = stored.Where(item => item is not null && !FolderEquals(item.Folder, folder)).ToList();
            var existing = stored.FirstOrDefault(item => item is not null && FolderEquals(item.Folder, folder))
                ?? new TeamProject(Normalize(folder), [], new([]), []);
            var updated = change(existing with { Folder = Normalize(folder) });
            if (JsonSerializer.Serialize(updated).Length > 1024 * 1024)
                throw new IOException("Team metadata for this project would be too large.");
            projects.Add(updated);
            File.WriteAllText(temp, JsonSerializer.Serialize(new TeamDocument { Version = TeamRoles.StoreVersion, Projects = projects.ToArray() }));
            File.Move(temp, _path, true);
            return updated;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private TeamDocument ReadForUpdate()
    {
        if (!File.Exists(_path)) return new() { Version = TeamRoles.StoreVersion, Projects = [] };
        var document = JsonSerializer.Deserialize<TeamDocument>(File.ReadAllText(_path)) ?? throw new IOException("Team metadata is empty.");
        if (document.Version != TeamRoles.StoreVersion || document.Projects is null)
            throw new IOException("Team metadata version is not supported. The file was left unchanged.");
        foreach (var project in document.Projects)
        {
            if (project is null || string.IsNullOrWhiteSpace(project.Folder))
                throw new IOException("Team metadata contains an invalid project.");
            var validated = Validate(project, new(Normalize(project.Folder), [], new([]), []));
            if (validated.SaveBlocked) throw new IOException("Team metadata contains invalid records.");
        }
        return document;
    }

    private static (TeamProject Project, string? Warning, bool SaveBlocked) Validate(TeamProject project, TeamProject empty)
    {
        var warnings = new List<string>();
        if (project.Assignments is null || project.Limitations is null || project.Handoff?.Notes is null)
            warnings.Add("missing collection");
        var assignments = new List<TeamAssignment>();
        var seen = new HashSet<string>();
        foreach (var item in project.Assignments ?? [])
        {
            if (!Valid(item) || !seen.Add(item.Id)) { warnings.Add("invalid assignment"); continue; }
            assignments.Add(item);
        }
        var limitations = new List<TeamLimitation>();
        foreach (var item in project.Limitations ?? [])
        {
            if (item is null || !Guid.TryParseExact(item.Id, "N", out _) || string.IsNullOrWhiteSpace(item.SessionId) || string.IsNullOrWhiteSpace(item.Reason))
            { warnings.Add("invalid limitation"); continue; }
            limitations.Add(item);
        }
        var notes = new List<TeamNote>();
        foreach (var item in project.Handoff?.Notes ?? [])
        {
            if (item is null || !Guid.TryParseExact(item.Id, "N", out _) || item.Kind is not ("decision" or "constraint" or "reference" or "pitfall" or "finding" or "verified")
                || string.IsNullOrWhiteSpace(item.Text) || string.IsNullOrWhiteSpace(item.Source))
            { warnings.Add("invalid note"); continue; }
            notes.Add(item);
        }
        var warning = warnings.Count == 0 ? null : "Team metadata skipped " + warnings.Count + " invalid item(s). Conversations are unchanged.";
        return (empty with { Assignments = assignments.ToArray(), Handoff = new(notes.ToArray()), Limitations = limitations.ToArray() }, warning, warnings.Count > 0);
    }

    private static bool Valid(TeamAssignment? item)
    {
        if (item is null || !Guid.TryParseExact(item.Id, "N", out _) || !TeamRoles.TryGet(item.RoleId, out var role) || item.RoleName != role.Name) return false;
        if (string.IsNullOrWhiteSpace(item.RoleInstructions) || !Guid.TryParseExact(item.ChatId, "N", out _) || item.TurnIndex < 0) return false;
        if (string.IsNullOrWhiteSpace(item.SessionId) || item.SessionId.Length > 200 || item.SessionId.Any(char.IsControl)) return false;
        if (item.SessionId.Equals(item.RoleId, StringComparison.OrdinalIgnoreCase) || item.SessionId.Equals(role.Name, StringComparison.OrdinalIgnoreCase)) return false;
        if (item.Task is null || item.State is not ("working" or "completed" or "failed" or "interrupted")) return false;
        if (item.ResultOutput?.Length > 6000) return false;
        return item.ResultRef == TeamBook.ResultRef(item.ChatId, item.TurnIndex, item.SessionId)
            || item.ResultRef == TeamBook.ResultRef(item.ChatId, item.TurnIndex, item.SessionId, item.Id);
    }

    public static string Normalize(string folder) => Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    public static bool FolderEquals(string? left, string? right) =>
        left is not null && right is not null && string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    private sealed class TeamDocument
    {
        public int Version { get; set; }
        public TeamProject[]? Projects { get; set; }
    }
}

public sealed class TeamBook
{
    private readonly TeamStore _store;
    private bool _dirty;
    public string Folder { get; }
    public TeamProject Project { get; private set; }
    public string? Warning { get; private set; }
    public bool SaveBlocked { get; private set; }

    private TeamBook(TeamStore store, string folder, TeamProject project, string? warning, bool saveBlocked)
    {
        _store = store; Folder = folder; Project = project; Warning = warning; SaveBlocked = saveBlocked;
    }

    public static TeamBook Open(TeamStore store, string folder)
    {
        var loaded = store.Load(folder);
        return new(store, TeamStore.Normalize(folder), loaded.Project, loaded.Warning, loaded.SaveBlocked);
    }

    public TeamMapResult AcceptSpawn(string sessionId, string? task, string chatId, int turnIndex, DateTimeOffset now,
        string? runtimeState = "running", DateTimeOffset? runtimeStartedAt = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 200 || sessionId.Any(char.IsControl))
            return new(null, "Helper session id was missing or unusable. No Team role was assigned.");
        if (!Guid.TryParseExact(chatId, "N", out _) || turnIndex < 0)
            return new(null, "This helper started before its chat turn was saved. No Team role was assigned.");
        var existing = Project.Assignments.LastOrDefault(item => item.SessionId == sessionId && item.ChatId == chatId
            && (runtimeStartedAt is not null ? item.RuntimeStartedAt == runtimeStartedAt : item.TurnIndex == turnIndex));
        existing ??= Project.Assignments.LastOrDefault(item => item.SessionId == sessionId && item.ChatId == chatId
            && item.TurnIndex == turnIndex && item.RuntimeStartedAt is null && item.State == "working");
        // A repeated terminal snapshot is an observation of an existing run, never a launch.
        if (TeamPrompt.MapRuntimeState(runtimeState) != "working") return new(existing, null);
        if (existing is not null && existing.State != "working" && runtimeStartedAt is not null)
            return new(existing, null);
        if (!TeamPrompt.TryReadRole(task, out var roleId, out var body) || !TeamRoles.TryGet(roleId, out var role))
        {
            if (string.IsNullOrWhiteSpace(task)) return new(null, null);
            var reason = "This helper was not assigned to a Team role. Its task did not start with an explicit role id, and role identity was not taken from the helper name or reply.";
            RememberLimitation(sessionId, chatId, turnIndex, reason, now);
            return new(null, reason);
        }
        if (sessionId.Equals(role.Id, StringComparison.OrdinalIgnoreCase) || sessionId.Equals(role.Name, StringComparison.OrdinalIgnoreCase))
        {
            var reason = "The runtime session id matched a role name. It was not stored as a Team assignment.";
            RememberLimitation(sessionId, chatId, turnIndex, reason, now);
            return new(null, reason);
        }
        var taskText = Clip(body, 2000);
        if (existing is { State: "working" })
        {
            // Native runtimes can supply task details after the initial running snapshot.
            var updated = existing with { RoleId = role.Id, RoleName = role.Name, RoleInstructions = role.Instructions,
                Task = taskText, RuntimeStartedAt = existing.RuntimeStartedAt ?? runtimeStartedAt };
            if (updated != existing)
            {
                Project = Project with { Assignments = Project.Assignments.Select(item => item.Id == existing.Id ? updated with { UpdatedAt = now } : item).ToArray() };
                _dirty = true;
            }
            return new(updated, null);
        }
        if (Project.Assignments.Count(item => item.ChatId == chatId) >= 200)
        {
            var reason = "This project already has 200 Team assignments. This helper was not stored.";
            RememberLimitation(sessionId, chatId, turnIndex, reason, now);
            return new(null, reason);
        }
        var id = Guid.NewGuid().ToString("N");
        var assignment = new TeamAssignment(id, role.Id, role.Name, role.Instructions, chatId, turnIndex,
            sessionId, taskText, "working", now, now, null, null, ResultRef(chatId, turnIndex, sessionId, id),
            RuntimeStartedAt: runtimeStartedAt, ResultOutput: "");
        var limitations = Project.Limitations.Where(item => !(item.SessionId == sessionId && item.ChatId == chatId && item.TurnIndex == turnIndex)).ToArray();
        Project = Project with { Assignments = [.. Project.Assignments, assignment], Limitations = limitations };
        _dirty = true;
        return new(assignment, null);
    }

    public bool ApplyRuntime(string sessionId, string? runtimeState, string? failure, DateTimeOffset now,
        string? output = null, DateTimeOffset? runtimeStartedAt = null, string? chatId = null)
    {
        var mapped = TeamPrompt.MapRuntimeState(runtimeState);
        if (mapped is null) return false;
        var unconfirmed = mapped == "interrupted" && IsUnconfirmed(runtimeState, failure);
        var index = IndexForRuntime(sessionId, runtimeStartedAt, chatId);
        if (index < 0) return false;
        var item = Project.Assignments[index];
        if (item.State != "working" && !(item.Unconfirmed && mapped != "working") && item.State != mapped) return false;
        var reason = mapped is "failed" or "interrupted"
            ? string.IsNullOrWhiteSpace(failure) ? item.Failure : Clip(failure.Trim(), 600)
            : null;
        var result = string.IsNullOrEmpty(output) ? item.ResultOutput : Clip(output, 6000);
        if (item.State == mapped && item.Unconfirmed == unconfirmed && reason == item.Failure && result == item.ResultOutput) return false;
        var next = Project.Assignments.ToArray();
        next[index] = item with
        {
            State = mapped, UpdatedAt = now, Failure = reason, ResultOutput = result,
            CompletedAt = mapped == "working" ? null : item.CompletedAt ?? now,
            Unconfirmed = unconfirmed
        };
        Project = Project with { Assignments = next };
        _dirty = true;
        return true;
    }

    private int IndexForRuntime(string sessionId, DateTimeOffset? runtimeStartedAt, string? chatId)
    {
        var latest = -1;
        for (var i = Project.Assignments.Length - 1; i >= 0; i--)
        {
            var item = Project.Assignments[i];
            if (item.SessionId != sessionId || chatId is not null && item.ChatId != chatId) continue;
            if (latest < 0) latest = i;
            if (runtimeStartedAt is null || item.RuntimeStartedAt == runtimeStartedAt) return i;
        }
        // Old metadata did not store the runtime start. Only its latest run is a safe fallback.
        return latest >= 0 && Project.Assignments[latest].RuntimeStartedAt is null ? latest : -1;
    }

    private static bool IsUnconfirmed(string? runtimeState, string? failure) =>
        runtimeState is "disconnected" || failure?.Contains("unconfirmed", StringComparison.OrdinalIgnoreCase) == true;

    public int ReconcileInactive(string chatId, IReadOnlySet<string> activeSessionIds, string reason, DateTimeOffset now)
    {
        var count = 0;
        var next = Project.Assignments.Select(item =>
        {
            if (item.ChatId != chatId || item.State != "working" || activeSessionIds.Contains(item.SessionId)) return item;
            count++;
            return item with { State = "interrupted", UpdatedAt = now, CompletedAt = item.CompletedAt ?? now, Unconfirmed = false,
                Failure = string.IsNullOrWhiteSpace(item.Failure) ? Clip(reason, 600) : item.Failure };
        }).ToArray();
        if (count == 0) return 0;
        Project = Project with { Assignments = next };
        _dirty = true;
        return count;
    }

    public bool RecordHandoff(string? reply, string source, DateTimeOffset now)
    {
        var incoming = TeamPrompt.ReadHandoff(reply, "team/v" + TeamRoles.StoreVersion + " " + source);
        if (incoming.Length == 0) return false;
        var notes = Project.Handoff.Notes.Concat(incoming).GroupBy(item => item.Id).Select(group => group.First()).TakeLast(12).ToArray();
        Project = Project with { Handoff = new(notes) };
        _dirty = true;
        return true;
    }

    public void Save()
    {
        if (!_dirty || SaveBlocked) return;
        try
        {
            var mine = Project;
            Project = _store.Update(Folder, disk => Merge(disk, mine));
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            SaveBlocked = true;
            Warning = "Team metadata could not be saved (" + ex.Message + "). The existing file was left unchanged.";
            _dirty = false;
        }
    }

    public static TeamProject Merge(TeamProject disk, TeamProject mine)
    {
        if (!TeamStore.FolderEquals(disk.Folder, mine.Folder)) throw new IOException("Team project folder changed.");
        var assignments = disk.Assignments.Concat(mine.Assignments)
            .GroupBy(item => item.Id)
            .Select(group => group.OrderByDescending(item => item.UpdatedAt).First())
            .OrderBy(item => item.StartedAt).ThenBy(item => item.Id)
            .ToArray();
        var limitations = disk.Limitations.Concat(mine.Limitations)
            .GroupBy(item => item.Id)
            .Select(group => group.OrderByDescending(item => item.At).First())
            .OrderBy(item => item.At)
            .ToArray();
        var notes = disk.Handoff.Notes.Concat(mine.Handoff.Notes)
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .TakeLast(12)
            .ToArray();
        return new(TeamStore.Normalize(disk.Folder), assignments, new(notes), limitations);
    }

    private void RememberLimitation(string sessionId, string chatId, int turnIndex, string reason, DateTimeOffset now)
    {
        if (Project.Limitations.Any(item => item.SessionId == sessionId && item.ChatId == chatId && item.TurnIndex == turnIndex && item.Reason == reason))
            return;
        Project = Project with { Limitations = [.. Project.Limitations, new(Guid.NewGuid().ToString("N"), sessionId, chatId, turnIndex, reason, now)] };
        _dirty = true;
    }

    public static string ResultRef(string chatId, int turnIndex, string sessionId, string? assignmentId = null) =>
        "chat/" + chatId + "/turn/" + turnIndex + "/session/" + sessionId
        + (assignmentId is null ? "" : "/assignment/" + assignmentId);

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];
}
