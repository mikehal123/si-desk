namespace Sovereign.Desktop.SetupLib;

public sealed record SetupRequest(
    SetupAction Action,
    string InstallDir,
    string? PayloadDir,
    string ProductId,
    string ProductName,
    string Version,
    string? ProfileDir,
    string StartMenuName,
    bool KeepData,
    int? FailAfterFileCount,
    string? SentinelPath,
    bool DesktopShortcut = false);

public enum SetupAction
{
    Install,
    Repair,
    Upgrade,
    Uninstall
}

public sealed record SetupResult(int ExitCode, string Message, IReadOnlyList<string> Log)
{
    public bool Ok => ExitCode == 0;
}
