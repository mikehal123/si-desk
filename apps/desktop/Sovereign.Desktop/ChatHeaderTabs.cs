using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static Sovereign.Desktop.MainWindow;

namespace Sovereign.Desktop;

internal sealed partial class ChatConversation
{
    private Button? _chatConnectionButton;
    private StackPanel? _chatHeadingActions;
    private bool _compactHeaderTabs;

    private void RefreshChatHeaderTabs()
    {
        if (_chatConnectionButton is null || _chatHeadingActions is null) return;
        var width = _chatHeading?.ActualWidth ?? _chatPage.ActualWidth;
        _compactHeaderTabs = width > 0 && width < 520;
        HeaderTab(_subagentsToggle, "\uE716", "Subagents", SubagentBadgeCount(), 116);
        HeaderTab(_changesToggle, "\uE8A5", "Changes", ChangesBadgeCount(), 108);
        HeaderTab(_chatConnectionButton, "\uE713", "Connection", null, 128);
        _teamOption.Visibility = Visibility.Collapsed;
    }

    private void HeaderTab(Button button, string glyph, string name, int? count, double fullWidth)
    {
        button.MinWidth = _compactHeaderTabs ? 36 : fullWidth;
        button.Width = button.MinWidth;
        button.Padding = new Thickness(DeskTheme.Space8, DeskTheme.Space8, DeskTheme.Space8, DeskTheme.Space8);
        button.CornerRadius = new CornerRadius(DeskTheme.Radius6);
        button.UseSystemFocusVisuals = true;
        button.HorizontalAlignment = HorizontalAlignment.Left;
        var icon = new FontIcon { Glyph = glyph, FontFamily = DeskTheme.IconFont, FontSize = DeskTheme.SizeBody,
            Foreground = DeskTheme.Muted };
        button.Content = _compactHeaderTabs ? icon : new StackPanel { Orientation = Orientation.Horizontal, Spacing = DeskTheme.Space8,
            Children = { icon, new TextBlock { Text = name + (count is null ? "" : " · " + count), FontSize = DeskTheme.SizeCaption,
                Foreground = DeskTheme.Text, VerticalAlignment = VerticalAlignment.Center } } };
        var detail = name + (count is null ? " settings" : " · " + count + " retained");
        ToolTipService.SetToolTip(button, detail);
    }
}
