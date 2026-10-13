using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Sovereign.Desktop.Connector;
using System.Windows.Automation;
using WinRT.Interop;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Sovereign.Desktop;

public sealed partial class MainWindow
{
    internal async Task<byte[]> CaptureGrokbotPngAsync()
    {
        if (Content is not UIElement root)
            throw new InvalidOperationException("The SI Desk window could not be captured.");
        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
        await bitmap.RenderAsync(root);
        var pixels = await bitmap.GetPixelsAsync();
        var data = new byte[(int)pixels.Length];
        using (var pixelReader = DataReader.FromBuffer(pixels))
            pixelReader.ReadBytes(data);
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, data);
        await encoder.FlushAsync();
        stream.Seek(0);
        var bytes = new byte[(int)stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        if (bytes.Length < 8) throw new InvalidOperationException("The SI Desk window could not be captured.");
        return bytes;
    }

    internal GrokbotWindowBounds GrokbotWindowBounds()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        if (hwnd == 0 || !GetWindowRect(hwnd, out var rect))
            throw new InvalidOperationException("The SI Desk window is not available.");
        return new GrokbotWindowBounds(rect.Left, rect.Top, Math.Max(0, rect.Right - rect.Left), Math.Max(0, rect.Bottom - rect.Top));
    }

    internal IReadOnlyList<GrokbotControl> ReadGrokbotControls()
    {
        var window = GrokbotWindowBounds();
        var hwnd = WindowNative.GetWindowHandle(this);
        var list = new List<GrokbotControl>();
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root is not null) WalkAutomation(root, 0, list, window);
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or ElementNotAvailableException)
        {
        }
        if (Content is DependencyObject visual)
            WalkVisual(visual, list, window, hwnd);
        return list;
    }

    internal string PerformGrokbotClick(string? controlId, int? x, int? y)
    {
        var window = GrokbotWindowBounds();
        var controls = ReadGrokbotControls();
        GrokbotControl? control = null;
        if (!string.IsNullOrWhiteSpace(controlId))
            control = controls.FirstOrDefault(item => string.Equals(item.Id, controlId, StringComparison.Ordinal))
                ?? throw new KeyNotFoundException("That control is not in the SI Desk window.");
        GrokbotControl? hit = null;
        if (x is int px && y is int py)
            hit = GrokbotWindow.SmallestHit(controls, px, py);
        GrokbotWindow.RequireClick(window, control, x, y, hit);
        var pointX = control is null ? x!.Value : control.X + control.Width / 2;
        var pointY = control is null ? y!.Value : control.Y + control.Height / 2;
        RequirePointOnThisWindow(pointX, pointY);
        if (!SetCursorPos(pointX, pointY))
            throw new InvalidOperationException("The click did not land on the SI Desk window.");
        mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
        mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
        return "Clicked.";
    }

    internal string PerformGrokbotType(string controlId, string text)
    {
        var window = GrokbotWindowBounds();
        var controls = ReadGrokbotControls();
        var control = controls.FirstOrDefault(item => string.Equals(item.Id, controlId, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException("That control is not in the SI Desk window.");
        GrokbotWindow.RequireType(control.Id, control.Type, control.Label, text, control.Password);
        GrokbotWindow.RequireInside(window, control);
        var hwnd = WindowNative.GetWindowHandle(this);
        var root = AutomationElement.FromHandle(hwnd) ?? throw new InvalidOperationException("The SI Desk window is not available.");
        var element = FindAutomation(root, control.Id) ?? throw new KeyNotFoundException("That control is not in the SI Desk window.");
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && pattern is ValuePattern value)
        {
            if (element.GetCurrentPropertyValue(ValuePattern.IsReadOnlyProperty) is true)
                throw new InvalidOperationException("That control does not accept text.");
            value.SetValue(text);
            return "Typed.";
        }
        if (FindTextBox(Content, control.Id) is TextBox box)
        {
            box.Text = text;
            return "Typed.";
        }
        throw new InvalidOperationException("That control does not accept text.");
    }

    internal string PerformGrokbotKeys(string keys)
    {
        GrokbotWindow.RequireSafeKeys(keys);
        var parsed = GrokbotWindow.ParseKeys(keys);
        var hwnd = WindowNative.GetWindowHandle(this);
        var focused = AutomationElement.FocusedElement;
        var inside = focused is not null && IsUnderWindow(hwnd, focused);
        var secret = focused is not null && GrokbotWindow.IsSecret(TextProp(focused, AutomationElement.AutomationIdProperty), TypeName(focused), TextProp(focused, AutomationElement.NameProperty), BoolProp(focused, AutomationElement.IsPasswordProperty));
        GrokbotWindow.RequireFocused(inside, secret);
        foreach (var modifier in parsed.Modifiers) keybd_event((byte)modifier, 0, 0, UIntPtr.Zero);
        keybd_event((byte)parsed.Key, 0, 0, UIntPtr.Zero);
        keybd_event((byte)parsed.Key, 0, KeyUp, UIntPtr.Zero);
        for (var i = parsed.Modifiers.Length - 1; i >= 0; i--) keybd_event((byte)parsed.Modifiers[i], 0, KeyUp, UIntPtr.Zero);
        return "Sent keys.";
    }

    internal void OpenNamedPage(string page) => Navigate(GrokbotWindow.CanonicalPage(page));

    private void RequirePointOnThisWindow(int x, int y)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var hit = GetAncestor(WindowFromPoint(new POINT { X = x, Y = y }), GaRoot);
        if (hit != hwnd)
            throw new InvalidOperationException("That point is not on the SI Desk window.");
    }

    private static void WalkAutomation(AutomationElement node, int depth, List<GrokbotControl> list, GrokbotWindowBounds window)
    {
        if (depth > 14 || list.Count >= 400) return;
        try
        {
            if (TryBounds(node, out var x, out var y, out var width, out var height))
            {
                var id = TextProp(node, AutomationElement.AutomationIdProperty);
                var name = TextProp(node, AutomationElement.NameProperty);
                var type = TypeName(node);
                var password = BoolProp(node, AutomationElement.IsPasswordProperty);
                if (id.Length > 0 || name.Length > 0)
                {
                    var control = new GrokbotControl(id.Length > 0 ? id : name, type, name, x, y, width, height, password);
                    if (GrokbotWindow.ContainsPoint(window, control.X + control.Width / 2, control.Y + control.Height / 2))
                        list.Add(control);
                }
            }
        }
        catch (ElementNotAvailableException) { return; }
        AutomationElement? child = null;
        try { child = TreeWalker.ControlViewWalker.GetFirstChild(node); }
        catch (ElementNotAvailableException) { return; }
        while (child is not null && list.Count < 400)
        {
            WalkAutomation(child, depth + 1, list, window);
            try { child = TreeWalker.ControlViewWalker.GetNextSibling(child); }
            catch (ElementNotAvailableException) { break; }
        }
    }

    private static AutomationElement? FindAutomation(AutomationElement node, string id)
    {
        try
        {
            if (string.Equals(TextProp(node, AutomationElement.AutomationIdProperty), id, StringComparison.Ordinal) || string.Equals(TextProp(node, AutomationElement.NameProperty), id, StringComparison.Ordinal))
                return node;
        }
        catch (ElementNotAvailableException) { return null; }
        AutomationElement? child = null;
        try { child = TreeWalker.ControlViewWalker.GetFirstChild(node); }
        catch (ElementNotAvailableException) { return null; }
        while (child is not null)
        {
            var found = FindAutomation(child, id);
            if (found is not null) return found;
            try { child = TreeWalker.ControlViewWalker.GetNextSibling(child); }
            catch (ElementNotAvailableException) { break; }
        }
        return null;
    }

    private static bool IsUnderWindow(nint hwnd, AutomationElement element)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            var current = element;
            for (var i = 0; i < 30 && current is not null; i++)
            {
                if (current.Equals(root)) return true;
                current = TreeWalker.ControlViewWalker.GetParent(current);
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException)
        {
        }
        return false;
    }

    private static string TextProp(AutomationElement node, AutomationProperty property)
    {
        try { return node.GetCurrentPropertyValue(property) as string ?? ""; }
        catch (ElementNotAvailableException) { return ""; }
    }

    private static bool BoolProp(AutomationElement node, AutomationProperty property)
    {
        try { return node.GetCurrentPropertyValue(property) is true; }
        catch (ElementNotAvailableException) { return false; }
    }

    private static string TypeName(AutomationElement node)
    {
        try
        {
            var value = node.GetCurrentPropertyValue(AutomationElement.ControlTypeProperty);
            return value?.GetType().GetProperty("ProgrammaticName")?.GetValue(value) as string ?? "";
        }
        catch (ElementNotAvailableException) { return ""; }
    }

    private static bool TryBounds(AutomationElement node, out int x, out int y, out int width, out int height)
    {
        x = y = width = height = 0;
        try
        {
            var rect = node.GetCurrentPropertyValue(AutomationElement.BoundingRectangleProperty);
            if (rect is null) return false;
            var type = rect.GetType();
            if (type.GetProperty("IsEmpty")?.GetValue(rect) is true) return false;
            x = ToInt(type.GetProperty("X")?.GetValue(rect));
            y = ToInt(type.GetProperty("Y")?.GetValue(rect));
            width = ToInt(type.GetProperty("Width")?.GetValue(rect));
            height = ToInt(type.GetProperty("Height")?.GetValue(rect));
            return width > 0 && height > 0;
        }
        catch (ElementNotAvailableException) { return false; }
    }

    private static int ToInt(object? value) => value is null ? 0 : Convert.ToInt32(value);

    private static void WalkVisual(DependencyObject node, List<GrokbotControl> list, GrokbotWindowBounds window, nint hwnd)
    {
        if (list.Count >= 400) return;
        if (node is FrameworkElement element && element.Visibility == Visibility.Visible && element.ActualWidth > 0 && element.ActualHeight > 0)
        {
            var id = Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(element);
            var name = Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(element);
            if (!string.IsNullOrEmpty(id) || !string.IsNullOrEmpty(name))
            {
                var key = string.IsNullOrEmpty(id) ? name : id;
                if (!list.Any(item => string.Equals(item.Id, key, StringComparison.Ordinal)))
                {
                    Windows.Foundation.Point? local = null;
                    try { local = element.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0)); }
                    catch (ArgumentException) { }
                    if (local is Windows.Foundation.Point point)
                    {
                        var origin = new POINT { X = (int)point.X, Y = (int)point.Y };
                        ClientToScreen(hwnd, ref origin);
                        var control = new GrokbotControl(key, element.GetType().Name, name ?? "", origin.X, origin.Y, (int)element.ActualWidth, (int)element.ActualHeight, element is PasswordBox);
                        if (GrokbotWindow.ContainsPoint(window, control.X + control.Width / 2, control.Y + control.Height / 2))
                            list.Add(control);
                    }
                }
            }
        }
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count && list.Count < 400; i++)
            WalkVisual(VisualTreeHelper.GetChild(node, i), list, window, hwnd);
    }

    private static TextBox? FindTextBox(object? node, string id)
    {
        if (node is not DependencyObject current) return null;
        if (node is TextBox box)
        {
            var name = Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(box);
            var auto = Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(box);
            if (string.Equals(auto, id, StringComparison.Ordinal) || string.Equals(name, id, StringComparison.Ordinal))
                return box;
        }
        var count = VisualTreeHelper.GetChildrenCount(current);
        for (var i = 0; i < count; i++)
        {
            var found = FindTextBox(VisualTreeHelper.GetChild(current, i), id);
            if (found is not null) return found;
        }
        return null;
    }

    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;
    private const uint KeyUp = 0x0002;
    private const uint GaRoot = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref POINT point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, nuint extra);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(POINT point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
}
