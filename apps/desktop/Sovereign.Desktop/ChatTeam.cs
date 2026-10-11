using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sovereign.Desktop.Client;
using Sovereign.Desktop.Connector;
using static Sovereign.Desktop.MainWindow;

namespace Sovereign.Desktop;

internal sealed partial class ChatConversation
{
    private readonly CheckBox _teamOption = new() { Content = "Team", Visibility = Visibility.Collapsed };
    private bool _teamOptionHooked;
    private readonly StackPanel _teamBoard = new() { Spacing = DeskTheme.Space8, Visibility = Visibility.Collapsed };
    private readonly TextBlock _teamWarningNote = Note("");
    private readonly TextBlock _teamNotes = Note("");
    private readonly StackPanel _teamUnassigned = new() { Spacing = DeskTheme.Space4 };

    private bool TeamUiActive => false;

    private void EnsureTeamBook()
    {
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
    }

    private string WithTeamContext(string prompt) => prompt;

    private void TrackTeamHelper(CursorSubagent child)
    {
        if (!TeamUiActive) { LayoutHelperRow(child.SessionId); return; }
    }

    private void SyncTeamTerminal()
    {
        if (!TeamUiActive) return;
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
    }

    private void LayoutHelperRow(string sessionId)
    {
        if (!_subagentViews.TryGetValue(sessionId, out var view)) return;
        if (!TeamUiActive)
        {
            ReturnRowToList(view.Root);
            return;
        }
    }

    private void ReturnRowToList(FrameworkElement row)
    {
        if (_restoring || row.Parent == _subagentList) return;
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

    private Task<object> VerifyTeamWorkflowAsync(Func<string, Task> capture, string output)
    {
        _ = capture;
        _ = output;
        throw new IOException("Team mode is unhooked.");
    }

    private Task<object> VerifyTeamReopenAsync(Func<string, Task> capture, string output)
    {
        _ = capture;
        _ = output;
        throw new IOException("Team mode is unhooked.");
    }
}
