using System.Text.Json;
using Microsoft.UI.Xaml;
using Sovereign.Desktop.Client;

namespace Sovereign.Desktop;

internal sealed partial class ChatConversation
{
    private async Task<object> VerifyTeamWorkflowAsync(Func<string, Task> capture, string output)
    {
        var checks = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition) { SaveNativeSubagentObservations(output, "team assertion failed", message); throw new IOException(message); }
            checks.Add(message);
        }
        Check(_subagentRows.Count == 0, "Team verification starts from a fresh chat.");
        var fixture = Path.Combine(_profileDirectory, "team-project");
        Directory.CreateDirectory(fixture);
        var titlePath = Path.Combine(fixture, "title.txt");
        File.WriteAllText(titlePath, "TITLE=old\n");
        _restoring = true;
        _saved = ChatArchive.New(DeskChatConnections.CursorStored, fixture) with {
            ProjectMode = CursorProjectMode.Agent, Permissions = CursorPermissionPolicy.AlwaysAllow,
            EnableSubagents = true, SubagentPreferenceVersion = 1, EnableTeam = true
        };
        _projectFolder = fixture;
        _chatProvider.SelectedIndex = DeskChatConnections.Cursor;
        RestoreProjectBehavior();
        _teamBook = null;
        EnsureTeamBook();
        _restoring = false;
        UpdateTeamOption();
        await ConnectChatAsync();
        Check(_chatReady && _cursorChat is not null, "The Cursor connection is ready: " + _chatStatus.Text);
        var modelId = (await _cursorChat!.ModelsAsync(default)).FirstOrDefault(model => model.Id.StartsWith("grok-4.7[", StringComparison.Ordinal)
            && model.Id.Contains("fast=true", StringComparison.Ordinal))?.Id ?? throw new IOException("Grok 4.7 unavailable.");
        Check(SelectChatModel(modelId), "The parent uses the selected advertised routing ID.");
        Check(_teamOption.IsChecked == true && _teamOption.Visibility == Visibility.Visible, "Team mode is visibly opted in beside the helper controls.");
        _chatInput.Text = "Change title.txt so its only line is TITLE=blue. It currently says TITLE=old. "
            + "Delegate one helper at a time and wait for each result before the next: "
            + "investigator locates the line and reports the file path; "
            + "builder changes only that line to TITLE=blue; "
            + "reviewer reads the file and reports whether the line is exactly TITLE=blue. "
            + "Then your own reply must contain the exact final line.";
        ShowAgents(true); SetChangesOpen(true);
        RefreshTeamBoard();
        await capture("team-idle");
        var sending = SendChatAsync();
        var deadline = DateTimeOffset.UtcNow.AddMinutes(12);
        var sawWorking = false;
        var capturedWorking = false;
        while (!sending.IsCompleted && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(200);
            DrainSavedTurn();
            if (_subagentRows.Count > 0) sawWorking = true;
            if (sawWorking && !capturedWorking)
            {
                capturedWorking = true;
                ShowAgents(true); SetChangesOpen(true); RefreshTeamBoard();
                await capture("team-working");
            }
        }
        if (!sending.IsCompleted) StopChatResponse();
        await sending;
        await Task.Delay(200);
        DrainSavedTurn();
        Save();
        ShowAgents(true); SetChangesOpen(true); RefreshTeamBoard();
        await capture("team-complete");
        var finalLine = File.ReadAllText(titlePath).Trim();
        Check(finalLine == "TITLE=blue", "Builder left title.txt as TITLE=blue. Actual text: " + finalLine);
        Check(sawWorking, "At least one helper was actually running.");
        var firstBook = TeamBook.Open(new TeamStore(_profileDirectory), fixture);
        var firstAssignments = firstBook.Project.Assignments.Where(item => item.ChatId == Id).ToArray();
        foreach (var role in new[] { "investigator", "builder", "reviewer" })
            Check(firstAssignments.Any(item => item.RoleId == role), "A real assignment was stored for " + role + ". Stored: " + string.Join(", ", firstAssignments.Select(item => item.RoleId + ":" + item.SessionId + ":" + item.State))
                + " Limitations: " + string.Join(" | ", firstBook.Project.Limitations.Select(item => item.SessionId + " " + item.Reason)));
        Check(LastReply.Contains("TITLE=blue", StringComparison.Ordinal), "The parent returned the actual final line.");
        var firstReviewer = firstAssignments.Where(item => item.RoleId == "reviewer").OrderBy(item => item.StartedAt).First();
        _chatInput.Text = "Read title.txt once more and report its line.";
        var followUp = SendChatAsync();
        var steered = false;
        var steerDeadline = DateTimeOffset.UtcNow.AddMinutes(8);
        while (!followUp.IsCompleted && DateTimeOffset.UtcNow < steerDeadline)
        {
            await Task.Delay(200);
            DrainSavedTurn();
            if (!steered && _cursorSendLoop && (_subagentRows.Values.Any(SubagentActive) || _chatBusy))
            {
                steered = true;
                _chatInput.Text = "Have the reviewer check the settings change too.";
                await SendChatAsync();
                await capture("team-steering");
            }
        }
        if (!followUp.IsCompleted) StopChatResponse();
        await followUp;
        await Task.Delay(200);
        DrainSavedTurn();
        Save();
        if (!steered)
        {
            _chatInput.Text = "Have the reviewer check the settings change too.";
            await SendChatAsync();
            DrainSavedTurn();
            Save();
        }
        Check(steered, "The correction was accepted by the running Cursor session through the existing steering path.");
        Check(_saved.Turns.Any(turn => turn.Prompt.Contains("Have the reviewer check the settings change too.", StringComparison.Ordinal)),
            "The correction was sent through the existing message path.");
        var book = TeamBook.Open(new TeamStore(_profileDirectory), fixture);
        var assignments = book.Project.Assignments.Where(item => item.ChatId == Id).ToArray();
        Check(assignments.Count(item => item.RoleId == "reviewer") >= 2, "The reviewer role kept a later assignment. Count: " + assignments.Count(item => item.RoleId == "reviewer")
            + ". Limitations: " + string.Join(" | ", book.Project.Limitations.Select(item => item.Reason)));
        Check(assignments.All(item => item.SessionId != item.RoleId && item.SessionId != item.RoleName), "Runtime session ids were not replaced with role names.");
        var reviewer = assignments.Where(item => item.RoleId == "reviewer").OrderBy(item => item.StartedAt).ToArray();
        Check(reviewer[0].Id == firstReviewer.Id && reviewer[0].ResultRef == firstReviewer.ResultRef && reviewer[0].Id != reviewer[^1].Id,
            "The reviewer's first result reference survived the later assignment.");
        Check(!_teamBoard.Visibility.Equals(Visibility.Collapsed), "The sidebar is showing the Team roles.");
        foreach (var role in TeamRoles.BuiltIn)
            Check(_teamSections[role.Id].Heading.Text.StartsWith(role.Name + " · ", StringComparison.Ordinal), "Sidebar names " + role.Name + " from its stable role, not from a guessed reply.");
        var reference = new { chat_id = Id, folder = fixture, assignments, limitations = book.Project.Limitations, notes = book.Project.Handoff.Notes };
        File.WriteAllText(Path.Combine(output, "team-reference.json"), JsonSerializer.Serialize(reference));
        File.WriteAllText(Path.Combine(_profileDirectory, "team", "team-qa-reference.json"), JsonSerializer.Serialize(reference));

        _chatInput.Text = "Have the investigator read title.txt once and stop.";
        var stopping = SendChatAsync();
        var stopDeadline = DateTimeOffset.UtcNow.AddSeconds(75);
        while (!stopping.IsCompleted && DateTimeOffset.UtcNow < stopDeadline && !_subagentRows.Values.Any(SubagentActive))
        { await Task.Delay(200); DrainSavedTurn(); }
        StopChatResponse();
        await stopping;
        await Task.Delay(200);
        DrainSavedTurn();
        Save();
        var afterStop = TeamBook.Open(new TeamStore(_profileDirectory), fixture);
        Check(!_chatBusy, "Stop left the chat idle.");
        Check(afterStop.Project.Assignments.Where(item => item.ChatId == Id).All(item => item.State != "working"),
            "Stop did not leave a Team assignment working.");
        ShowAgents(true); RefreshTeamBoard();
        await capture("team-stopped");
        return new { mode = "team", chat_id = Id, process_id = Environment.ProcessId, profile = _profileDirectory, fixture, model = modelId,
            final_line = finalLine, assignment_count = assignments.Length, reviewer_assignments = reviewer.Length,
            limitations = book.Project.Limitations.Length, checks };
    }

    private async Task<object> VerifyTeamReopenAsync(Func<string, Task> capture, string output)
    {
        var checks = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition) { SaveNativeSubagentObservations(output, "team reopen failed", message); throw new IOException(message); }
            checks.Add(message);
        }
        var referencePath = Environment.GetEnvironmentVariable("SOVEREIGN_QA_TEAM_REFERENCE")
            ?? Path.Combine(_profileDirectory, "team", "team-qa-reference.json");
        Check(File.Exists(referencePath), "Team reopen has the saved reference.");
        using var reference = JsonDocument.Parse(File.ReadAllText(referencePath));
        var chatId = reference.RootElement.GetProperty("chat_id").GetString();
        Check(Id == chatId && _cursorChat is null && _codexChat is null && !_chatReady, "Reopen restored the same chat without connecting a provider.");
        EnsureTeamBook();
        var book = _teamBook ?? throw new IOException("Team metadata did not load.");
        Check(book.Project.Assignments.Any(item => item.RoleId == "reviewer" && item.State is "completed" or "failed" or "interrupted"),
            "A saved reviewer assignment is still present after restart.");
        Check(book.Project.Assignments.All(item => item.State != "working"), "Restart did not resume a working helper.");
        ShowAgents(true); SetChangesOpen(true); RefreshTeamBoard();
        await capture("team-reopened");
        Check(_teamSections["investigator"].Heading.Text.Length > 0 && _teamSections["builder"].Heading.Text.Length > 0
            && _teamSections["reviewer"].Heading.Text.Length > 0, "All three roles are visible after restart.");
        if (Environment.GetEnvironmentVariable("SOVEREIGN_QA_TEAM_UI_REGRESSION") == "1")
            VerifyTeamUiRegression(Check);
        return new { mode = "team-reopen", chat_id = Id, process_id = Environment.ProcessId, provider_calls = false, checks,
            assignments = book.Project.Assignments.Select(item => new { item.RoleId, item.SessionId, item.State, item.ResultRef }) };
    }

    private void VerifyTeamUiRegression(Action<bool, string> check)
    {
        // Controlled runtime events exercise the real row/mapping/save path without a provider.
        BeginSavedTurn("Controlled Team UI regression", "controlled", [], "Controlled Team UI regression");
        var now = DateTimeOffset.UtcNow;
        var session = "qa-team-reused-" + Guid.NewGuid().ToString("N");
        var child = new CursorSubagent(session, "Reviewer", "SOVEREIGN_TEAM role=reviewer\nCheck first change.", "running",
            StartedAt: now, UpdatedAt: now);
        void Observe(CursorSubagent snapshot) => ObserveSubagent(new CursorActivityEvent("subagent", Subagent: snapshot));
        Observe(child);
        child = child with { State = "completed", Output = "FIRST RESULT", CompletedAt = now.AddSeconds(1) };
        Observe(child); Observe(child);
        check(_teamBook!.Project.Assignments.Count(item => item.SessionId == session) == 1,
            "Repeated terminal snapshots do not invent another Team assignment.");
        child = child with { State = "running", Task = "SOVEREIGN_TEAM role=reviewer\nCheck second change.",
            Output = "", StartedAt = now.AddSeconds(2), CompletedAt = null };
        Observe(child);
        child = child with { State = "completed", Output = "SECOND RESULT", CompletedAt = now.AddSeconds(3) };
        Observe(child);
        var reused = _teamBook.Project.Assignments.Where(item => item.SessionId == session).ToArray();
        check(reused.Length == 2 && reused[0].ResultOutput == "FIRST RESULT" && reused[1].ResultOutput == "SECOND RESULT"
            && reused[0].ResultRef != reused[1].ResultRef, "Same-session follow-up preserves two distinct assignment results.");
        check(_subagentViews[session].Root.Parent == _teamSections["reviewer"].Live,
            "The current reused helper row stays under the current role assignment.");
        var retiredRoot = _subagentViews[session].Root;

        var delayed = "qa-team-delayed-" + Guid.NewGuid().ToString("N");
        child = new CursorSubagent(delayed, "Investigator", "", "running", StartedAt: now.AddSeconds(4));
        Observe(child);
        child = child with { Task = "SOVEREIGN_TEAM role=investigator\nRead the current file." };
        Observe(child);
        child = child with { State = "disconnected", CurrentWork = "Completion unconfirmed" };
        Observe(child);
        child = child with { State = "completed", Output = "ACTUAL LATE RESULT" };
        Observe(child);
        check(_teamBook.Project.Assignments.Single(item => item.SessionId == delayed) is { RoleId: "investigator", State: "completed", Unconfirmed: false },
            "Late task details map the role and genuine completion resolves the unconfirmed disconnect.");

        for (var index = 0; index < 65; index++)
        {
            child = new CursorSubagent("qa-team-retire-" + index + "-" + Guid.NewGuid().ToString("N"), "Reviewer",
                "SOVEREIGN_TEAM role=reviewer\nControlled retirement check.", "running", StartedAt: now.AddMinutes(index + 1));
            Observe(child);
            Observe(child with { State = "completed", Output = "Checked", CompletedAt = now.AddMinutes(index + 1).AddSeconds(1) });
        }
        check(retiredRoot.Parent is null && !_subagentViews.ContainsKey(session) && _subagentViews.Count == 64,
            "Retired Team helper rows detach from their actual role panel and the live row count stays bounded.");
        _savedTurnIndex = -1;
        Save();
    }
}
