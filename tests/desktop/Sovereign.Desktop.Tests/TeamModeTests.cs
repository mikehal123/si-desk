using Sovereign.Desktop.Client;
using System.Text.Json;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class TeamModeTests : IDisposable
{
    private readonly string _profile = Path.Combine(Path.GetTempPath(), "Sovereign-team-" + Guid.NewGuid().ToString("N"));
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "Sovereign-team-project-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = new(2026, 10, 6, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Old_chat_loads_when_team_metadata_is_missing()
    {
        var chats = new ChatArchive(_profile);
        var saved = ChatArchive.New(4, _folder) with { Title = "Earlier chat", Draft = "keep", Turns = [new("question", "grok", "answer", "completed", [])] };
        chats.Save(saved);
        var path = Path.Combine(_profile, "chats", saved.Id + ".json");
        var json = File.ReadAllText(path).Replace(",\"EnableTeam\":null", "").Replace("\"EnableTeam\":null,", "");
        File.WriteAllText(path, json);
        Assert.DoesNotContain("EnableTeam", File.ReadAllText(path));
        var loaded = Assert.Single(new ChatArchive(_profile).Load());
        Assert.False(loaded.TeamEnabled);
        Assert.Equal("answer", loaded.Turns[0].Reply);
        Assert.Equal("keep", loaded.Draft);
        var team = new TeamStore(_profile).Load(_folder);
        Assert.Null(team.Warning);
        Assert.Empty(team.Project.Assignments);
        Assert.False(File.Exists(new TeamStore(_profile).PathToFile));
    }

    [Fact]
    public void Team_mode_off_leaves_the_outgoing_message_unchanged()
    {
        const string message = "Have the reviewer check the settings change too.";
        var project = SampleProject();
        Assert.Same(message, TeamPrompt.Compose(false, message, project));
        Assert.Equal(message, TeamPrompt.Compose(false, message, null));
        Assert.DoesNotContain("SOVEREIGN_TEAM", TeamPrompt.Compose(false, message, project));
    }

    [Fact]
    public void One_role_keeps_both_assignments_and_their_runtime_sessions()
    {
        var book = TeamBook.Open(new TeamStore(_profile), _folder);
        var chat = Guid.NewGuid().ToString("N");
        var first = book.AcceptSpawn("runtime-session-1", TaskFor("reviewer", "Check the first edit."), chat, 0, _now);
        book.ApplyRuntime("runtime-session-1", "completed", null, _now.AddMinutes(1));
        var second = book.AcceptSpawn("runtime-session-2", TaskFor("reviewer", "Check the settings change too."), chat, 1, _now.AddMinutes(2));
        book.ApplyRuntime("runtime-session-2", "completed", null, _now.AddMinutes(3));
        book.Save();
        var loaded = TeamBook.Open(new TeamStore(_profile), _folder);
        var reviewer = loaded.Project.Assignments.Where(item => item.RoleId == "reviewer").OrderBy(item => item.StartedAt).ToArray();
        Assert.Equal(2, reviewer.Length);
        Assert.Equal(first.Assignment!.Id, reviewer[0].Id);
        Assert.Equal("runtime-session-1", reviewer[0].SessionId);
        Assert.Equal(TeamBook.ResultRef(chat, 0, "runtime-session-1", reviewer[0].Id), reviewer[0].ResultRef);
        Assert.Equal(second.Assignment!.Id, reviewer[1].Id);
        Assert.Equal("runtime-session-2", reviewer[1].SessionId);
        Assert.NotEqual(reviewer[0].RoleId, reviewer[0].SessionId);
        Assert.Equal(TeamRoles.Reviewer.Instructions, reviewer[0].RoleInstructions);
        Assert.Equal("Check the first edit.", reviewer[0].Task);
        var board = TeamPrompt.BuildBoard(loaded.Project);
        var role = Assert.Single(board.Roles, item => item.RoleId == "reviewer");
        Assert.Equal("Completed", role.Status);
        Assert.Equal(reviewer[1].Id, role.Current!.Id);
        Assert.Equal(reviewer[0].Id, Assert.Single(role.Previous).Id);
        Assert.Equal("Idle", Assert.Single(board.Roles, item => item.RoleId == "investigator").Status);
        Assert.Null(Assert.Single(board.Roles, item => item.RoleId == "builder").Current);
    }

    [Fact]
    public void Completed_failed_and_interrupted_assignments_survive_reopen()
    {
        var store = new TeamStore(_profile);
        var book = TeamBook.Open(store, _folder);
        var chat = Guid.NewGuid().ToString("N");
        book.AcceptSpawn("session-done", TaskFor("investigator", "Find the setting."), chat, 0, _now);
        book.ApplyRuntime("session-done", "completed", null, _now.AddMinutes(1));
        book.AcceptSpawn("session-bad", TaskFor("builder", "Edit the setting."), chat, 1, _now.AddMinutes(2));
        book.ApplyRuntime("session-bad", "failed", "Tool failed.", _now.AddMinutes(3));
        book.AcceptSpawn("session-stop", TaskFor("reviewer", "Check the edit."), chat, 2, _now.AddMinutes(4));
        book.ApplyRuntime("session-stop", "stop requested", "Stop requested · completion unconfirmed", _now.AddMinutes(5));
        book.AcceptSpawn("session-live", TaskFor("reviewer", "Still going."), chat, 3, _now.AddMinutes(6));
        book.Save();
        var reopened = TeamBook.Open(new TeamStore(_profile), _folder);
        Assert.Equal("working", reopened.Project.Assignments.Single(item => item.SessionId == "session-live").State);
        Assert.Equal(1, reopened.ReconcileInactive(chat, new HashSet<string>(), "App restarted before a terminal helper event. Helpers are not resumed.", _now.AddMinutes(7)));
        Assert.Equal(0, reopened.ReconcileInactive(chat, new HashSet<string>(), "App restarted before a terminal helper event. Helpers are not resumed.", _now.AddMinutes(8)));
        reopened.Save();
        var loaded = TeamBook.Open(new TeamStore(_profile), _folder).Project.Assignments.ToDictionary(item => item.SessionId);
        Assert.Equal("completed", loaded["session-done"].State);
        Assert.Equal(TeamBook.ResultRef(chat, 0, "session-done", loaded["session-done"].Id), loaded["session-done"].ResultRef);
        Assert.Equal("failed", loaded["session-bad"].State);
        Assert.Equal("Tool failed.", loaded["session-bad"].Failure);
        Assert.Equal("interrupted", loaded["session-stop"].State);
        Assert.Equal("Stop requested · completion unconfirmed", loaded["session-stop"].Failure);
        Assert.Equal("interrupted", loaded["session-live"].State);
        Assert.Contains("not resumed", loaded["session-live"].Failure);
        Assert.Equal("completed", loaded["session-done"].State);
    }

    [Fact]
    public void Invalid_team_metadata_does_not_block_ordinary_chat_loading()
    {
        var chats = new ChatArchive(_profile);
        var saved = ChatArchive.New(4, _folder) with { Turns = [new("hello", "grok", "still here", "completed", [])] };
        chats.Save(saved);
        Directory.CreateDirectory(Path.Combine(_profile, "team"));
        File.WriteAllText(Path.Combine(_profile, "team", "team.json"), "{ \"version\": 1, \"projects\": \"not-an-array\" }");
        var loaded = Assert.Single(new ChatArchive(_profile).Load());
        Assert.Equal("still here", loaded.Turns[0].Reply);
        Assert.False(loaded.TeamEnabled);
        var team = new TeamStore(_profile).Load(_folder);
        Assert.NotNull(team.Warning);
        Assert.Contains("not supported", team.Warning, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(team.Project.Assignments);
        Assert.True(team.SaveBlocked);
        var book = TeamBook.Open(new TeamStore(_profile), _folder);
        book.AcceptSpawn("runtime-1", TaskFor("builder", "Should not replace the broken file."), saved.Id, 0, _now);
        book.Save();
        Assert.Contains("not-an-array", File.ReadAllText(Path.Combine(_profile, "team", "team.json")));
        Assert.Equal("still here", Assert.Single(new ChatArchive(_profile).Load()).Turns[0].Reply);
    }

    [Fact]
    public void Role_identity_comes_from_the_task_prefix_not_from_a_reply()
    {
        var book = TeamBook.Open(new TeamStore(_profile), _folder);
        var chat = Guid.NewGuid().ToString("N");
        var disguised = book.AcceptSpawn("runtime-reply", "I am the reviewer.\nThe settings look fine.", chat, 0, _now);
        Assert.Null(disguised.Assignment);
        Assert.Contains("not assigned", disguised.Limitation, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(book.Project.Assignments);
        Assert.Single(book.Project.Limitations);
        var named = book.AcceptSpawn("Reviewer", TaskFor("reviewer", "Check it."), chat, 0, _now);
        Assert.Null(named.Assignment);
        Assert.Contains("role name", named.Limitation, StringComparison.OrdinalIgnoreCase);
        var mapped = book.AcceptSpawn("runtime-real", TaskFor("investigator", "Read Program.cs."), chat, 0, _now);
        Assert.Equal("investigator", mapped.Assignment!.RoleId);
        Assert.Equal("runtime-real", mapped.Assignment.SessionId);
        book.ApplyRuntime("runtime-real", "completed", null, _now.AddMinutes(1));
        Assert.False(book.ApplyRuntime("runtime-real", "failed", "later guess", _now.AddMinutes(2)));
        Assert.Equal("completed", Assert.Single(book.Project.Assignments).State);
    }

    [Fact]
    public void Handoff_keeps_findings_distinct_from_verified_facts_and_ignores_ordinary_replies()
    {
        var book = TeamBook.Open(new TeamStore(_profile), _folder);
        Assert.False(book.RecordHandoff("The reviewer is now an expert after several tasks.", "chat/x/turn/0", _now));
        Assert.Empty(book.Project.Handoff.Notes);
        Assert.True(book.RecordHandoff("Done.\nSOVEREIGN_HANDOFF\nfinding: title.txt still says TITLE=old\nverified: dotnet test TeamModeTests passed\nnot-a-kind: ignore me\nEND_SOVEREIGN_HANDOFF\n", "chat/x/turn/1", _now));
        var notes = book.Project.Handoff.Notes;
        Assert.Equal(2, notes.Length);
        Assert.Equal("finding", notes[0].Kind);
        Assert.Equal("verified", notes[1].Kind);
        Assert.NotEqual(notes[0].Kind, notes[1].Kind);
        var prompt = TeamPrompt.Compose("Change the title.", book.Project);
        Assert.Contains("Finding (not verified): title.txt still says TITLE=old", prompt);
        Assert.Contains("Verified: dotnet test TeamModeTests passed", prompt);
        Assert.Contains("Read the current files before relying on them", prompt);
        Assert.DoesNotContain("expert", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CURRENT USER MESSAGE:\nChange the title.", prompt);
        Assert.Contains(TeamRoles.Builder.Instructions, prompt);
    }

    [Fact]
    public void A_later_save_does_not_drop_an_earlier_assignment()
    {
        var store = new TeamStore(_profile);
        var first = TeamBook.Open(store, _folder);
        var second = TeamBook.Open(new TeamStore(_profile), _folder);
        var chat = Guid.NewGuid().ToString("N");
        first.AcceptSpawn("runtime-a", TaskFor("builder", "First edit."), chat, 0, _now);
        first.Save();
        second.AcceptSpawn("runtime-b", TaskFor("reviewer", "Second check."), chat, 1, _now.AddMinutes(1));
        second.Save();
        var ids = TeamBook.Open(new TeamStore(_profile), _folder).Project.Assignments.Select(item => item.SessionId).OrderBy(item => item).ToArray();
        Assert.Equal(["runtime-a", "runtime-b"], ids);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_profile, "team"), "*.tmp"));
    }

    [Fact]
    public void Explicit_team_preference_roundtrips_and_stays_off_until_chosen()
    {
        var chats = new ChatArchive(_profile);
        var off = ChatArchive.New(4, _folder);
        var on = ChatArchive.New(4, _folder) with { EnableTeam = true, EnableSubagents = true, SubagentPreferenceVersion = 1 };
        chats.Save(off); chats.Save(on);
        var loaded = new ChatArchive(_profile).Load();
        Assert.False(loaded.Single(item => item.Id == off.Id).TeamEnabled);
        Assert.True(loaded.Single(item => item.Id == on.Id).TeamEnabled);
        Assert.True(loaded.Single(item => item.Id == on.Id).SubagentsEnabled);
    }

    [Fact]
    public void Damaged_team_metadata_does_not_throw_when_saving()
    {
        var chats = new ChatArchive(_profile);
        chats.Save(ChatArchive.New(4, _folder) with { Turns = [new("hello", "grok", "still here", "completed", [])] });
        var store = new TeamStore(_profile);
        var book = TeamBook.Open(store, _folder);
        var chat = Guid.NewGuid().ToString("N");
        book.AcceptSpawn("runtime-ok", TaskFor("builder", "First save."), chat, 0, _now);
        book.Save();
        var withoutFlag = File.ReadAllText(store.PathToFile).Replace(",\"Unconfirmed\":false", "")
            .Replace(",\"RuntimeStartedAt\":null", "").Replace(",\"ResultOutput\":\"\"", "")
            .Replace("/assignment/" + book.Project.Assignments.Single().Id, "");
        Assert.DoesNotContain("Unconfirmed", withoutFlag);
        File.WriteAllText(store.PathToFile, withoutFlag);
        var legacy = Assert.Single(TeamBook.Open(new TeamStore(_profile), _folder).Project.Assignments);
        Assert.False(legacy.Unconfirmed);
        Assert.Null(legacy.RuntimeStartedAt);
        Assert.Null(legacy.ResultOutput);
        Assert.Equal(TeamBook.ResultRef(chat, 0, "runtime-ok"), legacy.ResultRef);
        File.WriteAllText(store.PathToFile, "{ \"Version\": 1, \"Projects\": [");
        book.AcceptSpawn("runtime-later", TaskFor("reviewer", "Must not replace the damaged file."), chat, 1, _now.AddMinutes(1));
        var thrown = Record.Exception(() => book.Save());
        Assert.Null(thrown);
        Assert.True(book.SaveBlocked);
        Assert.Contains("left unchanged", book.Warning);
        Assert.Equal("{ \"Version\": 1, \"Projects\": [", File.ReadAllText(store.PathToFile));
        Assert.Equal("still here", Assert.Single(new ChatArchive(_profile).Load()).Turns[0].Reply);
    }

    [Fact]
    public void Same_session_follow_up_keeps_a_separate_assignment_and_the_earlier_result()
    {
        var book = TeamBook.Open(new TeamStore(_profile), _folder);
        var chat = Guid.NewGuid().ToString("N");
        const string session = "runtime-session-same";
        var first = book.AcceptSpawn(session, TaskFor("reviewer", "Check the first edit."), chat, 0, _now);
        var replay = book.AcceptSpawn(session, TaskFor("reviewer", "Check the first edit."), chat, 0, _now.AddSeconds(30));
        book.ApplyRuntime(session, "completed", null, _now.AddMinutes(1));
        var second = book.AcceptSpawn(session, TaskFor("reviewer", "Check the settings change too."), chat, 0, _now.AddMinutes(3));
        Assert.True(book.ApplyRuntime(session, "failed", "The follow-up found a defect.", _now.AddMinutes(4)));
        book.Save();
        var loaded = TeamBook.Open(new TeamStore(_profile), _folder).Project.Assignments.OrderBy(item => item.StartedAt).ToArray();
        Assert.Equal(2, loaded.Length);
        Assert.Equal(first.Assignment!.Id, replay.Assignment!.Id);
        Assert.Equal(first.Assignment.Id, loaded[0].Id);
        Assert.Equal("completed", loaded[0].State);
        Assert.Equal("Check the first edit.", loaded[0].Task);
        Assert.Equal(TeamBook.ResultRef(chat, 0, session, loaded[0].Id), loaded[0].ResultRef);
        Assert.Equal(second.Assignment!.Id, loaded[1].Id);
        Assert.Equal(session, loaded[1].SessionId);
        Assert.Equal("failed", loaded[1].State);
        Assert.Equal("The follow-up found a defect.", loaded[1].Failure);
        Assert.Equal("Check the settings change too.", loaded[1].Task);
    }

    [Fact]
    public void Late_completion_after_unconfirmed_disconnect_is_kept()
    {
        var book = TeamBook.Open(new TeamStore(_profile), _folder);
        var chat = Guid.NewGuid().ToString("N");
        var task = TaskFor("builder", "Edit the setting.");
        book.AcceptSpawn("runtime-late", task, chat, 0, _now, "running", _now);
        book.AcceptSpawn("runtime-late", task, chat, 0, _now.AddMinutes(1), "disconnected", _now);
        Assert.True(book.ApplyRuntime("runtime-late", "disconnected", "Parent turn ended · completion unconfirmed", _now.AddMinutes(1), runtimeStartedAt: _now, chatId: chat));
        Assert.Equal("interrupted", Assert.Single(book.Project.Assignments).State);
        Assert.True(Assert.Single(book.Project.Assignments).Unconfirmed);
        book.AcceptSpawn("runtime-late", task, chat, 0, _now.AddMinutes(2), "completed", _now);
        Assert.True(book.ApplyRuntime("runtime-late", "completed", null, _now.AddMinutes(2), "The setting was edited.", _now, chat));
        var completed = Assert.Single(book.Project.Assignments);
        Assert.Equal("completed", completed.State);
        Assert.False(completed.Unconfirmed);
        Assert.Null(completed.Failure);
        Assert.Equal("The setting was edited.", completed.ResultOutput);
        Assert.Equal(TeamBook.ResultRef(chat, 0, "runtime-late", completed.Id), completed.ResultRef);
        Assert.False(book.ApplyRuntime("runtime-late", "failed", "later guess", _now.AddMinutes(3)));
        Assert.Equal("completed", Assert.Single(book.Project.Assignments).State);

        book.AcceptSpawn("runtime-confirmed", TaskFor("reviewer", "Check it."), chat, 1, _now.AddMinutes(4));
        book.ApplyRuntime("runtime-confirmed", "cancelled", "Cancelled", _now.AddMinutes(5));
        Assert.False(book.ApplyRuntime("runtime-confirmed", "completed", null, _now.AddMinutes(6)));
        Assert.Equal("interrupted", book.Project.Assignments.Single(item => item.SessionId == "runtime-confirmed").State);
        Assert.False(book.Project.Assignments.Single(item => item.SessionId == "runtime-confirmed").Unconfirmed);
    }

    [Fact]
    public void Role_is_assigned_when_the_task_arrives_after_the_helper_starts()
    {
        var book = TeamBook.Open(new TeamStore(_profile), _folder);
        var chat = Guid.NewGuid().ToString("N");
        var early = book.AcceptSpawn("runtime-late-task", null, chat, 0, _now, "running", _now);
        Assert.Null(early.Assignment);
        Assert.Null(early.Limitation);
        Assert.Empty(book.Project.Assignments);
        Assert.Empty(book.Project.Limitations);
        var assigned = book.AcceptSpawn("runtime-late-task", TaskFor("investigator", "Read title.txt."), chat, 0, _now.AddMinutes(1), "running", _now);
        Assert.Equal("investigator", assigned.Assignment!.RoleId);
        Assert.Equal("Read title.txt.", assigned.Assignment.Task);
        Assert.Equal("runtime-late-task", assigned.Assignment.SessionId);
        Assert.Empty(book.Project.Limitations);
        book.ApplyRuntime("runtime-late-task", "running", null, _now.AddMinutes(1));
        Assert.Equal("working", Assert.Single(book.Project.Assignments).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Null_assignment_preserves_the_damaged_file_and_blocks_saves(bool damageAfterOpen)
    {
        var store = new TeamStore(_profile);
        var book = damageAfterOpen ? TeamBook.Open(store, _folder) : null;
        Directory.CreateDirectory(Path.GetDirectoryName(store.PathToFile)!);
        var damaged = JsonSerializer.Serialize(new {
            Version = 1, Projects = new[] { new TeamProject(_folder, [null!], new([]), []) }
        });
        File.WriteAllText(store.PathToFile, damaged);
        book ??= TeamBook.Open(store, _folder);
        book.AcceptSpawn("runtime-new", TaskFor("builder", "Keep chat working."), Guid.NewGuid().ToString("N"), 0, _now);
        Assert.Null(Record.Exception(book.Save));
        Assert.True(book.SaveBlocked);
        Assert.NotNull(book.Warning);
        Assert.Equal(damaged, File.ReadAllText(store.PathToFile));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(store.PathToFile)!, "*.tmp"));
    }

    [Fact]
    public void Repeated_terminal_snapshots_update_output_without_creating_assignments()
    {
        var book = TeamBook.Open(new TeamStore(_profile), _folder);
        var chat = Guid.NewGuid().ToString("N");
        var task = TaskFor("reviewer", "Inspect the edit.");
        Assert.Null(book.AcceptSpawn("never-started", task, chat, 0, _now, "completed", _now).Assignment);
        Assert.Empty(book.Project.Assignments);
        var first = book.AcceptSpawn("runtime-repeat", task, chat, 0, _now, "running", _now).Assignment!;
        Assert.Equal(first.Id, book.AcceptSpawn("runtime-repeat", task, chat, 0, _now.AddSeconds(1), "running", _now).Assignment!.Id);
        book.AcceptSpawn("runtime-repeat", task, chat, 0, _now.AddSeconds(2), "completed", _now);
        book.ApplyRuntime("runtime-repeat", "completed", null, _now.AddSeconds(2), "Initial result", _now, chat);
        Assert.Equal(first.Id, book.AcceptSpawn("runtime-repeat", task, chat, 0, _now.AddSeconds(3), "completed", _now).Assignment!.Id);
        Assert.True(book.ApplyRuntime("runtime-repeat", "completed", null, _now.AddSeconds(3), "Final result", _now, chat));
        Assert.Equal(first.Id, book.AcceptSpawn("runtime-repeat", task, chat, 0, _now.AddSeconds(4), "running", _now).Assignment!.Id);
        Assert.False(book.ApplyRuntime("runtime-repeat", "running", null, _now.AddSeconds(4), runtimeStartedAt: _now, chatId: chat));
        book.Save();
        var saved = Assert.Single(TeamBook.Open(new TeamStore(_profile), _folder).Project.Assignments);
        Assert.Equal("completed", saved.State);
        Assert.Equal("Final result", saved.ResultOutput);
    }

    [Fact]
    public void Reusing_the_same_task_keeps_each_runs_result_and_identity()
    {
        var book = TeamBook.Open(new TeamStore(_profile), _folder);
        var chat = Guid.NewGuid().ToString("N");
        var task = TaskFor("reviewer", "Inspect the edit.");
        const string session = "runtime-same-task";
        var first = book.AcceptSpawn(session, task, chat, 0, _now, "running", _now).Assignment!;
        book.ApplyRuntime(session, "completed", null, _now.AddMinutes(1), "First result", _now, chat);
        var nextStart = _now.AddMinutes(2);
        var second = book.AcceptSpawn(session, task, chat, 0, nextStart, "running", nextStart).Assignment!;
        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.ResultRef, second.ResultRef);
        Assert.Equal("", second.ResultOutput);
        book.ApplyRuntime(session, "failed", "The follow-up failed.", _now.AddMinutes(3), "Second result", nextStart, chat);
        book.AcceptSpawn(session, task, chat, 0, _now.AddMinutes(4), "completed", _now);
        book.ApplyRuntime(session, "completed", null, _now.AddMinutes(4), "First final result", _now, chat);
        book.Save();
        var saved = TeamBook.Open(new TeamStore(_profile), _folder).Project.Assignments;
        Assert.Equal(2, saved.Length);
        Assert.Equal("completed", saved.Single(item => item.Id == first.Id).State);
        Assert.Equal("First final result", saved.Single(item => item.Id == first.Id).ResultOutput);
        Assert.Equal("failed", saved.Single(item => item.Id == second.Id).State);
        Assert.Equal("Second result", saved.Single(item => item.Id == second.Id).ResultOutput);
    }

    [Fact]
    public void Late_confirmation_targets_the_disconnected_run_while_its_follow_up_is_working()
    {
        var book = TeamBook.Open(new TeamStore(_profile), _folder);
        var chat = Guid.NewGuid().ToString("N");
        var task = TaskFor("builder", "Edit the setting.");
        var first = book.AcceptSpawn("runtime-followup", task, chat, 0, _now, "running", _now).Assignment!;
        book.ApplyRuntime("runtime-followup", "disconnected", "Completion unconfirmed", _now.AddMinutes(1), runtimeStartedAt: _now, chatId: chat);
        var nextStart = _now.AddMinutes(2);
        var second = book.AcceptSpawn("runtime-followup", task, chat, 0, nextStart, "running", nextStart).Assignment!;
        book.AcceptSpawn("runtime-followup", task, chat, 0, _now.AddMinutes(3), "completed", _now);
        Assert.True(book.ApplyRuntime("runtime-followup", "completed", null, _now.AddMinutes(3), "Earlier edit finished", _now, chat));
        Assert.Equal("completed", book.Project.Assignments.Single(item => item.Id == first.Id).State);
        Assert.False(book.Project.Assignments.Single(item => item.Id == first.Id).Unconfirmed);
        Assert.Equal("working", book.Project.Assignments.Single(item => item.Id == second.Id).State);
        Assert.Equal("", book.Project.Assignments.Single(item => item.Id == second.Id).ResultOutput);
    }

    public void Dispose()
    {
        if (Directory.Exists(_profile)) Directory.Delete(_profile, true);
    }

    private static string TaskFor(string role, string task) => "SOVEREIGN_TEAM role=" + role + "\n" + task;

    private TeamProject SampleProject() => new(_folder, [], new([new(Guid.NewGuid().ToString("N"), "pitfall", "Check the saved flag.", "team/v1 chat/sample/turn/0")]), []);
}
