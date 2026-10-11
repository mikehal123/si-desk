using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Sovereign.Desktop.Client;
using Sovereign.Desktop.Core;

namespace Sovereign.Desktop;

public sealed partial class MainWindow
{
    private readonly TextBlock _pageTitle = new() { FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeTitle, Foreground = DeskTheme.Text, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _pageSubtitle = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _emptyState = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(DeskTheme.Space16), FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeBody, Foreground = DeskTheme.Muted };
    private readonly StackPanel _draftPanel = new() { Spacing = DeskTheme.Space12 };
    private readonly StackPanel _inspectorPanel = new() { Spacing = DeskTheme.Space12 };
    private readonly StackPanel _settingsPanel = new() { Spacing = DeskTheme.Space12 };
    private readonly StackPanel _runPanel = new() { Spacing = DeskTheme.Space12 };
    private Grid? _recordToolbar = null;
    private Grid? _metrics = null;
    private Border? _inspectorCard = null;
    private ColumnDefinition? _inspectorColumn = null;
    private string _boundNav = "";

    internal static SolidColorBrush Ink(byte r, byte g, byte b) => new(Windows.UI.Color.FromArgb(255, r, g, b));
    private static Border Card(UIElement child) => new()
    {
        Background = DeskTheme.Surface, BorderBrush = DeskTheme.Divider, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(DeskTheme.Radius10), Padding = new Thickness(DeskTheme.Space16), Child = child,
        BackgroundTransition = new BrushTransition { Duration = TimeSpan.FromMilliseconds(DeskTheme.MotionMilliseconds) }
    };
    internal static TextBlock Note(string text) => new()
    {
        Text = text, FontFamily = DeskTheme.UiFont, Foreground = DeskTheme.Muted,
        TextWrapping = TextWrapping.Wrap, FontSize = DeskTheme.SizeBody, LineHeight = DeskTheme.Space24, Opacity = 1
    };

    private static Button DeskButton(string content) => new()
    {
        Content = content, FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeBody,
        Background = DeskTheme.Surface, Foreground = DeskTheme.Text, BorderBrush = DeskTheme.Divider,
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(DeskTheme.Radius6),
        Padding = new Thickness(DeskTheme.Space12, DeskTheme.Space8, DeskTheme.Space12, DeskTheme.Space8),
        MinHeight = DeskTheme.Space24 + DeskTheme.Space12, UseSystemFocusVisuals = true,
        FocusVisualPrimaryBrush = DeskTheme.Accent, FocusVisualSecondaryBrush = DeskTheme.Background
    };

    private readonly TextBlock _connectionDot = new() { Text = "●", FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeCaption, VerticalAlignment = VerticalAlignment.Center };
    private static FontIcon Glyph(string glyph) => new() { Glyph = glyph, FontFamily = DeskTheme.IconFont, FontSize = DeskTheme.SizeHeading, Foreground = DeskTheme.Muted };
    private static Button Inactive(string label, string reason)
    {
        var button = DeskButton(label);
        button.IsEnabled = false;
        button.FontSize = DeskTheme.SizeCaption;
        AutomationName(button, label + ". " + reason);
        ToolTipService.SetToolTip(button, reason);
        return button;
    }

    private Grid BuildShell()
    {
        _banner.Foreground = DeskTheme.Muted;
        _banner.FontSize = DeskTheme.SizeCaption;
        _account.Foreground = DeskTheme.Muted;
        _status.FontSize = DeskTheme.SizeCaption;
        _status.Foreground = DeskTheme.Muted;
        _hint.Foreground = DeskTheme.Muted;
        _pageSubtitle.Foreground = DeskTheme.Muted;
        _pageSubtitle.FontFamily = DeskTheme.UiFont;
        _pageSubtitle.FontSize = DeskTheme.SizeBody;
        _host.FontSize = DeskTheme.SizeCaption;
        _host.Foreground = DeskTheme.Muted;
        _draftError.Foreground = DeskTheme.Error;
        _nav.Width = double.NaN;
        _nav.SelectionMode = ListViewSelectionMode.None;
        _nav.Padding = new Thickness(0);
        _nav.Margin = new Thickness(0, DeskTheme.Space8, 0, 0);
        _nav.Resources["ListViewItemBackgroundSelected"] = DeskTheme.Raised;
        _nav.Resources["ListViewItemBackgroundSelectedPointerOver"] = DeskTheme.Hover;
        _nav.Resources["ListViewItemBackgroundSelectedPressed"] = DeskTheme.Pressed;
        _nav.Resources["ListViewItemSelectionIndicatorBrush"] = DeskTheme.Accent;
        _nav.Resources["ListViewItemSelectionIndicatorPointerOverBrush"] = DeskTheme.Accent;
        _nav.Resources["ListViewItemForegroundSelected"] = DeskTheme.Text;
        foreach (ListViewItem item in _nav.Items)
        {
            var tag = (string)item.Tag;
            var glyph = tag switch { "chat" => "\uE8F2", "room" => "\uE902", "grokbot" => "\uE99A", "settings" => "\uE713", _ => "\uE8F2" };
            UIElement mark = tag == "grokbot" ? GrokbotNavMark() : Glyph(glyph);
            item.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DeskTheme.Space12, Children = { mark, new TextBlock { Text = Label(tag), FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeBody, Foreground = DeskTheme.Text, VerticalAlignment = VerticalAlignment.Center } } };
            item.MinHeight = DeskTheme.Space24 + DeskTheme.Space16;
            item.Padding = new Thickness(DeskTheme.Space12, DeskTheme.Space8, DeskTheme.Space12, DeskTheme.Space8);
            item.CornerRadius = new CornerRadius(DeskTheme.Radius6);
            item.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            item.UseSystemFocusVisuals = true;
            item.FocusVisualPrimaryBrush = DeskTheme.Accent;
            item.FocusVisualSecondaryBrush = DeskTheme.Background;
            // Keep the stock template's pointer and keyboard states while navigation supplies the active row.
            item.RegisterPropertyChangedCallback(Control.BackgroundProperty, (sender, _) => ApplyNavigationPresentation((ListViewItem)sender));
            ApplyNavigationPresentation(item);
        }
        _main.Background = DeskTheme.Transparent;
        _main.BorderThickness = new Thickness(0);
        _main.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><TextBlock Text='{Binding}' TextWrapping='Wrap' Margin='12,8' FontFamily='{ThemeResource ControlContentThemeFontFamily}' FontSize='{ThemeResource DeskBodySize}' Foreground='{ThemeResource DeskTextBrush}'/></DataTemplate>");
        _search.Width = double.NaN;
        _search.PlaceholderText = "Filter";
        _scenarios.Width = double.NaN; _modes.Width = double.NaN;
        _scenarios.Header = "Preview scenario"; _modes.Header = "Workspace mode";

        var brand = new Grid { Margin = new Thickness(DeskTheme.Space8, DeskTheme.Space16, DeskTheme.Space8, DeskTheme.Space24), ColumnSpacing = DeskTheme.Space8 };
        brand.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        brand.ColumnDefinitions.Add(new ColumnDefinition());
        brand.Children.Add(DeskLogo(DeskTheme.Space24 + DeskTheme.Space8));
        var brandName = new TextBlock { Text = XaiDeskIdentity.AppName, FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeBody, Foreground = DeskTheme.Text, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(brandName, 1);
        brand.Children.Add(brandName);
        var navPanel = new StackPanel { Spacing = DeskTheme.Space4, Padding = new Thickness(DeskTheme.Space12, 0, DeskTheme.Space12, DeskTheme.Space8) };
        navPanel.Children.Add(brand);
        navPanel.Children.Add(_nav);
        navPanel.Children.Add(BuildChatRecents());
        var accountName = new TextBlock { Text = "Settings", FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeBody, Foreground = DeskTheme.Text, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, VerticalAlignment = VerticalAlignment.Center };
        var accountInitial = new TextBlock { Text = "x", FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeBody, Foreground = DeskTheme.Text, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var identity = new Grid { ColumnSpacing = DeskTheme.Space12 };
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        identity.ColumnDefinitions.Add(new ColumnDefinition());
        identity.Children.Add(new Border { Background = DeskTheme.Raised, CornerRadius = new CornerRadius(DeskTheme.Radius6), Width = DeskTheme.Space24 + DeskTheme.Space8, Height = DeskTheme.Space24 + DeskTheme.Space8, Child = accountInitial });
        Grid.SetColumn(accountName, 1); identity.Children.Add(accountName);
        var navGrid = new Grid(); navGrid.RowDefinitions.Add(new RowDefinition()); navGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        navGrid.Children.Add(new ScrollViewer { Content = navPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var profileButton = new Button { Content = identity, Background = DeskTheme.Transparent, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(DeskTheme.Radius6), Padding = new Thickness(DeskTheme.Space12, DeskTheme.Space8, DeskTheme.Space12, DeskTheme.Space8), Margin = new Thickness(DeskTheme.Space8), HorizontalContentAlignment = HorizontalAlignment.Stretch, UseSystemFocusVisuals = true };
        profileButton.Click += (_,_) => Navigate("settings"); AutomationName(profileButton,"Your xAI Desk account");
        var profileRow = new Border { BorderBrush = DeskTheme.Divider, BorderThickness = new Thickness(0, 1, 0, 0), Child = profileButton };
        Grid.SetRow(profileRow, 1); navGrid.Children.Add(profileRow);
        var sidebar = new Border { Background = DeskTheme.Background, BorderBrush = DeskTheme.Divider, BorderThickness = new Thickness(0,0,1,0), Child = navGrid };

        BuildSettingsPage();
        BuildChatPage();
        BuildRoomPage();
        App.CrashLog("before BuildGrokbotPage");
        try
        {
            BuildGrokbotPage();
        }
        catch (Exception ex)
        {
            App.CrashLog("BUILD_GROKBOT " + ex);
            throw;
        }
        App.CrashLog("after BuildGrokbotPage");
        var contentHost = new Grid(); contentHost.Children.Add(PageScroll(_chatPage, minimumWidth: 620, fillHeight: true));
        contentHost.Children.Add(PageScroll(_roomPage, minimumWidth: 900, fillHeight: true));
        App.CrashLog("before grokbot PageScroll");
        contentHost.Children.Add(PageScroll(_grokbotPage, minimumWidth: 620, fillHeight: true));
        App.CrashLog("after grokbot PageScroll");
        contentHost.Children.Add(_settingsPage);
        var systemHost = new Grid();
        systemHost.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto }); systemHost.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto }); systemHost.RowDefinitions.Add(new RowDefinition());
        _systemTabs.Spacing = DeskTheme.Space8;
        var tabScroll=new ScrollViewer { Content=_systemTabs, HorizontalScrollBarVisibility=ScrollBarVisibility.Auto, VerticalScrollBarVisibility=ScrollBarVisibility.Disabled, HorizontalScrollMode=ScrollMode.Enabled, Margin = new Thickness(DeskTheme.Space24,DeskTheme.Space8,DeskTheme.Space24,0) };
        App.CrashLog("before control banner");
        var banners = new StackPanel { Children = { _grokbotControlBanner } };
        App.CrashLog("after control banner");
        systemHost.Children.Add(banners); Grid.SetRow(tabScroll,1); systemHost.Children.Add(tabScroll); Grid.SetRow(contentHost,2); systemHost.Children.Add(contentHost);
        var layout = DeskLayoutStore.Load(_vm.ProfileDirectory);
        var sidebarWidth = layout.Sidebar;
        var sidebarColumn = new ColumnDefinition { Width = new GridLength(sidebarWidth) };
        var body = new Grid();
        double bodyWidth() => body.ActualWidth > 0 ? body.ActualWidth : 1440;
        var sidebarSplitter = new SplitterBar();
        AutomationName(sidebarSplitter, "Resize sidebar");
        void FitContent(double window)
        {
            var shown = sidebarColumn.Width.Value;
            var available = Math.Max(1, window - shown - DeskSplit.Grip);
            systemHost.Width = available; systemHost.MaxWidth = available; contentHost.MaxWidth = available;
        }
        sidebarSplitter.DragDelta += delta =>
        {
            var current = sidebarColumn.Width.Value > 0 ? sidebarColumn.Width.Value : sidebarWidth;
            sidebarWidth = DeskSplit.DragSidebar(current, delta, bodyWidth());
            sidebarColumn.Width = new GridLength(sidebarWidth);
            FitContent(bodyWidth());
        };
        sidebarSplitter.DragCompleted += () => DeskLayoutStore.SaveSidebar(_vm.ProfileDirectory, sidebarWidth);
        sidebarSplitter.ResetWidth += () =>
        {
            sidebarWidth = DeskSplit.SidebarDefault;
            sidebarColumn.Width = new GridLength(DeskSplit.ClampSidebar(sidebarWidth, bodyWidth()));
            DeskLayoutStore.SaveSidebar(_vm.ProfileDirectory, sidebarWidth);
            FitContent(bodyWidth());
        };
        sidebarSplitter.Nudge += arrow =>
        {
            var current = sidebarColumn.Width.Value > 0 ? sidebarColumn.Width.Value : sidebarWidth;
            sidebarWidth = DeskSplit.NudgeSidebar(current, arrow, bodyWidth());
            sidebarColumn.Width = new GridLength(sidebarWidth);
            DeskLayoutStore.SaveSidebar(_vm.ProfileDirectory, sidebarWidth);
            FitContent(bodyWidth());
        };
        body.ColumnDefinitions.Add(sidebarColumn);
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.Children.Add(sidebar);
        Grid.SetColumn(sidebarSplitter, 1);
        body.Children.Add(sidebarSplitter);
        Grid.SetColumn(systemHost, 2);
        body.Children.Add(systemHost);
        body.SizeChanged += (_, e) =>
        {
            var shown = DeskSplit.ClampSidebar(sidebarWidth, e.NewSize.Width);
            sidebarColumn.Width = new GridLength(shown);
            FitContent(e.NewSize.Width);
        };

        var menu=new MenuBar { Background=DeskTheme.Surface, FontFamily=DeskTheme.UiFont, FontSize=DeskTheme.SizeBody, Padding = new Thickness(DeskTheme.Space8,0,0,0) };
        MenuFlyoutItem Command(string label,Action action){var item=new MenuFlyoutItem { Text=label };item.Click+=(_,_)=>action();return item;}
        var file=new MenuBarItem { Title="File" };
        _openProjectMenu = new MenuFlyoutItem { Text = "Open Folder…" };
        _openProjectMenu.Click += async (_, _) => await OpenProjectFolderAsync();
        _closeProjectMenu = Command("Close Folder", CloseProjectFolder); _closeProjectMenu.IsEnabled = false;
        file.Items.Add(_openProjectMenu); file.Items.Add(_closeProjectMenu); file.Items.Add(new MenuFlyoutSeparator());
        file.Items.Add(Command("Close window",Close));
        var view=new MenuBarItem { Title="View" };foreach(var tag in XaiDeskIdentity.Navigation){var target=tag;view.Items.Add(Command(Label(tag),()=>Navigate(target)));}
        var help=new MenuBarItem { Title="Help" };help.Items.Add(Command("Settings / Support",()=>Navigate("settings")));
        menu.Items.Add(file);menu.Items.Add(view);menu.Items.Add(help);
        var chrome=new Grid { Background=DeskTheme.Surface,Height=36,Padding = new Thickness(0,0,DeskTheme.Space24 + DeskTheme.Space24 + DeskTheme.Space24 + DeskTheme.Space24 + DeskTheme.Space24 + DeskTheme.Space16,0) };
        chrome.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });chrome.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });chrome.ColumnDefinitions.Add(new ColumnDefinition());
        var titleMark = DeskLogo(18);
        titleMark.Margin = new Thickness(DeskTheme.Space12, 0, DeskTheme.Space8, 0);
        titleMark.VerticalAlignment = VerticalAlignment.Center;
        chrome.Children.Add(titleMark);
        Grid.SetColumn(menu, 1); chrome.Children.Add(menu);var drag=new Border { Background=DeskTheme.Surface };Grid.SetColumn(drag,2);chrome.Children.Add(drag);
        ExtendsContentIntoTitleBar=true;SetTitleBar(drag);
        var root=new Grid { RequestedTheme=ElementTheme.Dark,Background=DeskTheme.Background };
        DeskTheme.Install(root.Resources);
        DeskTheme.Install(root.Resources);
        root.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });root.RowDefinitions.Add(new RowDefinition());
        root.Children.Add(chrome);Grid.SetRow(body,1);root.Children.Add(body);
        root.SizeChanged+=(_,e)=>{if(_inspectorColumn is not null && _inspectorCard?.Visibility==Visibility.Visible)_inspectorColumn.Width=new GridLength(e.NewSize.Width<1200?260:300);};
        return root;
    }

    private void BindDesign()
    {
        _title.Text = XaiDeskIdentity.AppName;
        _connectionDot.Foreground = _vm.IsNativeAuthenticated ? DeskTheme.Success : DeskTheme.Muted;
        ToolTipService.SetToolTip(_connectionDot, _vm.IsNativeAuthenticated ? "Connected" : "No authenticated native connection");
        _banner.Text = _vm.Mode switch
        {
            ApplicationMode.NativeConnection => _vm.IsNativeAuthenticated ? "CONNECTED" : "DISCONNECTED",
            ApplicationMode.EvidenceArchive => "ARCHIVE  ·  Read only",
            _ => "PREVIEW"
        };
        _account.Text = _vm.Account.IsKnown ? "Run loaded · " + _vm.Account.Value!.AccountId : "Inspect connection details in Settings.";
        var nav = CurrentSection;
        foreach (ListViewItem item in _nav.Items) ApplyNavigationPresentation(item);
        _pageTitle.Text = Label(nav);
        _pageSubtitle.Text = nav switch
        {
            "room" => "Shared room.",
            "grokbot" => "Mailbox chat with Grokbot.",
            "settings" => "Your app, connections and local data.",
            _ => "Conversations on this PC."
        };
        _draftPanel.Visibility = nav == "mandate" ? Visibility.Visible : Visibility.Collapsed;
        _settingsPanel.Visibility = nav == "connection" ? Visibility.Visible : Visibility.Collapsed;
        _inspectorPanel.Visibility = nav == "decisions" ? Visibility.Visible : Visibility.Collapsed;
        _runPanel.Visibility = nav is "overview" or "evaluation" ? Visibility.Visible : Visibility.Collapsed;
        _homeActions.Visibility = nav == "home" ? Visibility.Visible : Visibility.Collapsed;
        _banner.Visibility = Visibility.Collapsed;
        if (_inspectorColumn is not null && _inspectorCard is not null)
        {
            var shown = nav is "home" or "overview" or "evaluation" or "decisions" or "mandate" or "connection";
            _inspectorColumn.Width = new GridLength(shown ? 300 : 0);
            _inspectorCard.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        }
        if (_recordToolbar is not null) _recordToolbar.Visibility = nav == "decisions" ? Visibility.Visible : Visibility.Collapsed;
        if (_metrics is not null) _metrics.Visibility = nav == "overview" ? Visibility.Visible : Visibility.Collapsed;
        _emptyState.Visibility = _main.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _emptyState.Text = "No records in this view.";
        if (_boundNav != nav) { _detail.Text = "Select a record to inspect its details."; _evidence.Text = "No evidence selected."; _boundNav = nav; }
        _scenarios.Visibility = _vm.Mode == ApplicationMode.DesktopPreview ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyNavigationPresentation(ListViewItem item)
    {
        var background = (string)item.Tag == _activeSystem ? DeskTheme.Raised : DeskTheme.Transparent;
        if (!ReferenceEquals(item.Background, background)) item.Background = background;
    }

    private static Image DeskLogo(double size)
    {
        var image = new Image { Width = size, Height = size, Stretch = Stretch.Uniform };
        AutomationName(image, XaiDeskIdentity.AppName + " logo");
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "logo.png");
        if (System.IO.File.Exists(path)) image.Source = new BitmapImage(new Uri(path));
        return image;
    }
}
