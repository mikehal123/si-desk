namespace Sovereign.Desktop.Client;

/// <summary>
/// Codex resolves pwsh from PATH. On this PC that is the zero-byte WindowsApps alias, which the sandbox refuses to start.
/// This Codex build has no shell-path setting, so the connection hides that alias from the process PATH instead.
/// </summary>
public static class CodexWindowsShell
{
    public const string WindowsPowerShell = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";
    public readonly record struct WhereChoice(string? Shell, bool AliasStillVisible);

    public static bool IsStorePowerShell(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        foreach (var segment in path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries))
            if (segment.Trim().Trim('"').Equals("WindowsApps", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>First non-alias pwsh on PATH, then a machine-wide PowerShell 7, then Windows PowerShell 5.1.</summary>
    public static string Select(IEnumerable<string> pathEntries, Func<string, bool> exists)
    {
        foreach (var entry in pathEntries)
        {
            if (string.IsNullOrWhiteSpace(entry) || IsStorePowerShell(entry)) continue;
            var candidate = Path.Combine(entry.Trim().Trim('"'), "pwsh.exe");
            if (!IsStorePowerShell(candidate) && exists(candidate)) return candidate;
        }
        var machine = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        if (!IsStorePowerShell(machine) && exists(machine)) return machine;
        return WindowsPowerShell;
    }

    public static string SelectDefault()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return Select(SplitPath(path), File.Exists);
    }

    public static string WithoutStoreEntries(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        return string.Join(Path.PathSeparator, SplitPath(path).Where(entry => !IsStorePowerShell(entry)));
    }

    /// <summary>
    /// Codex uses the first pwsh on PATH. A WindowsApps hit first means model commands still launch the alias,
    /// so a later PowerShell 5.1 path is not an acceptable substitute.
    /// </summary>
    public static WhereChoice Choose(string? pwshStdout, string? powershellStdout)
    {
        var pwsh = ShellPaths(pwshStdout, "pwsh.exe");
        if (pwsh.Count > 0)
            return IsStorePowerShell(pwsh[0]) ? new WhereChoice(null, true) : new WhereChoice(pwsh[0], false);
        var powershell = ShellPaths(powershellStdout, "powershell.exe").FirstOrDefault(path => !IsStorePowerShell(path));
        return new WhereChoice(powershell, false);
    }

    public static IReadOnlyList<string> ShellPaths(string? stdout, string fileName)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return [];
        var paths = new List<string>();
        foreach (var raw in stdout.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim().Trim('"');
            if (!Path.IsPathRooted(line)) continue;
            if (!Path.GetFileName(line).Equals(fileName, StringComparison.OrdinalIgnoreCase)) continue;
            paths.Add(line);
        }
        return paths;
    }

    private static string[] SplitPath(string path) =>
        path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
