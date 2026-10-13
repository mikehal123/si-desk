using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Sovereign.Desktop.Connector;

namespace Sovereign.Desktop;

internal sealed class GrokbotAppToaster : IGrokbotToaster
{
    private static readonly object RegisterGate = new();
    private static volatile bool _registered;

    public void Show(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        try
        {
            if (!AppNotificationManager.IsSupported()) return;
            EnsureRegistered();
            AppNotificationManager.Default.Show(new AppNotificationBuilder().AddText(line).BuildNotification());
        }
        catch
        {
        }
    }

    private static void EnsureRegistered()
    {
        if (_registered) return;
        lock (RegisterGate)
        {
            if (_registered) return;
            AppNotificationManager.Default.Register();
            _registered = true;
        }
    }
}
