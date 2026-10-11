using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Sovereign.Desktop;

public sealed partial class MainWindow
{
    private string _activeSystem = "chat";
    private string _laterSection = "overview";
    private readonly StackPanel _systemTabs = new() { Orientation = Orientation.Horizontal, Spacing = DeskTheme.Space4 };
    private readonly StackPanel _homeActions = new() { Spacing = DeskTheme.Space12 };
    private string CurrentSection => _activeSystem == "later" ? _laterSection : _activeSystem;

    private void MarkSystem(string system, string? section = null)
    {
        _activeSystem = system;
        _ = section;
        foreach (ListViewItem item in _nav.Items)
        {
            item.Background = (string)item.Tag == system ? DeskTheme.Hover : DeskTheme.Background;
            AutomationName(item, Label((string)item.Tag) + ((string)item.Tag == system ? " · selected" : ""));
        }
        _systemTabs.Children.Clear();
        void Tab(string title, string target, bool selected)
        {
            var button = Button(title, () => Navigate(target));
            button.Background = selected ? DeskTheme.Hover : DeskTheme.Surface;
            button.BorderThickness = new Thickness(0); button.CornerRadius = new CornerRadius(DeskTheme.Radius6);
            AutomationName(button, title + (selected ? " · selected" : ""));
            _systemTabs.Children.Add(button);
        }
        if (system == "chat") Tab("New conversation", "new-chat", false);
        _systemTabs.Visibility = _systemTabs.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Navigate(string target)
    {
        CloseRoomView(); CloseChatView(); CloseSettings(); CloseGrokbot();
        switch (target)
        {
            case "settings": OpenSettings(); return;
            case "chat": OpenChat(); return;
            case "new-chat": NewChat(); return;
            case "room": OpenRoom(); return;
            case "grokbot": OpenGrokbot(); return;
            default: OpenChat(); return;
        }
    }
}
