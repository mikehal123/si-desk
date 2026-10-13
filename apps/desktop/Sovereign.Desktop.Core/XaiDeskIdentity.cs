namespace Sovereign.Desktop.Core;

/// <summary>Identity for SI Desk. The class name stays so existing call sites keep compiling.</summary>
public static class XaiDeskIdentity
{
    public const string AppName = "SI Desk";
    public const string ExeName = "SIDesk";
    public const string AppUserModelId = "SIDesk";
    public const string DataFolderName = "SIDesk";
    public const string LegacyDataFolderName = "xAIDesk";
    public const string SingleInstanceMutex = @"Local\SIDesk.SingleInstance";
    public const string OpenFileMutexPrefix = @"Local\SIDesk.SingleInstance.";
    public const string OpenFilePipePrefix = "SIDesk.SingleInstance.";
    public const string CredentialPrefix = "SIDesk";
    public const string LegacyCredentialPrefix = "xAIDesk";
    public const string XaiApiCredentialResource = "SIDesk.XaiApi";
    public const string LegacyXaiApiCredentialResource = "xAIDesk.XaiApi";
    public const string GrokBotCredentialResource = "SIDesk.GrokBotConnector";
    public const string LegacyGrokBotCredentialResource = "xAIDesk.GrokBotConnector";
    public const string XaiApiCredentialAccount = "default";
    public const string GrokBotCredentialAccount = "token";
    public const int GrokBotPort = 8790;
    public const int PhonePort = 8789;
    public const bool UpdaterEnabled = false;
    public static readonly string[] Navigation = ["chat", "room", "grokbot", "settings"];

    public static string DataDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DataFolderName);

    /// <summary>
    /// When the new profile folder is missing and the previous xAIDesk folder exists, copy it.
    /// The previous folder is left in place. An existing SIDesk folder is not changed.
    /// </summary>
    public static void MigrateLegacyProfile()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root)) return;
        CopyProfileIfMissing(Path.Combine(root, LegacyDataFolderName), Path.Combine(root, DataFolderName));
        CopyProfileIfMissing(
            Path.Combine(root, "xai-desk", "swarm"),
            Path.Combine(root, DataFolderName, "swarm"));
    }

    public static void CopyProfileIfMissing(string source, string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var sourceFull = Path.GetFullPath(source);
        var destinationFull = Path.GetFullPath(destination);
        if (SamePath(sourceFull, destinationFull)) return;
        if (Directory.Exists(destinationFull) || !Directory.Exists(sourceFull)) return;
        var staging = destinationFull + ".migrating-" + Guid.NewGuid().ToString("N");
        try
        {
            CopyDirectory(sourceFull, staging);
            if (Directory.Exists(destinationFull))
            {
                Directory.Delete(staging, recursive: true);
                return;
            }

            Directory.Move(staging, destinationFull);
        }
        catch
        {
            if (Directory.Exists(staging))
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (IOException) { }
            }

            throw;
        }
    }

    /// <summary>The previous default profile when <paramref name="profileDirectory"/> is the new default. Custom profiles are unchanged.</summary>
    public static string? LegacyDefaultProfile(string profileDirectory)
    {
        if (string.IsNullOrWhiteSpace(profileDirectory)) return null;
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root)) return null;
        var current = Path.Combine(root, DataFolderName);
        if (!SamePath(profileDirectory, current)) return null;
        return Path.GetFullPath(Path.Combine(root, LegacyDataFolderName));
    }

    /// <summary>New Credential Manager resource first, then the previous xAIDesk resource when the prefix matches.</summary>
    public static IReadOnlyList<string> CredentialResourceNames(string resource)
    {
        if (string.IsNullOrEmpty(resource)
            || !resource.StartsWith(CredentialPrefix, StringComparison.Ordinal))
            return [resource];
        var legacy = LegacyCredentialPrefix + resource[CredentialPrefix.Length..];
        return string.Equals(legacy, resource, StringComparison.Ordinal) ? [resource] : [resource, legacy];
    }

    public static bool IsGrokModel(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Contains("grok", StringComparison.OrdinalIgnoreCase);

    /// <summary>Grok Build and the xAI API may only offer Grok or xAI models.</summary>
    public static bool IsDeskModel(string? id, string? name = null)
    {
        if (OtherVendor(id) || OtherVendor(name)) return false;
        return IsGrokModel(id) || IsGrokModel(name) || MentionsXai(id) || MentionsXai(name);
    }

    private static bool MentionsXai(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains("xai", StringComparison.OrdinalIgnoreCase);

    private static bool OtherVendor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        foreach (var token in new[] { "claude", "gemini", "gpt", "openai", "ollama", "kimi", "glm", "composer", "sonnet", "opus" })
            if (value.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static string NavigationLabel(string tag) => tag switch
    {
        "chat" => "Chat",
        "room" => "Shared room",
        "grokbot" => "Grok Bot",
        "settings" => "Settings",
        _ => "Settings"
    };

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var name = Path.GetFileName(entry);
            if (string.IsNullOrEmpty(name)) continue;
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            var target = Path.Combine(destination, name);
            if (attributes.HasFlag(FileAttributes.Directory))
                CopyDirectory(entry, target);
            else
                File.Copy(entry, target, overwrite: false);
        }
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
