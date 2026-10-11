namespace Sovereign.Desktop.Client;

/// <summary>Version advertised to provider runtimes, including 0.x releases.</summary>
internal static class DesktopClientVersion
{
    internal static string Current { get; } = FromVersion(typeof(DesktopClientVersion).Assembly.GetName().Version);
    internal static string FromVersion(Version? version) => version is { Build: >= 0 }
        ? version.ToString(3) : "unknown";
}
