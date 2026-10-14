using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sovereign.Desktop.Client;
using Sovereign.Desktop.Connector;
using static Sovereign.Desktop.MainWindow;

namespace Sovereign.Desktop;

internal sealed partial class ChatConversation
{
    private readonly CheckBox _teamOption = new() { Content = "Team", Visibility = Visibility.Collapsed };
    private TeamStore? _teamStore;
    private TeamBook? _teamBook;
    private string? _teamWarning;
    private bool _teamOptionHooked;
    private readonly StackPanel _teamBoard = new() { Spacing = DeskTheme.Space8, Visibility = Visibility.Collapsed };
    private readonly TextBlock _teamWarningNote = Note("");
    private readonly TextBlock _teamNotes = Note("");
    private readonly StackPanel _teamUnassigned = new() { Spacing = DeskTheme.Space4 };
    private readonly Dictionary<string, TeamRoleSection> _teamSections = new();

    private bool TeamUiActive => false;

    private void EnsureTeamBook()
    {
        _teamStore ??= new TeamStore(_profileDirectory);
        if (_projectFolder is null) { _teamBook = null; _teamWarning = null; return; }
        var folder = TeamStore.Normalize(_projectFolder);
        if (_teamBook is not null && string.Equals(_teamBook.Folder, folder, StringComparison.OrdinalIgnoreCase)) return;
        _teamBook = TeamBook.Open(_teamStore, folder);
        _teamWarning = _teamBook.Warning;
    }

    private void ConfigureTeamOption()
    {
        if (_teamOptionHooked) return;
        _teamOptionHooked = true;
        AutomationName(_teamOption, "Team mode");
        ToolTipService.SetToolTip(_teamOption, "Opt in to Investigator, Builder, and Reviewer. They use the existing helpers and this chat's permission policy. A role name does not grant extra authority.");
        _teamOption.Click += (_, _) =>
        {
            if (_restoring || _chatBusy) { _teamOption.IsChecked = _saved.EnableTeam == true; return; }
            if (_teamOption.IsChecked == true && !_saved.SubagentsEnabled)
            {
                _teamOption.IsChecked = false;
                _chatStatus.Text = "Team mode uses the existing helpers. Turn on Allow subagents before Team mode.";
                return;
            }
            _saved = _saved with { EnableTeam = _teamOption.IsChecked == true };
            Save();
            RefreshTeamBoard();
        };
        EnsureTeamBook();
        if (_teamBoard.Children.Count == 0)
        {
            _teamWarningNote.TextWrapping = TextWrapping.Wrap;
            _teamNotes.TextWrapping = TextWrapping.Wrap;
            _teamBoard.Children.Add(_teamWarningNote);
            foreach (var role in TeamRoles.BuiltIn)
            {
                var section = new TeamRoleSection(role.Name);
                _teamSections.Add(role.Id, section);
                _teamBoard.Children.Add(section.Root);
            }
            _teamBoard.Children.Add(_teamUnassigned);
            _teamBoard.Children.Add(_teamNotes);
        }
    }

    private void UpdateTeamOption()
    {
        _teamOption.Visibility = Visibility.Collapsed;
        _teamOption.IsChecked = false;
        _teamBoard.Visibility = Visibility.Collapsed;
    }

    private void ApplyTeamWarning()
    {
        if (_teamWarning is null || _chatStatus.Text.Contains(_teamWarning, StringComparison.Ordinal)) return;
        _chatStatus.Text = "Team metadata needs attention: " + _teamWarning + " " + _chatStatus.Text;
    }

    private string WithTeamContext(string prompt) => prompt;

    private void TrackTeamHelper(CursorSubagent child)
    {
        if (!TeamUiActive) { LayoutHelperRow(child.SessionId); return; }
        EnsureTeamBook();
        if (_teamBook is null || _restoring) { LayoutHelperRow(child.SessionId); return; }
        var now = DateTimeOffset.UtcNow;
        _teamBook.AcceptSpawn(child.SessionId, child.Task, _saved.Id, _savedTurnIndex, now, child.State, child.StartedAt);
        _teamBook.ApplyRuntime(child.SessionId, child.State, child.Failure ?? child.CurrentWork, now, child.Output, child.StartedAt, _saved.Id);
        _teamBook.Save();
        LayoutHelperRow(child.SessionId);
        RefreshTeamBoard();
    }

    private void SyncTeamTerminal()
    {
        if (!TeamUiActive || _teamBook is null) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _subagentRows)
            _teamBook.ApplyRuntime(pair.Key, pair.Value.State, pair.Value.Failure ?? pair.Value.CurrentWork, now,
                pair.Value.Output, pair.Value.StartedAt, _saved.Id);
        var active = _subagentRows.Where(pair => SubagentActive(pair.Value)).Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        _teamBook.ReconcileInactive(_saved.Id, active,
            _restoring ? "App restarted before a terminal helper event. Helpers are not resumed."
                : "The helper was not running after the response ended. It was not resumed.", now);
        _teamBook.Save();
        RefreshTeamBoard();
    }

    private void RecordTeamHandoff(string reply)
    {
        if (_projectFolder is null || !_saved.SwarmEnabled) return;
        var board = SwarmSection.TryLoad(_saved.Id);
        var passed = board is not null && SwarmMemory.VerifierPassed(board);
        if (!SwarmMemory.AppendReply(_profileDirectory, _projectFolder, reply, "chat/" + _saved.Id + "/turn/" + _savedTurnIndex, passed)) return;
        RenderProjectMemory();
    }

    private void RefreshTeamBoard()
    {
        _teamOption.Visibility = Visibility.Collapsed;
        _teamBoard.Visibility = Visibility.Collapsed;
        if (!_teamOptionHooked) return;
        if (!TeamUiActive)
        {
            _teamBoard.Visibility = Visibility.Collapsed;
            if (!_restoring)
                foreach (var view in _subagentViews.Values) ReturnRowToList(view.Root);
            return;
        }
        _teamBoard.Visibility = Visibility.Visible;
        _subagentEmpty.Visibility = Visibility.Collapsed;
        if (_teamBook?.Warning is { } warning) _teamWarning = warning;
        var project = _teamBook?.Project ?? new(_projectFolder!, [], new([]), []);
        var board = TeamPrompt.BuildBoard(project, _teamWarning);
        var rowOwners = project.Assignments.Where(item => item.ChatId == _saved.Id)
            .GroupBy(item => item.SessionId).ToDictionary(group => group.Key, group => group.Last().Id, StringComparer.Ordinal);
        bool OwnsRow(TeamAssignment item) => rowOwners.TryGetValue(item.SessionId, out var id) && id == item.Id;
        _teamWarningNote.Text = board.Warning is null ? "" : board.Warning;
        _teamWarningNote.Visibility = board.Warning is null ? Visibility.Collapsed : Visibility.Visible;
        foreach (var role in board.Roles)
        {
            var section = _teamSections[role.RoleId];
            section.Heading.Text = role.Name + " · " + role.Status;
            section.Current.Text = role.Current is null
                ? "Idle. No helper is running for this role."
                : Describe(role.Current, current: true);
            section.Previous.Text = role.Previous.Length == 0 ? "" : "Previous results\n" + string.Join("\n\n", role.Previous.Select(item => Describe(item, current: false)));
            section.Previous.Visibility = role.Previous.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            AutomationName(section.Root, "Team role " + role.Name + ", " + role.Status);
            if (_restoring) continue;
            if (role.Current is { } current && OwnsRow(current)) PlaceRow(current.SessionId, section.Live);
            foreach (var previous in role.Previous.Where(OwnsRow)) PlaceRow(previous.SessionId, section.PreviousLive);
        }
        if (_restoring) return;
        _teamUnassigned.Children.Clear();
        var assignedSessions = board.Roles.SelectMany(role => role.Previous.Select(item => item.SessionId)
            .Concat(role.Current is null ? [] : [role.Current.SessionId])).ToHashSet(StringComparer.Ordinal);
        var unassigned = board.Unassigned.Where(item => !assignedSessions.Contains(item.SessionId)).ToArray();
        if (unassigned.Length > 0) _teamUnassigned.Children.Add(Note("Not assigned to a role"));
        foreach (var item in unassigned)
        {
            _teamUnassigned.Children.Add(Note("Session " + item.SessionId + " · " + item.Reason));
            PlaceRow(item.SessionId, _teamUnassigned);
        }
        _teamNotes.Text = board.Notes.Length == 0 ? "" : "Project notes\n" + string.Join("\n", board.Notes.Select(note => note.Kind + ": " + note.Text));
        _teamNotes.Visibility = board.Notes.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private string Describe(TeamAssignment assignment, bool current)
    {
        var saved = ResultPreview(assignment);
        var failure = string.IsNullOrWhiteSpace(assignment.Failure) ? "" : "\nReason: " + assignment.Failure;
        return (current ? "Current assignment " : "Assignment ") + assignment.Id
            + "\nSession " + assignment.SessionId
            + "\nState " + assignment.State
            + "\nTask " + assignment.Task
            + failure
            + "\n" + saved;
    }

    private string ResultPreview(TeamAssignment assignment)
    {
        var reference = "Saved result reference: " + assignment.ResultRef;
        if (assignment.ResultOutput is { } snapshot)
            return reference + (snapshot.Length == 0 ? "\nNo helper result is saved for this assignment yet."
                : "\nFrom this assignment:\n" + PreviewOutput(snapshot));
        var activity = assignment.ChatId == _saved.Id && assignment.TurnIndex >= 0 && assignment.TurnIndex < _saved.Turns.Length
            ? _saved.Turns[assignment.TurnIndex].Activity : [];
        var child = activity.Select(item => item.Subagent).LastOrDefault(item => item?.SessionId == assignment.SessionId);
        if (child is null || child.Output.Length == 0) return reference + "\nNo helper result is saved in this chat yet.";
        return reference + "\nFrom saved chat activity:\n" + PreviewOutput(child.Output);
    }

    private static string PreviewOutput(string output) => output.Length > 500 ? output[..500] + "…" : output;

    private void LayoutHelperRow(string sessionId)
    {
        if (!_subagentViews.TryGetValue(sessionId, out var view)) return;
        if (!TeamUiActive || _teamBook is null)
        {
            ReturnRowToList(view.Root);
            return;
        }
        RefreshTeamBoard();
    }

    private void PlaceRow(string? sessionId, Panel parent)
    {
        if (_restoring || sessionId is null || !_subagentViews.TryGetValue(sessionId, out var view)) return;
        if (view.Root.Parent == parent) return;
        try
        {
            Detach(view.Root);
            parent.Children.Add(view.Root);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Moving a helper row before the page is in the live tree raises 0x800F1000.
            // The role text above still names the assignment and its saved result.
        }
    }

    private void ReturnRowToList(FrameworkElement row)
    {
        if (_restoring || row.Parent == _subagentList || row.Parent == _oldSubagentRows) return;
        try
        {
            Detach(row);
            _subagentList.Children.Insert(Math.Min(SubagentCardInsertIndex, _subagentList.Children.Count), row);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
        }
    }

    private static void Detach(FrameworkElement element)
    {
        if (element.Parent is Panel panel) panel.Children.Remove(element);
    }

    private sealed class TeamRoleSection
    {
        internal readonly Border Root;
        internal readonly TextBlock Heading = new() { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        internal readonly TextBlock Current = Note("");
        internal readonly StackPanel Live = new() { Spacing = DeskTheme.Space4 };
        internal readonly TextBlock Previous = Note("");
        internal readonly StackPanel PreviousLive = new() { Spacing = DeskTheme.Space4 };
        internal TeamRoleSection(string name)
        {
            Heading.Text = name + " · Idle";
            Current.TextWrapping = TextWrapping.Wrap;
            Previous.TextWrapping = TextWrapping.Wrap;
            Root = new Border { Background = DeskTheme.Hover, CornerRadius = new CornerRadius(DeskTheme.Radius6), Padding = new Thickness(DeskTheme.Space8),
                Child = new StackPanel { Spacing = DeskTheme.Space4, Children = { Heading, Current, Live, Previous, PreviousLive } } };
        }
    }
}
