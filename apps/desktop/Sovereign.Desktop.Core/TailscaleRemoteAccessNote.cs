namespace Sovereign.Desktop.Core;

/// <summary>
/// Settings copy for phone access and the Grok Bot connector.
/// Shown when Tailscale mode is selected or offered. Bind, port, and auth stay as they are.
/// </summary>
public static class TailscaleRemoteAccessNote
{
    public const string Text =
        "Tailscale mode is remote access: any device on your tailnet can reach this port. It is protected only by the access token, so keep the token secret.";

    public static bool ShouldShow(bool tailscaleSelected, bool tailscaleAvailable) =>
        tailscaleSelected || tailscaleAvailable;
}
