using System.Runtime.InteropServices;

namespace Sovereign.Desktop;

public sealed partial class MainWindow
{
    internal void AcceptShellFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        Activate();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ShowShellWindow(hwnd, 9);
        FocusShellWindow(hwnd);
        Navigate("chat", Sovereign.Desktop.Connector.PageSwitchSource.Shell);
        _currentChat.EnqueueShellFile(path);
    }

    [DllImport("user32.dll", EntryPoint = "ShowWindow")]
    private static extern bool ShowShellWindow(IntPtr window, int command);

    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    private static extern bool FocusShellWindow(IntPtr window);
}
