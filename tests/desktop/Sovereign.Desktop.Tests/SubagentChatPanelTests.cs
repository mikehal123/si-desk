using System.Text.Json;
using Sovereign.Desktop.Client;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class SubagentChatPanelTests : IDisposable
{
    private readonly string _profile = Path.Combine(Path.GetTempPath(), "Sovereign-subagent-panels-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Breadcrumb_shows_the_main_chat_then_each_nested_helper()
    {
        var names = new Dictionary<string, string>
        {
            ["rewrite"] = "Rewrite tx settings providers and keep the long title intact",
            ["rewrite/inspect"] = "Inspect chat layout gaps"
        };
        var crumbs = SubagentChatPanels.Breadcrumb("rewrite/inspect", names);
        Assert.Null(crumbs[0].Id);
        Assert.Equal("rewrite", crumbs[1].Id);
        Assert.Equal("rewrite/inspect", crumbs[2].Id);
        Assert.Equal("Rewrite tx settings providers and keep the long title intact", crumbs[1].Label);
        Assert.Equal("Inspect chat layout gaps", crumbs[2].Label);

        var parent = SubagentChatPanels.Breadcrumb(crumbs[1].Id, names);
        Assert.Null(parent[0].Id);
        Assert.Equal("rewrite", parent[1].Id);
        Assert.Equal("Root", SubagentChatPanels.Breadcrumb(null, names).Single().Label);
        Assert.Equal("Root", SubagentChatPanels.Breadcrumb("missing", names).Single().Label);
    }

    [Fact]
    public void Setting_defaults_on_when_the_stored_preference_is_missing()
    {
        Assert.True(SubagentChatPanels.Enabled(null));
        Assert.False(SubagentChatPanels.Enabled(false));
        Assert.True(SubagentChatPanels.Enabled(true));
    }

    [Fact]
    public void Open_appends_moves_existing_to_the_end_and_a_seventeenth_drops_the_oldest()
    {
        var open = SubagentChatPanels.Open(null, "helper-a");
        open = SubagentChatPanels.Open(open, "helper-b");
        Assert.Equal(["helper-a", "helper-b"], open);
        open = SubagentChatPanels.Open(open, "helper-a");
        Assert.Equal(["helper-b", "helper-a"], open);
        var closed = SubagentChatPanels.Close(open, "helper-b");
        Assert.Equal(["helper-a"], closed);
        Assert.Equal(["helper-a"], SubagentChatPanels.Close(closed, "missing"));

        string[]? panels = null;
        for (var i = 0; i < 17; i++) panels = SubagentChatPanels.Open(panels, "id-" + i);
        Assert.Equal(16, panels!.Length);
        Assert.Equal(Enumerable.Range(1, 16).Select(i => "id-" + i).ToArray(), panels);
    }

    [Fact]
    public void Open_and_close_normalize_ids_and_invalid_opens_do_not_evict_a_helper()
    {
        var open = Enumerable.Range(0, SubagentChatPanels.MaxOpen).Select(i => "id-" + i).ToArray();
        var original = open.ToArray();
        foreach (var invalid in new[] { null, "", " \t ", "bad\nid", "bad\0id", new string('x', 201) })
            Assert.Equal(original, SubagentChatPanels.Open(open, invalid!));

        Assert.Equal([.. open.Skip(1), "id-0"], SubagentChatPanels.Open(open, " id-0 "));
        Assert.Equal(open.Skip(1).ToArray(), SubagentChatPanels.Close(open, " id-0 "));
        Assert.Equal(original, open);
    }

    [Fact]
    public void Sanitize_ids_preserves_ordinal_identity_and_enforces_the_length_boundary()
    {
        var longest = new string('x', 200);
        Assert.Equal(["helper", "Helper", longest], SubagentChatPanels.SanitizeIds(
            [null!, " helper ", "helper", "Helper", "", "bad\0id", new string('x', 201), longest])!);
        Assert.Equal([longest], SubagentChatPanels.Open(null, " " + longest + " "));
        Assert.Null(SubagentChatPanels.SanitizeIds(null));
        Assert.Null(SubagentChatPanels.SanitizeIds(["", " ", "bad\nid", new string('x', 201)]));
    }

    [Fact]
    public void Reported_prompt_includes_only_real_helper_output_and_failure()
    {
        var helpers = new[]
        {
            new CursorSubagent("empty", "Quiet", "Look", "completed"),
            new CursorSubagent("done", "Reader", "Read the file", "completed", "finished the file"),
            new CursorSubagent("bad", "Checker", "Check it", "failed", Failure: "the tool was refused")
        };
        var report = SubagentChatPanels.ReportedForPrompt(helpers);
        Assert.DoesNotContain("Quiet", report, StringComparison.Ordinal);
        Assert.Contains("Helper \"Reader\" state completed.", report, StringComparison.Ordinal);
        Assert.Contains("finished the file", report, StringComparison.Ordinal);
        Assert.Contains("Reported failure:\nthe tool was refused", report, StringComparison.Ordinal);
        Assert.Contains("Do not invent helper results", report, StringComparison.Ordinal);
        var huge = new CursorSubagent("long", "Writer", "Write", "completed", new string('x', 6001));
        Assert.Contains("[Reported result truncated]", SubagentChatPanels.ReportedForPrompt([huge]), StringComparison.Ordinal);
        Assert.Equal("", SubagentChatPanels.ReportedForPrompt([]));
    }

    [Fact]
    public void Arrange_shows_the_latest_helper_and_keeps_the_others_on_tabs()
    {
        var one = SubagentChatPanels.Arrange(["only"]);
        Assert.Equal(["only"], one.Visible);
        Assert.Empty(one.Tabs);

        var two = SubagentChatPanels.Arrange(["first", "second"]);
        Assert.Equal(["second"], two.Visible);
        Assert.Equal(["first", "second"], two.Tabs);

        var three = SubagentChatPanels.Arrange(["oldest", "middle", "newest"]);
        Assert.Equal(["newest"], three.Visible);
        Assert.Equal(["oldest", "middle", "newest"], three.Tabs);
    }

    [Fact]
    public void Chat_archive_roundtrip_keeps_an_explicit_off_and_defaults_omitted_fields_on()
    {
        var archive = new ChatArchive(_profile);
        var panelsOff = ChatArchive.New() with { OpenSubagentChatPanels = false, OpenSubagentPanelIds = ["helper-a", "helper-b"] };
        var omitted = ChatArchive.New();
        archive.Save(panelsOff);
        // An older archive actually omits the new fields instead of serializing them as null.
        File.WriteAllBytes(Path.Combine(_profile, "chats", omitted.Id + ".json"), JsonSerializer.SerializeToUtf8Bytes(new
        { omitted.Id, omitted.Title, omitted.Provider, omitted.Model, omitted.Folder, omitted.Draft, omitted.Updated, omitted.Turns }));
        var loaded = archive.Load().ToDictionary(chat => chat.Id);
        Assert.False(loaded[panelsOff.Id].SubagentChatPanelsEnabled);
        Assert.Equal(["helper-a", "helper-b"], loaded[panelsOff.Id].OpenSubagentPanelIds!);
        Assert.True(loaded[omitted.Id].SubagentChatPanelsEnabled);
        Assert.Null(loaded[omitted.Id].OpenSubagentPanelIds);
    }

    [Fact]
    public void Load_sanitizes_bad_panel_ids_without_rejecting_the_conversation()
    {
        var warnings = new List<string>();
        var chat = ChatArchive.New() with
        {
            OpenSubagentChatPanels = false,
            OpenSubagentPanelIds = [null!, " ok ", "", "ok", "bad\nid", new string('x', 201),
                .. Enumerable.Range(0, 20).Select(i => "helper-" + i)]
        };
        new ChatArchive(_profile).Save(chat);
        var path = Path.Combine(_profile, "chats", chat.Id + ".json");
        var saved = File.ReadAllBytes(path);
        var loaded = Assert.Single(new ChatArchive(_profile).Load(warnings.Add));
        Assert.Equal(["ok", .. Enumerable.Range(0, 15).Select(i => "helper-" + i)], loaded.OpenSubagentPanelIds!);
        Assert.False(loaded.SubagentChatPanelsEnabled);
        Assert.Equal(saved, File.ReadAllBytes(path));
        Assert.Empty(warnings);
    }

    [Fact]
    public void Diff_emits_full_text_then_appends_and_marks_terminal_outcomes()
    {
        var tool = new CursorToolActivity("tool-1", "Read", "read", "completed", "", []);
        var running = new CursorSubagent("child", "Helper", "Look", "running", "Answer", Reasoning: "Think", Tool: tool);
        var first = SubagentChatPanels.Diff(null, running);
        Assert.Equal("Think", first.ReasoningAppend);
        Assert.Equal("Answer", first.AnswerAppend);
        Assert.False(first.Terminal);
        Assert.Single(first.Tools);

        var appended = SubagentChatPanels.Diff(running, running with { Output = "Answer more", Reasoning = "Think further" });
        Assert.Equal(" more", appended.AnswerAppend);
        Assert.Equal(" further", appended.ReasoningAppend);
        Assert.False(appended.Reset);

        Assert.True(SubagentChatPanels.Diff(running, running with { Reasoning = "Else" }).Reset);

        var completed = SubagentChatPanels.Diff(running, running with { State = "completed" });
        Assert.True(completed.Terminal);
        Assert.Equal("completed", completed.Outcome);
        Assert.Equal("failed", SubagentChatPanels.Diff(running, running with { State = "failed" }).Outcome);
        Assert.Equal("stopped", SubagentChatPanels.Diff(running, running with { State = "cancelled" }).Outcome);
    }

    [Fact]
    public void Saving_an_untouched_legacy_chat_does_not_add_default_panel_metadata()
    {
        var archive = new ChatArchive(_profile);
        var chat = ChatArchive.New();
        archive.Save(chat);
        var json = File.ReadAllText(Path.Combine(_profile, "chats", chat.Id + ".json"));
        Assert.DoesNotContain("OpenSubagentChatPanels", json);
        Assert.DoesNotContain("OpenSubagentPanelIds", json);
        Assert.DoesNotContain("SubagentChatPanelsEnabled", json);
        archive.Save(chat with { OpenSubagentChatPanels = false, OpenSubagentPanelIds = ["helper"] });
        var loaded = Assert.Single(archive.Load());
        Assert.False(loaded.SubagentChatPanelsEnabled);
        Assert.Equal(["helper"], loaded.OpenSubagentPanelIds!);
    }

    [Fact]
    public void Diff_keeps_the_current_tool_snapshot_during_incremental_updates()
    {
        var read = new CursorToolActivity("read", "Read", "read", "in_progress", "input", []);
        var previous = new CursorSubagent("child", "Helper", "Look", "running", Tools: [read]);
        var finishedRead = read with { Status = "completed", Output = "result", Diagnostics = "detail" };
        var edit = new CursorToolActivity("edit", "Edit", "edit", "pending", "", []);
        var next = previous with { Output = "Answer", Tools = [finishedRead, edit],
            Changes = [new("a.cs", null, ""), new("a.cs", null, ""), new(" ", null, "")] };
        var patch = SubagentChatPanels.Diff(previous, next);

        Assert.False(patch.Reset);
        Assert.Equal("Answer", patch.AnswerAppend);
        Assert.Equal([finishedRead, edit], patch.Tools);
        Assert.Equal(["a.cs"], patch.ChangePaths);
        var unchanged = SubagentChatPanels.Diff(next, next);
        Assert.False(unchanged.Reset);
        Assert.Null(unchanged.ReasoningAppend);
        Assert.Null(unchanged.AnswerAppend);
        Assert.Equal([finishedRead, edit], unchanged.Tools);
    }

    [Fact]
    public void Diff_resets_for_replaced_or_shortened_text_and_changed_tasks()
    {
        var previous = new CursorSubagent("child", "Helper", "Look", "running", "Answer", Reasoning: "Think");
        foreach (var next in new[] { previous with { Output = "An" }, previous with { Reasoning = "Else" },
            previous with { Task = "New task" } })
        {
            var patch = SubagentChatPanels.Diff(previous, next);
            Assert.True(patch.Reset);
            Assert.Equal(next.Output, patch.AnswerAppend);
            Assert.Equal(next.Reasoning, patch.ReasoningAppend);
        }
    }

    [Fact]
    public void Diff_reconstructs_a_disconnected_helper_when_the_real_final_result_arrives()
    {
        var read = new CursorToolActivity("read", "Read", "read", "unconfirmed", "", []);
        var previous = new CursorSubagent("child", "Helper", "Look", "disconnected", "Answer",
            Reasoning: "Think", Tools: [read]);
        var finishedRead = read with { Status = "completed", Output = "result" };
        var next = previous with { State = "completed", Output = "Answer more", Tools = [finishedRead] };
        var patch = SubagentChatPanels.Diff(previous, next);

        Assert.True(patch.Reset);
        Assert.Equal("Answer more", patch.AnswerAppend);
        Assert.Equal("Think", patch.ReasoningAppend);
        Assert.Equal([finishedRead], patch.Tools);
        Assert.True(patch.Terminal);
        Assert.Equal("completed", patch.Outcome);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("disconnected")]
    public void Diff_reconstructs_a_same_session_resume_even_when_the_previous_text_was_empty(string state)
    {
        var previous = new CursorSubagent("child", "Helper", "Look", state);
        var patch = SubagentChatPanels.Diff(previous, previous with { State = "running" });
        Assert.True(patch.Reset);
        Assert.False(patch.Terminal);
        Assert.Null(patch.ReasoningAppend);
        Assert.Null(patch.AnswerAppend);

        previous = previous with { Output = "Answer", Reasoning = "Think" };
        patch = SubagentChatPanels.Diff(previous, previous with
            { State = "running", Output = "Answer more", Reasoning = "Think more" });
        Assert.True(patch.Reset);
        Assert.Equal("Answer more", patch.AnswerAppend);
        Assert.Equal("Think more", patch.ReasoningAppend);
    }

    [Fact]
    public void Diff_reconstructs_late_terminal_text_and_tool_corrections_but_not_equivalent_snapshots()
    {
        var change = new CursorFileChange("a.cs", "before", "after");
        var read = new CursorToolActivity("read", "Read", "read", "completed", "", [change]);
        var previous = new CursorSubagent("child", "Helper", "Look", "completed", "Answer",
            Reasoning: "Think", Tool: read);
        foreach (var next in new[] { previous with { Output = "Answer more" },
            previous with { Reasoning = "Think more" }, previous with { Tool = read with { Diagnostics = "detail" } },
            previous with { Tool = read with { Changes = [change with { After = "corrected" }] } } })
        {
            var patch = SubagentChatPanels.Diff(previous, next);
            Assert.True(patch.Reset);
            Assert.Equal(next.Output, patch.AnswerAppend);
            Assert.Equal(next.Reasoning, patch.ReasoningAppend);
            Assert.Equal([next.Tool!], patch.Tools);
        }

        var equivalent = previous with { Tools = [read with { Changes = [change with { }] }], UpdatedAt = DateTimeOffset.UtcNow };
        var unchanged = SubagentChatPanels.Diff(previous, equivalent);
        Assert.False(unchanged.Reset);
        Assert.Null(unchanged.AnswerAppend);
        Assert.Null(unchanged.ReasoningAppend);
        Assert.True(unchanged.Terminal);
    }

    [Fact]
    public void Follow_up_stays_off_and_stop_is_only_for_a_writable_running_helper()
    {
        Assert.False(SubagentChatPanels.FollowUpEnabled);
        Assert.False(SubagentChatPanels.StopAvailable(readOnly: true, "running"));
        Assert.True(SubagentChatPanels.StopAvailable(false, "running"));
        Assert.False(SubagentChatPanels.StopAvailable(false, "completed"));
    }

    [Theory]
    [InlineData("completed", "Finished", "completed", false)]
    [InlineData("failed", "Failed", "failed", false)]
    [InlineData("cancelled", "Stopped", "stopped", false)]
    [InlineData("stopped", "Stopped", "stopped", false)]
    [InlineData("stop requested", "Stop requested", "interrupted", false)]
    [InlineData("disconnected", "Interrupted", "interrupted", false)]
    [InlineData("interrupted", "Interrupted", "interrupted", false)]
    [InlineData("unconfirmed", "Interrupted", "interrupted", false)]
    [InlineData("blocked", "Blocked", "interrupted", false)]
    [InlineData("unknown", "Status unknown", "interrupted", false)]
    [InlineData(null, "Status unknown", "interrupted", false)]
    [InlineData("running", "Working", "interrupted", true)]
    [InlineData("pending", "Working", "interrupted", true)]
    [InlineData("queued", "Working", "interrupted", true)]
    [InlineData("waiting", "Working", "interrupted", true)]
    public void Status_and_activity_outcomes_keep_unconfirmed_endings_honest(string? state, string text, string outcome, bool active)
    {
        Assert.Equal(text, SubagentChatPanels.StatusText(state));
        Assert.Equal(active, SubagentChatPanels.Running(state));
        Assert.Equal(active, SubagentChatPanels.StopAvailable(false, state));
        var patch = SubagentChatPanels.Diff(null, new CursorSubagent("child", "Helper", "Look", state ?? ""));
        Assert.Equal(!active, patch.Terminal);
        Assert.Equal(outcome, patch.Outcome);
    }

    public void Dispose() { if (Directory.Exists(_profile)) Directory.Delete(_profile, true); }
}
