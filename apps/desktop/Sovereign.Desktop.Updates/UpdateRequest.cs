namespace Sovereign.Desktop.Updates;

public sealed record UpdateRequest(string InstallDirectory, string ProfileDirectory, int ProcessId, long ProcessStartTicks, string CurrentVersion,
    DeskUpdateSnapshot? DeskSnapshot = null);

public static class UpdatePaths
{
    public static string ValidateInstall(string install, string localAppData)
    {
        install = Path.GetFullPath(install).TrimEnd(Path.DirectorySeparatorChar);
        var parent = Path.Combine(Path.GetFullPath(localAppData), "xAIDesk");
        var baseDir = Directory.GetParent(install) ?? throw new IOException("Invalid install path.");
        if (!string.Equals(baseDir.Parent?.FullName, parent, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(install) != "app" || !System.Text.RegularExpressions.Regex.IsMatch(baseDir.Name, "^(main|qa-[a-z0-9-]+)$"))
            throw new IOException("Automatic updates require an installed copy of xAI Desk.");
        for (var directory = new DirectoryInfo(install); directory is not null; directory = directory.Parent)
            if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Update paths cannot use directory links.");
        return baseDir.FullName;
    }
}
