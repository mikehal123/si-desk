namespace Sovereign.Desktop.Connector;

/// <summary>Remembers whether the Swarm activity feed is open. A missing file means collapsed.</summary>
public static class SwarmActivityFold
{
    public static bool Expanded(string? stored) =>
        string.Equals(stored?.Trim(), "open", StringComparison.Ordinal);

    public static string Preview(IEnumerable<string>? lines)
    {
        if (lines is null) return "";
        var preview = "";
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var text = line.ReplaceLineEndings(" ").Trim();
            if (text.Length == 0) continue;
            preview = text;
        }
        return preview;
    }

    public static void Remember(string profileDirectory, string conversationId, bool open)
    {
        var path = FileFor(profileDirectory, conversationId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, open ? "open" : "closed");
    }

    public static bool Load(string profileDirectory, string conversationId)
    {
        var path = FileFor(profileDirectory, conversationId);
        if (!File.Exists(path)) return false;
        return Expanded(File.ReadAllText(path));
    }

    private static string FileFor(string profileDirectory, string conversationId)
    {
        if (string.IsNullOrWhiteSpace(profileDirectory))
            throw new ArgumentException("Profile directory is required.", nameof(profileDirectory));
        if (!IsConversationId(conversationId))
            throw new ArgumentException("Conversation id must be a guid.", nameof(conversationId));

        var root = Path.GetFullPath(Path.Combine(profileDirectory, "swarm-activity"));
        var path = Path.GetFullPath(Path.Combine(root, conversationId));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(prefix, comparison)
            || !string.Equals(Path.GetFileName(path), conversationId, comparison))
            throw new ArgumentException("Conversation id must stay inside swarm-activity.", nameof(conversationId));
        return path;
    }

    private static bool IsConversationId(string? conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId)) return false;
        if (conversationId.IndexOfAny(['\\', '/', ':']) >= 0) return false;
        return Guid.TryParseExact(conversationId, "N", out _)
            || Guid.TryParseExact(conversationId, "D", out _);
    }
}
