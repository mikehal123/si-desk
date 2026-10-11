using Sovereign.Desktop.Client;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class ChatArchiveTests : IDisposable
{
    [Fact]
    public void Quiet_display_roundtrips_without_removing_provider_context()
    {
        var store = new ChatArchive(_profile);
        var chat = ChatArchive.New(4) with { Turns = [new("inspect", "grok", "Earlier progress. Final answer.", "completed", [],
            ProgressUpdates: ["Earlier progress."], DisplayReply: "Final answer.")] };
        store.Save(chat);
        var loaded = Assert.Single(store.Load());
        Assert.Equal("Final answer.", loaded.Turns[0].DisplayReply);
        Assert.Equal("Earlier progress.", Assert.Single(loaded.Turns[0].ProgressUpdates!));
        Assert.Contains("Earlier progress. Final answer.", ChatArchive.RestoreContext(loaded, "continue"));
        Assert.Null(new SavedChatTurn("old", "model", "original answer", "completed", []).DisplayReply);
    }
    private readonly string _profile = Path.Combine(Path.GetTempPath(), "Sovereign-chat-test-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public void Delete_removes_only_selected_transcript_and_survives_reopen()
    {
        var store = new ChatArchive(_profile);
        var deleted = ChatArchive.New() with { Draft = "delete me" };
        var kept = ChatArchive.New(4) with { Draft = "keep me", Permissions = CursorPermissionPolicy.AlwaysAllow };
        store.Save(deleted); store.Save(kept);
        var keptPath = Path.Combine(_profile, "chats", kept.Id + ".json");
        var before = File.ReadAllBytes(keptPath);
        store.Delete(deleted.Id); store.Delete(deleted.Id);
        Assert.False(File.Exists(Path.Combine(_profile, "chats", deleted.Id + ".json")));
        Assert.Equal(before, File.ReadAllBytes(keptPath));
        Assert.Equal(kept.Id, Assert.Single(new ChatArchive(_profile).Load()).Id);
        Assert.Throws<ArgumentException>(() => store.Delete("../other"));
        store.Delete(kept.Id);
        Assert.Empty(new ChatArchive(_profile).Load());
    }
    [Fact]
    public void Separate_conversations_roundtrip_provider_folder_draft_and_partial_activity()
    {
        var store = new ChatArchive(_profile);
        var a = ChatArchive.New(4, @"D:\Project A") with { Title = "Agent A", Draft = "unfinished draft", Model = "grok",
            Turns = [new("question A", "grok", "partial answer", "running", [new("reasoning", "visible reasoning")])] };
        var b = ChatArchive.New(1, @"D:\Project B") with { Title = "Agent B", Model = "local",
            Turns = [new("question B", "local", "answer B", "completed", [])] };
        store.Save(a); store.Save(b);
        var restored = new ChatArchive(_profile).Load();
        Assert.Equal(2, restored.Count);
        var first = Assert.Single(restored, x => x.Id == a.Id);
        Assert.Equal(4, first.Provider); Assert.Equal(a.Folder, first.Folder); Assert.Equal(a.Draft, first.Draft);
        Assert.Equal("interrupted", first.Turns[0].Outcome); Assert.Equal("partial answer", first.Turns[0].Reply);
        var activity = new CursorTurnActivity(); activity.Finish(first.Turns[0].Outcome);
        Assert.Equal("Interrupted · partial response", activity.Phase);
        Assert.Equal("visible reasoning", first.Turns[0].Activity[0].Text);
        Assert.Equal("answer B", restored.Single(x => x.Id == b.Id).Turns[0].Reply);
    }
    [Fact]
    public void Preferences_are_per_chat_and_new_chats_always_ask()
    {
        var store = new ChatArchive(_profile);
        var a = ChatArchive.New(4, "project") with { Permissions = CursorPermissionPolicy.AlwaysAllow };
        var b = ChatArchive.New(4, "project") with { ProjectMode = CursorProjectMode.Plan };
        store.Save(a); store.Save(b);
        var loaded = store.Load();
        Assert.Equal(CursorPermissionPolicy.AlwaysAllow, loaded.Single(x => x.Id == a.Id).Permissions);
        Assert.Equal(CursorProjectMode.Plan, loaded.Single(x => x.Id == b.Id).ProjectMode);
        Assert.Equal(CursorPermissionPolicy.AlwaysAsk, loaded.Single(x => x.Id == b.Id).Permissions);
        Assert.Equal(CursorPermissionPolicy.AlwaysAsk, ChatArchive.New(4, "project").Permissions);
    }

    [Fact]
    public void Legacy_chats_default_to_ask_and_unknown_preferences_do_not_grant_access()
    {
        var store = new ChatArchive(_profile); var a = ChatArchive.New(4, "project"); store.Save(a);
        var path = Path.Combine(_profile, "chats", a.Id + ".json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { a.Id, a.Title, a.Provider, a.Model,
            a.Folder, a.Draft, a.Updated, a.Turns }));
        var legacy = Assert.Single(store.Load());
        Assert.Equal(CursorPermissionPolicy.AlwaysAsk, legacy.Permissions);
        Assert.Null(legacy.PendingInstructions);
        store.Save(a with { ProjectMode = (CursorProjectMode)99, Permissions = CursorPermissionPolicy.AlwaysAllow });
        var restored = Assert.Single(store.Load());
        Assert.Equal(CursorProjectMode.Plan, restored.ProjectMode);
        Assert.Equal(CursorPermissionPolicy.AlwaysAsk, restored.Permissions);
        store.Save(a with { Permissions = (CursorPermissionPolicy)99 });
        Assert.Equal(CursorPermissionPolicy.AlwaysAsk, Assert.Single(store.Load()).Permissions);
    }

    [Fact]
    public void Redirected_turn_preserves_partial_text_edits_and_each_batched_instruction()
    {
        var store = new ChatArchive(_profile);
        var first = new SavedChatInstruction(Guid.NewGuid().ToString("N"), "Keep the title", "grok", [], MayHaveBeenSent: true);
        var second = new SavedChatInstruction(Guid.NewGuid().ToString("N"), "Keep the footer too", "grok", [], MayHaveBeenSent: true);
        var sentText = "Instruction 1:\nKeep the title\n\nInstruction 2:\nKeep the footer too";
        var turn = new SavedChatTurn("Keep the title\n\nKeep the footer too", "grok", "Partial response before correction", "redirected",
            [new("tool", Tool: new("tool-1", "Edit title", "edit", "completed", "{}", [], "title retained"))], SentText: sentText,
            Instructions: [first, second]);
        store.Save(ChatArchive.New(4) with { Turns = [turn] });
        var restored = Assert.Single(store.Load());
        Assert.Equal("redirected", restored.Turns[0].Outcome);
        Assert.Equal(turn.Reply, restored.Turns[0].Reply);
        Assert.Equal("completed", Assert.Single(restored.Turns[0].Activity).Tool!.Status);
        Assert.Equal(new[] { first.Text, second.Text }, restored.Turns[0].Instructions!.Select(i => i.Text));
        Assert.Equal(sentText, restored.Turns[0].SentText);
    }

    [Fact]
    public void Restart_retains_queued_and_uncertain_instructions_without_automatic_send()
    {
        var store = new ChatArchive(_profile);
        var uncertain = new SavedChatInstruction(Guid.NewGuid().ToString("N"), "An instruction already dispatched", "grok", [], MayHaveBeenSent: true);
        var queued = new SavedChatInstruction(Guid.NewGuid().ToString("N"), "An instruction never sent", "grok", []);
        var chat = ChatArchive.New(4) with { Draft = "later draft belongs to user", PendingInstructions = [uncertain, queued],
            Turns = [new(uncertain.Text, "grok", "partial", "running", [], Instructions: [uncertain])] };
        store.Save(chat);
        var restored = Assert.Single(store.Load());
        Assert.Equal("interrupted", restored.Turns[0].Outcome);
        Assert.Equal(chat.Draft, restored.Draft);
        Assert.True(restored.PendingInstructions![0].MayHaveBeenSent);
        Assert.False(restored.PendingInstructions[1].MayHaveBeenSent);
        Assert.Equal(uncertain.Id, Assert.Single(restored.Turns[0].Instructions!).Id);
        var queue = new ChatSteeringQueue(restored.PendingInstructions);
        Assert.Empty(queue.BeginBatch());
        Assert.False(queue.HasAutomaticPending);
        var context = ChatArchive.RestoreContext(restored, "current explicit message");
        Assert.DoesNotContain(queued.Text, context);
        Assert.Contains("CURRENT MESSAGE:\ncurrent explicit message", context);
    }

    [Fact]
    public void Invalid_or_duplicate_pending_identity_is_rejected_without_destroying_the_saved_file()
    {
        var store = new ChatArchive(_profile);
        var pending = new SavedChatInstruction(Guid.NewGuid().ToString("N"), "correction", "grok", []);
        var chat = ChatArchive.New(4) with { PendingInstructions = [pending, pending] };
        store.Save(chat);
        var warnings = new List<string>();
        Assert.Empty(store.Load(warnings.Add));
        Assert.Single(warnings);
        Assert.True(File.Exists(Path.Combine(_profile, "chats", chat.Id + ".json")));
        store.Save(chat with { PendingInstructions = [pending with { Id = "../outside" }] });
        Assert.Empty(store.Load());
    }

    [Fact]
    public void Save_replaces_only_target_and_corrupt_file_is_preserved_with_warning()
    {
        var store = new ChatArchive(_profile); var a = ChatArchive.New(); var b = ChatArchive.New(1);
        store.Save(a); store.Save(b); store.Save(a with { Draft = "updated" });
        var bad = Path.Combine(_profile, "chats", Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(bad, "broken"); var warnings = new List<string>();
        var loaded = store.Load(warnings.Add);
        Assert.Equal(2, loaded.Count); Assert.Single(warnings); Assert.Equal("broken", File.ReadAllText(bad));
        Assert.Equal("updated", loaded.Single(x => x.Id == a.Id).Draft);
        Assert.Empty(Directory.GetFiles(Path.Combine(_profile, "chats"), "*.tmp"));
    }
    [Fact]
    public void Reconnected_context_is_bounded_and_never_includes_activity_or_permissions()
    {
        var a = ChatArchive.New() with { Turns = [new("old question", "model", "old response", "completed",
            [new("tool", "secret tool input")])] };
        var prompt = ChatArchive.RestoreContext(a, "new question");
        Assert.Contains("old response", prompt); Assert.Contains("CURRENT MESSAGE:\nnew question", prompt);
        Assert.DoesNotContain("secret tool input", prompt); Assert.Contains("Do not repeat earlier actions", prompt);
        var huge = a with { Turns = [new(new string('x', 110000), "model", "oversized", "completed", [])] };
        Assert.Contains("omitted", ChatArchive.RestoreContext(huge, "new question"));
        Assert.True(ChatArchive.RestoreContext(huge, "new question").Length < 1000);
        Assert.Throws<ArgumentException>(() => new ChatArchive(_profile).Save(a with { Id = "../other" }));
    }
    public void Dispose() { if (Directory.Exists(_profile)) Directory.Delete(_profile, true); }
}
