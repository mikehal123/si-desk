using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Sovereign.Desktop.MainWindow;

namespace Sovereign.Desktop;

internal sealed partial class ChatConversation
{
    private readonly CheckBox _subagentPanelsOption = new() { Content = "Open subagents as chat panels" };

    private UIElement BuildSubagentPanelsOption()
    {
        _subagentPanelsOption.IsChecked = _saved.SubagentChatPanelsEnabled;
        AutomationName(_subagentPanelsOption, "Open subagents as chat panels");
        ToolTipService.SetToolTip(_subagentPanelsOption, "Also open each helper in a chat row under the transcript. The Subagents list stays. Turn off to keep only the expandable cards.");
        _subagentPanelsOption.Click += (_, _) =>
        {
            if (_restoring) return;
            _saved = _saved with { OpenSubagentChatPanels = _subagentPanelsOption.IsChecked == true };
            ApplySubagentChatPanelMode();
            Save();
        };
        return _subagentPanelsOption;
    }

    private void UpdateSubagentPanelsOption()
    {
        _subagentPanelsOption.Visibility = _subagentOption.Visibility;
        _subagentPanelsOption.IsEnabled = !_chatBusy;
        _subagentPanelsOption.IsChecked = _saved.SubagentChatPanelsEnabled;
    }
}
