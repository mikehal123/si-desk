using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Sovereign.Desktop;

/// <summary>One set of chrome, typography, spacing and motion tokens for xAI Desk.</summary>
internal static class DeskTheme
{
    public const double Space4 = 4, Space8 = 8, Space12 = 12, Space16 = 16, Space24 = 24;
    public const double Radius6 = 6, Radius10 = 10;
    public const double SizeCaption = 12, SizeBody = 14, SizeHeading = 16, SizeTitle = 18, SizeCode = 13;
    public const int MotionMilliseconds = 120;
    public static FontFamily UiFont { get; } = new("Segoe UI Variable Text");
    public static FontFamily MonoFont { get; } = new("Cascadia Mono, Consolas");
    public static FontFamily IconFont { get; } = new("Segoe Fluent Icons");
    public static SolidColorBrush Background { get; } = Brush(12, 12, 15);
    public static SolidColorBrush Surface { get; } = Brush(20, 20, 24);
    public static SolidColorBrush Raised { get; } = Brush(29, 29, 35);
    public static SolidColorBrush Divider { get; } = Brush(48, 48, 56);
    public static SolidColorBrush Text { get; } = Brush(238, 238, 243);
    public static SolidColorBrush Muted { get; } = Brush(177, 177, 187);
    public static SolidColorBrush Accent { get; } = Brush(238, 238, 243);
    public static SolidColorBrush Hover { get; } = Brush(38, 38, 45);
    public static SolidColorBrush Pressed { get; } = Brush(47, 47, 56);
    public static SolidColorBrush Success { get; } = Brush(151, 218, 176);
    public static SolidColorBrush Error { get; } = Brush(255, 164, 170);
    public static SolidColorBrush Warning { get; } = Brush(247, 210, 150);
    public static SolidColorBrush Transparent { get; } = new(Microsoft.UI.Colors.Transparent);

    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromArgb(255, r, g, b));
    public static SolidColorBrush StatusForeground(string? status) => status?.ToLowerInvariant() switch
    {
        "completed" or "finished" => Success,
        "failed" or "error" => Error,
        "blocked" or "unconfirmed" or "disconnected" => Warning,
        "running" or "in_progress" => Accent,
        _ => Muted
    };
    public static SolidColorBrush StatusSurface(string? _) => Raised;

    public static void Install(ResourceDictionary resources)
    {
        resources["ControlContentThemeFontFamily"] = UiFont;
        resources["ContentControlThemeFontFamily"] = UiFont;
        resources["ControlContentThemeFontSize"] = SizeBody;
        resources["ControlCornerRadius"] = new CornerRadius(Radius6);
        resources["OverlayCornerRadius"] = new CornerRadius(Radius10);
        foreach (var name in new[] { "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3", "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3" })
            resources[name] = Accent.Color;
        foreach (var name in new[] { "TextFillColorPrimaryBrush", "ControlForeground", "ButtonForeground", "ButtonForegroundPointerOver", "ButtonForegroundPressed", "ToggleButtonForeground", "TextControlForeground", "ComboBoxForeground" })
            resources[name] = Text;
        foreach (var name in new[] { "TextFillColorSecondaryBrush", "TextFillColorTertiaryBrush", "TextControlPlaceholderForeground", "TextControlPlaceholderForegroundFocused" })
            resources[name] = Muted;
        foreach (var name in new[] { "ButtonBackground", "ToggleButtonBackground", "ComboBoxBackground", "TextControlBackground", "TextControlBackgroundFocused", "FlyoutPresenterBackground", "MenuFlyoutPresenterBackground" })
            resources[name] = Surface;
        foreach (var name in new[] { "ButtonBackgroundPointerOver", "ToggleButtonBackgroundPointerOver", "ComboBoxBackgroundPointerOver", "ListViewItemBackgroundPointerOver", "MenuFlyoutItemBackgroundPointerOver" })
            resources[name] = Hover;
        foreach (var name in new[] { "ButtonBackgroundPressed", "ToggleButtonBackgroundPressed", "ComboBoxBackgroundPressed", "ListViewItemBackgroundPressed", "MenuFlyoutItemBackgroundPressed" })
            resources[name] = Pressed;
        foreach (var name in new[] { "ButtonBorderBrush", "ComboBoxBorderBrush", "TextControlBorderBrush", "FlyoutPresenterBorderBrush", "MenuFlyoutPresenterBorderBrush" })
            resources[name] = Divider;
        resources["ComboBoxBackgroundDisabled"] = Surface;
        resources["ComboBoxForegroundDisabled"] = Muted;
        resources["ComboBoxBorderBrushDisabled"] = Divider;
        resources["SystemControlFocusVisualPrimaryBrush"] = Accent;
        resources["FocusStrokeColorOuterBrush"] = Accent;
        resources["SystemControlFocusVisualSecondaryBrush"] = Background;
        resources["FocusStrokeColorInnerBrush"] = Background;
        resources["ControlFastAnimationDuration"] = TimeSpan.FromMilliseconds(MotionMilliseconds);
        resources["CheckBoxCheckBackgroundFillChecked"] = Accent;
        resources["CheckBoxCheckBackgroundFillCheckedPointerOver"] = Accent;
        resources["CheckBoxCheckBackgroundFillCheckedPressed"] = Accent;
        resources["CheckBoxCheckGlyphForegroundChecked"] = Background;
        resources["CheckBoxCheckGlyphForegroundCheckedPointerOver"] = Background;
        resources["CheckBoxCheckGlyphForegroundCheckedPressed"] = Background;
        resources["DeskTextBrush"] = Text; resources["DeskMutedBrush"] = Muted;
        resources["DeskBodySize"] = SizeBody; resources["DeskCaptionSize"] = SizeCaption;
        resources["DeskRadius6"] = new CornerRadius(Radius6);
    }

    public static void Apply(DependencyObject root)
    {
        if (root is Control control)
        {
            if (control.FontFamily.Source is not ("Consolas" or "Cascadia Mono, Consolas")) control.FontFamily = UiFont;
            control.UseSystemFocusVisuals = true;
            control.FocusVisualPrimaryBrush = Accent;
            control.FocusVisualSecondaryBrush = Background;
        }
        if (root is TextBlock block)
        {
            if (block.FontFamily.Source is not ("Consolas" or "Cascadia Mono, Consolas" or "Segoe Fluent Icons")) block.FontFamily = UiFont;
            block.Opacity = 1;
        }
        if (root is Border border) border.BackgroundTransition = new BrushTransition { Duration = TimeSpan.FromMilliseconds(MotionMilliseconds) };
        if (root is Panel panel) panel.BackgroundTransition = new BrushTransition { Duration = TimeSpan.FromMilliseconds(MotionMilliseconds) };
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) Apply(VisualTreeHelper.GetChild(root, i));
    }
}
