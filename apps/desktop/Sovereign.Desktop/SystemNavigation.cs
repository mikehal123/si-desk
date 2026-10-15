using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sovereign.Desktop.Connector;

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

    // Set by the connector before it injects a click or key; see PageSwitch.InputWindow.
    private long _connectorInputUntil;

    internal void NoteConnectorInput() =>
        _connectorInputUntil = Environment.TickCount64 + (long)PageSwitch.InputWindow.TotalMilliseconds;

    /// <summary>
    /// Changes the selected page. Only the user may move the view; background sources are ignored unless the page
    /// is already shown. The connector may switch only while the Grokbot-tab act window is open.
    /// </summary>
    private void Navigate(string target, PageSwitchSource source = PageSwitchSource.User)
    {
        source = PageSwitch.Resolve(source, _connectorInputUntil, Environment.TickCount64);
        var actOn = source == PageSwitchSource.Connector && _grokbotMail.ActWindowOpen();
        if (!PageSwitch.Allowed(source, _activeSystem, target, actOn)) return;
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
