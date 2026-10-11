using System.Text.Json;
using Sovereign.Desktop.Client;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class ChatProjectCatalogTests : IDisposable
{
    [Fact]
    public void Folder_survives_reopen_and_legacy_projects_still_load()
    {
        var folder = Path.Combine(_profile, "workspace");
        Directory.CreateDirectory(folder);
        var project = new SavedChatProject(Guid.NewGuid().ToString("N"), "Workspace", Now, Folder: folder);
        var legacy = new SavedChatProject(Guid.NewGuid().ToString("N"), "Legacy", Now);
        var catalog = new ChatProjectCatalog(_profile);
        catalog.Save([project, legacy]);
        Assert.Equal(folder, catalog.Load().Single(item => item.Id == project.Id).Folder);
        Assert.Null(catalog.Load().Single(item => item.Id == legacy.Id).Folder);
        Assert.Throws<ArgumentException>(() => catalog.Save([project with { Folder = "relative-folder" }]));
        Assert.Equal(folder, catalog.Load().Single(item => item.Id == project.Id).Folder);
    }
    private readonly string _profile = Path.Combine(Path.GetTempPath(), "Sovereign-chat-projects-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TryNormalizeName_trims_and_rejects_empty_long_symbol_and_duplicate_names()
    {
        Assert.True(ChatProjectCatalog.TryNormalizeName("  North wind  ", [], null, out var trimmed, out var trimError));
        Assert.Equal("North wind", trimmed);
        Assert.Null(trimError);
        Assert.False(ChatProjectCatalog.TryNormalizeName(null, [], null, out var empty, out var emptyError));
        Assert.Equal("", empty);
        Assert.Equal("Enter a project name.", emptyError);
        Assert.False(ChatProjectCatalog.TryNormalizeName(" \t ", [], null, out empty, out emptyError));
        Assert.Equal("", empty);
        Assert.Equal("Enter a project name.", emptyError);
        Assert.False(ChatProjectCatalog.TryNormalizeName(new string('a', 81), [], null, out var longName, out var longError));
        Assert.Equal("", longName);
        Assert.Equal("Use 80 characters or fewer.", longError);
        Assert.True(ChatProjectCatalog.TryNormalizeName(new string('b', 80), [], null, out var max, out var maxError));
        Assert.Equal(80, max.Length);
        Assert.Null(maxError);
        foreach (var symbol in "\\/:*?\"<>|")
        {
            Assert.False(ChatProjectCatalog.TryNormalizeName("Plan" + symbol, [], null, out var rejected, out var symbolError));
            Assert.Equal("", rejected);
            Assert.Equal("Use a name without file symbols.", symbolError);
        }
        Assert.False(ChatProjectCatalog.TryNormalizeName("Plan\n2", [], null, out var control, out var controlError));
        Assert.Equal("", control);
        Assert.Equal("Use a name without file symbols.", controlError);
        var atlas = new SavedChatProject(Guid.NewGuid().ToString("N"), "Atlas", Now, true);
        var other = new SavedChatProject(Guid.NewGuid().ToString("N"), "Beacon", Now);
        Assert.False(ChatProjectCatalog.TryNormalizeName(" atlas ", [atlas, other], null, out var duplicate, out var duplicateError));
        Assert.Equal("", duplicate);
        Assert.Equal("A project with that name already exists.", duplicateError);
        Assert.True(ChatProjectCatalog.TryNormalizeName("Atlas", [atlas, other], atlas.Id, out var own, out var ownError));
        Assert.Equal("Atlas", own);
        Assert.Null(ownError);
        Assert.True(ChatProjectCatalog.TryRename(atlas, "Atlas", [atlas, other], out var renamed, out var renameError));
        Assert.Null(renameError);
        Assert.NotNull(renamed);
        Assert.Equal(atlas.Id, renamed.Id);
        Assert.Equal(atlas.Created, renamed.Created);
        Assert.True(renamed.Collapsed);
        Assert.Equal("Atlas", renamed.Name);
        Assert.False(ChatProjectCatalog.TryRename(atlas, "beacon", [atlas, other], out var blocked, out var blockedError));
        Assert.Null(blocked);
        Assert.Equal("A project with that name already exists.", blockedError);
    }

    [Fact]
    public void TryCreate_uses_trimmed_name_guid_identity_and_current_time()
    {
        Assert.True(ChatProjectCatalog.TryCreate("  Northwind  ", [], Now, out var project, out var error));
        Assert.Null(error);
        Assert.NotNull(project);
        Assert.Equal("Northwind", project.Name);
        Assert.Equal(Now, project.Created);
        Assert.False(project.Collapsed);
        Assert.True(Guid.TryParseExact(project.Id, "N", out _));
        Assert.False(ChatProjectCatalog.TryCreate("northwind", [project], Now, out var blocked, out var blockedError));
        Assert.Null(blocked);
        Assert.Equal("A project with that name already exists.", blockedError);
    }

    [Fact]
    public void Save_then_load_roundtrips_collapsed_and_orders_by_created_then_id()
    {
        var older = new SavedChatProject("dddddddddddddddddddddddddddddddd", "Older", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), true);
        var newer = new SavedChatProject("cccccccccccccccccccccccccccccccc", "Newer", new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        var when = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var tieB = new SavedChatProject("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Bee", when);
        var tieA = new SavedChatProject("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Aye", when);
        var catalog = new ChatProjectCatalog(_profile);
        catalog.Save([newer, tieB, older, tieA]);
        var path = Path.Combine(_profile, "chat-projects.json");
        var json = File.ReadAllText(path);
        Assert.Contains("\"Version\":1", json);
        Assert.Contains("\"Collapsed\":true", json);
        Assert.True(json.IndexOf(newer.Id, StringComparison.Ordinal) < json.IndexOf(tieB.Id, StringComparison.Ordinal));
        Assert.True(json.IndexOf(tieB.Id, StringComparison.Ordinal) < json.IndexOf(older.Id, StringComparison.Ordinal));
        Assert.True(json.IndexOf(older.Id, StringComparison.Ordinal) < json.IndexOf(tieA.Id, StringComparison.Ordinal));
        var loaded = new ChatProjectCatalog(_profile).Load();
        Assert.Equal([older.Id, tieA.Id, tieB.Id, newer.Id], loaded.Select(project => project.Id).ToArray());
        Assert.Equal("Older", loaded[0].Name);
        Assert.Equal(older.Created, loaded[0].Created);
        Assert.True(loaded[0].Collapsed);
        Assert.False(loaded[1].Collapsed);
        Assert.Equal(when, loaded[1].Created);
        Assert.Equal(newer.Created, loaded[3].Created);
    }

    [Fact]
    public void Corrupt_catalog_is_preserved_byte_for_byte_and_load_warns_empty()
    {
        var catalog = new ChatProjectCatalog(_profile);
        var path = Path.Combine(_profile, "chat-projects.json");
        Directory.CreateDirectory(_profile);
        File.WriteAllText(path, "{");
        var broken = File.ReadAllBytes(path);
        var warnings = new List<string>();
        Assert.Empty(catalog.Load(warnings.Add));
        Assert.NotEmpty(warnings);
        Assert.Equal(broken, File.ReadAllBytes(path));
        var version = """{"Version":2,"Projects":[{"Id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","Name":"Kept","Created":"2026-01-01T00:00:00+00:00"}]}""";
        File.WriteAllText(path, version);
        var versionBytes = File.ReadAllBytes(path);
        warnings.Clear();
        Assert.Empty(catalog.Load(warnings.Add));
        Assert.NotEmpty(warnings);
        Assert.Equal(versionBytes, File.ReadAllBytes(path));
        File.WriteAllText(path, """{"Version":1,"Projects":null}""");
        var missingProjects = File.ReadAllBytes(path);
        warnings.Clear();
        Assert.Empty(catalog.Load(warnings.Add));
        Assert.NotEmpty(warnings);
        Assert.Equal(missingProjects, File.ReadAllBytes(path));
    }

    [Fact]
    public void Missing_catalog_loads_empty_without_warning()
    {
        var warnings = new List<string>();
        Assert.Empty(new ChatProjectCatalog(_profile).Load(warnings.Add));
        Assert.Empty(warnings);
        Assert.False(File.Exists(Path.Combine(_profile, "chat-projects.json")));
    }

    [Fact]
    public void Catalog_file_is_profile_chat_projects_json_not_under_chats()
    {
        new ChatProjectCatalog(_profile).Save([new SavedChatProject(Guid.NewGuid().ToString("N"), "Field", Now)]);
        Assert.True(File.Exists(Path.Combine(_profile, "chat-projects.json")));
        Assert.False(Directory.Exists(Path.Combine(_profile, "chats")));
    }

    [Fact]
    public void ChatArchive_load_ignores_chat_projects_json()
    {
        new ChatProjectCatalog(_profile).Save([new SavedChatProject(Guid.NewGuid().ToString("N"), "Field", Now)]);
        var chat = ChatArchive.New(4) with { Title = "Real", Draft = "text", Turns = [new("prompt", "grok", "reply", "completed", [])] };
        new ChatArchive(_profile).Save(chat);
        var warnings = new List<string>();
        var loaded = new ChatArchive(_profile).Load(warnings.Add);
        Assert.Empty(warnings);
        Assert.Equal(chat.Id, Assert.Single(loaded).Id);
        Assert.True(File.Exists(Path.Combine(_profile, "chat-projects.json")));
        Assert.False(File.Exists(Path.Combine(_profile, "chats", "chat-projects.json")));
    }

    [Fact]
    public void Legacy_conversation_without_project_id_loads_and_file_bytes_stay()
    {
        var id = Guid.NewGuid().ToString("N");
        var updated = new DateTimeOffset(2026, 10, 7, 18, 0, 0, TimeSpan.Zero);
        var json = JsonSerializer.Serialize(new
        {
            Id = id, Title = "Legacy title", Provider = 4, Model = "grok", Folder = @"E:\Work\Folder", Draft = "unsent draft", Updated = updated,
            Turns = new[] { new { Prompt = "What happened?", Model = "grok", Reply = "It loaded.", Outcome = "completed", Activity = Array.Empty<CursorActivityEvent>() } }
        });
        Assert.DoesNotContain("ProjectId", json);
        var path = WriteChat(id, json);
        var before = File.ReadAllBytes(path);
        var warnings = new List<string>();
        var loaded = Assert.Single(new ChatArchive(_profile).Load(warnings.Add));
        Assert.Empty(warnings);
        Assert.Null(loaded.ProjectId);
        Assert.Equal("What happened?", loaded.Turns[0].Prompt);
        Assert.Equal("It loaded.", loaded.Turns[0].Reply);
        Assert.Equal("completed", loaded.Turns[0].Outcome);
        Assert.Equal("unsent draft", loaded.Draft);
        Assert.Equal("Legacy title", loaded.Title);
        Assert.Equal(@"E:\Work\Folder", loaded.Folder);
        Assert.Equal(4, loaded.Provider);
        Assert.Equal(updated, loaded.Updated);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Invalid_project_id_is_cleared_in_memory_without_rewriting_the_file()
    {
        var id = Guid.NewGuid().ToString("N");
        var updated = new DateTimeOffset(2026, 10, 7, 19, 0, 0, TimeSpan.Zero);
        var json = JsonSerializer.Serialize(new
        {
            Id = id, Title = "Filed badly", Provider = 1, Model = "grok", Folder = @"E:\Work\Folder", Draft = "keep draft", Updated = updated,
            ProjectId = "not-a-guid",
            Turns = new[] { new { Prompt = "Stay", Model = "grok", Reply = "Kept", Outcome = "completed", Activity = Array.Empty<CursorActivityEvent>() } }
        });
        var path = WriteChat(id, json);
        var before = File.ReadAllBytes(path);
        var warnings = new List<string>();
        var loaded = Assert.Single(new ChatArchive(_profile).Load(warnings.Add));
        Assert.Empty(warnings);
        Assert.Null(loaded.ProjectId);
        Assert.Equal("Stay", loaded.Turns[0].Prompt);
        Assert.Equal("Kept", loaded.Turns[0].Reply);
        Assert.Equal("completed", loaded.Turns[0].Outcome);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Valid_project_id_roundtrips_through_chat_archive()
    {
        Assert.Null(ChatArchive.New().ProjectId);
        var projectId = Guid.NewGuid().ToString("N").ToUpperInvariant();
        var updated = new DateTimeOffset(2026, 10, 7, 20, 0, 0, TimeSpan.Zero);
        var chat = ChatArchive.New(2, @"E:\Folder") with
        {
            Title = "Filed", Draft = "notes", Model = "grok", Updated = updated, ProjectId = projectId,
            Turns = [new("prompt", "grok", "reply", "completed", [])]
        };
        new ChatArchive(_profile).Save(chat);
        var loaded = Assert.Single(new ChatArchive(_profile).Load());
        Assert.Equal(projectId, loaded.ProjectId);
        Assert.Equal(chat.Title, loaded.Title);
        Assert.Equal(chat.Draft, loaded.Draft);
        Assert.Equal(updated, loaded.Updated);
        Assert.Equal("reply", loaded.Turns[0].Reply);
    }

    [Fact]
    public void Move_and_unfile_change_only_project_assignment()
    {
        var updated = new DateTimeOffset(2024, 4, 5, 6, 7, 8, TimeSpan.Zero);
        var chat = ChatArchive.New() with { Title = "Title", Draft = "Draft", Updated = updated, Turns = [new("prompt", "model", "reply", "completed", [])] };
        var projectId = Guid.NewGuid().ToString("N");
        var moved = ChatProjectAssignment.Move(chat, projectId);
        Assert.Equal(chat with { ProjectId = projectId }, moved);
        Assert.Equal(projectId, moved.ProjectId);
        Assert.Equal(updated, moved.Updated);
        Assert.Equal(chat.Draft, moved.Draft);
        Assert.Equal(chat.Title, moved.Title);
        Assert.Equal(chat.Turns[0].Prompt, moved.Turns[0].Prompt);
        var cleared = ChatProjectAssignment.Move(moved, null);
        Assert.Null(cleared.ProjectId);
        Assert.Equal(moved with { ProjectId = null }, cleared);
        Assert.Equal(updated, cleared.Updated);
        var otherId = Guid.NewGuid().ToString("N");
        var otherUpdated = updated.AddHours(1);
        var other = chat with { Id = Guid.NewGuid().ToString("N"), ProjectId = otherId, Updated = otherUpdated, Title = "Other" };
        var unfiled = ChatProjectAssignment.Unfile([moved, other], projectId);
        Assert.Equal(moved with { ProjectId = null }, unfiled[0]);
        Assert.Equal(updated, unfiled[0].Updated);
        Assert.Equal(other, unfiled[1]);
        Assert.Equal(otherUpdated, unfiled[1].Updated);
        Assert.Equal(otherId, unfiled[1].ProjectId);
        Assert.Throws<ArgumentException>(() => ChatProjectAssignment.Move(chat, "nope"));
        Assert.Throws<ArgumentException>(() => ChatProjectAssignment.Unfile([chat], "nope"));
    }

    [Fact]
    public void Save_refuses_a_non_guid_identity_without_leaving_a_temp_file()
    {
        var catalog = new ChatProjectCatalog(_profile);
        Assert.Throws<ArgumentException>(() => catalog.Save([new SavedChatProject("nope", "Field", Now)]));
        Assert.False(File.Exists(Path.Combine(_profile, "chat-projects.json")));
        Assert.False(Directory.Exists(_profile) && Directory.EnumerateFiles(_profile, "*.tmp", SearchOption.AllDirectories).Any());
        var kept = new SavedChatProject(Guid.NewGuid().ToString("N"), "Field", Now, true);
        catalog.Save([kept]);
        var path = Path.Combine(_profile, "chat-projects.json");
        var before = File.ReadAllBytes(path);
        Assert.Throws<ArgumentException>(() => catalog.Save([new SavedChatProject(Guid.NewGuid().ToString("N"), "a/b", Now)]));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(Directory.EnumerateFiles(_profile, "*.tmp", SearchOption.AllDirectories));
        var padded = Guid.NewGuid().ToString("N");
        catalog.Save([new SavedChatProject(padded, "  North wind  ", Now)]);
        Assert.Equal("North wind", Assert.Single(new ChatProjectCatalog(_profile).Load()).Name);
        var keptTrimmed = File.ReadAllBytes(Path.Combine(_profile, "chat-projects.json"));
        Assert.Throws<ArgumentException>(() => catalog.Save([
            new SavedChatProject(padded, "North wind", Now),
            new SavedChatProject(Guid.NewGuid().ToString("N"), "  north wind  ", Now)]));
        Assert.Equal(keptTrimmed, File.ReadAllBytes(Path.Combine(_profile, "chat-projects.json")));
    }

    [Fact]
    public void Load_skips_invalid_and_duplicate_projects_without_rewriting()
    {
        var path = Path.Combine(_profile, "chat-projects.json");
        Directory.CreateDirectory(_profile);
        File.WriteAllText(path, """
            {"Version":1,"Projects":[
            {"Id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","Name":"Alpha","Created":"2026-05-01T00:00:00+00:00","Collapsed":true},
            {"Id":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","Name":"alpha","Created":"2026-01-01T00:00:00+00:00"},
            {"Id":"not-a-guid","Name":"Beta","Created":"2026-02-01T00:00:00+00:00"},
            {"Id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","Name":"Gamma","Created":"2026-03-01T00:00:00+00:00"},
            {"Id":"cccccccccccccccccccccccccccccccc","Name":"   ","Created":"2026-04-01T00:00:00+00:00"},
            {"Id":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","Name":"Plain","Created":"2026-04-15T00:00:00+00:00"}]}
            """);
        var before = File.ReadAllBytes(path);
        var warnings = new List<string>();
        var loaded = new ChatProjectCatalog(_profile).Load(warnings.Add);
        Assert.Equal(["eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"], loaded.Select(project => project.Id).ToArray());
        Assert.False(loaded[0].Collapsed);
        Assert.Equal("Plain", loaded[0].Name);
        Assert.True(loaded[1].Collapsed);
        Assert.Equal("Alpha", loaded[1].Name);
        Assert.Equal(4, warnings.Count);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    private string WriteChat(string id, string json)
    {
        var directory = Path.Combine(_profile, "chats");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, id + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    public void Dispose() { if (Directory.Exists(_profile)) Directory.Delete(_profile, true); }
}
