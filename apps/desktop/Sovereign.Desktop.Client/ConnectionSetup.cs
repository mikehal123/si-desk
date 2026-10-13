namespace Sovereign.Desktop.Client;

/// <summary>Non-secret, per-Windows-user connector configuration, outside the install tree.</summary>
public static class ConnectionSetup
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SIDesk", "Connections");
    public static void SaveCodexPath(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path) || !Path.GetFileName(path).Equals("codex.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select the official codex.exe executable.");
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, "codex-path.txt"), path);
    }
    public static string FindCodex()
    {
        string? configured = null;
        try { configured = File.ReadAllText(Path.Combine(Root, "codex-path.txt")).Trim(); } catch (IOException) { }
        return FindCodex(configured, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetEnvironmentVariable("PATH") ?? "");
    }
    internal static string FindCodex(string? configured, string local, string roaming, string path)
    {
        var roots = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim().Trim('"'))
            .Concat(new[] { Path.Combine(roaming, "npm") }).Where(Path.IsPathFullyQualified).Distinct(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured)) candidates.Add(configured);
        candidates.Add(Path.Combine(local, "Programs", "OpenAI", "Codex", "bin", "codex.exe"));
        foreach (var root in roots)
        {
            candidates.Add(Path.Combine(root, "codex.exe"));
            foreach (var platform in new[] { "x64", "arm64" })
            {
                var triple = platform == "x64" ? "x86_64-pc-windows-msvc" : "aarch64-pc-windows-msvc";
                foreach (var package in new[] { Path.Combine("@openai", "codex"), Path.Combine("@openai", "codex-win32-" + platform), Path.Combine("@openai", "codex", "node_modules", "@openai", "codex-win32-" + platform) })
                    candidates.Add(Path.Combine(root, "node_modules", package, "vendor", triple, "codex", "codex.exe"));
            }
        }
        return candidates.FirstOrDefault(p => File.Exists(p) && Path.GetFileName(p).Equals("codex.exe", StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException("Codex executable not found. Open Settings → Connections to install it or locate an existing official codex.exe.");
    }
    public static string Endpoint(string provider)
    {
        var allowed = CodingPlanChatClient.Endpoints(provider);
        try { var value = File.ReadAllText(Path.Combine(Root, provider + "-endpoint.txt")).Trim(); if (allowed.Contains(value)) return value; } catch (IOException) { }
        return allowed[0];
    }
    public static void SaveEndpoint(string provider, string endpoint)
    {
        if (!CodingPlanChatClient.Endpoints(provider).Contains(endpoint)) throw new ArgumentException("Unsupported endpoint.");
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, provider + "-endpoint.txt"), endpoint);
    }
}
