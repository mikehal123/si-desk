using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sovereign.Desktop.Client;
using static Sovereign.Desktop.MainWindow;

namespace Sovereign.Desktop;

internal sealed partial class ChatConversation
{
    private readonly Button _closedAgents = new() { Content = "Closed agents", FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeCaption,
        Foreground = DeskTheme.Muted, Background = DeskTheme.Raised, BorderBrush = DeskTheme.Divider, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(DeskTheme.Radius6), Padding = new Thickness(DeskTheme.Space8, DeskTheme.Space4, DeskTheme.Space8, DeskTheme.Space4),
        HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
    private readonly MenuFlyout _closedAgentsMenu = new();

    private UIElement BuildSubagentHeader()
    {
        _closedAgents.Flyout = _closedAgentsMenu;
        _closedAgentsMenu.Opening += (_, _) => FillClosedAgentsMenu();
        AutomationName(_closedAgents, "Reopen a closed agent card");
        var note = SubagentCaption("Helpers that are not part of a swarm.");
        AutomationName(note, "Subagents tab note");
        return new StackPanel { Spacing = DeskTheme.Space8, Margin = new Thickness(0, 0, 0, DeskTheme.Space4), Children = { note, _subagentSummary, _closedAgents } };
    }

    private bool IsSubagentCardClosed(string id) => (_saved.ClosedSubagentIds ?? []).Contains(id, StringComparer.Ordinal);

    private void CloseSubagentCard(string id)
    {
        if (!_subagentRows.ContainsKey(id)) return;
        _saved = _saved with { ClosedSubagentIds = (_saved.ClosedSubagentIds ?? []).Append(id).Distinct(StringComparer.Ordinal).ToArray() };
        _subagentViews[id].SetClosed(true);
        CloseSubagentPanel(id);
        UpdateSubagentCounts();
    }

    private void ReopenSubagentCard(string id)
    {
        if (!_subagentRows.TryGetValue(id, out var child)) return;
        var closed = (_saved.ClosedSubagentIds ?? []).Where(value => value != id).ToArray();
        _saved = _saved with { ClosedSubagentIds = closed.Length == 0 ? null : closed };
        var view = _subagentViews[id];
        view.SetClosed(false);
        view.Update(child);
        UpdateSubagentCounts();
        if (_saved.SubagentChatPanelsEnabled) OpenSubagentPanel(id);
        else { view.Root.IsExpanded = true; Save(); }
    }

    private void UpdateClosedAgentCount()
    {
        var count = _subagentRows.Keys.Count(IsSubagentCardClosed);
        _closedAgents.Content = $"Closed agents · {count}";
        _closedAgents.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _subagentEmpty.Text = count > 0 ? "Reopen a card from Closed agents." : "No helpers yet.";
        _subagentEmpty.Visibility = !TeamUiActive && _subagentRows.Count == count ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FillClosedAgentsMenu()
    {
        _closedAgentsMenu.Items.Clear();
        foreach (var id in (_saved.ClosedSubagentIds ?? []).Reverse())
        {
            if (!_subagentRows.TryGetValue(id, out var child)) continue;
            var name = string.IsNullOrWhiteSpace(child.Name) ? "Subagent" : child.Name.Trim();
            var item = new MenuFlyoutItem
            {
                Text = name + " · " + SubagentStatusLabel(child.State), FontFamily = DeskTheme.UiFont,
                FontSize = DeskTheme.SizeBody, Foreground = DeskTheme.Text
            };
            item.Click += (_, _) => ReopenSubagentCard(id);
            _closedAgentsMenu.Items.Add(item);
        }
    }
}
