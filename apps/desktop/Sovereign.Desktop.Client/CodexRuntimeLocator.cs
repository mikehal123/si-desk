namespace Sovereign.Desktop.Client;

/// <summary>Uses the current desktop application's deployed CLI before legacy aliases.</summary>
public static class CodexRuntimeLocator
{
    public static string FindExecutable()
    {
        string? configured = null;
        try { configured = File.ReadAllText(Path.Combine(ConnectionSetup.Root, "codex-path.txt")).Trim(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return FindExecutable(configured, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ConnectionSetup.FindCodex);
    }

    internal static string FindExecutable(string? configured, string local, Func<string> fallback)
    {
        if (Valid(configured)) return Path.GetFullPath(configured!);
        var deployments = Path.Combine(local, "OpenAI", "Codex", "bin");
        try
        {
            if (Directory.Exists(deployments))
            {
                // Desktop runtime folders use content identities rather than version names.
                // Ignore incomplete/stale deployments and prefer the most recently installed binary.
                var current = Directory.EnumerateDirectories(deployments)
                    .Select(directory => Path.Combine(directory, "codex.exe"))
                    .Where(Valid)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (current is not null) return Path.GetFullPath(current);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return fallback();
    }

    private static bool Valid(string? path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
        && Path.GetFileName(path).Equals("codex.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path);
}
