using System.Security.Cryptography;
using System.Text.Json;

namespace Sovereign.Desktop.Core.Archive;

public sealed record ArchiveFileEntry(string RelativePath, string Sha256, long SizeBytes);

public sealed record ArchiveManifest(
    string SchemaVersion,
    string PacketType,
    string ObservationStart,
    string ObservationEnd,
    IReadOnlyList<ArchiveFileEntry> DeclaredFiles,
    IReadOnlyList<string> ResultReferences);

public sealed record ArchiveLoadResult(
    ArchiveTrust Trust,
    string Reason,
    ArchiveManifest? Manifest,
    IReadOnlyDictionary<string, string> Texts,
    IReadOnlyList<string> RefusedPaths);

public static class ArchiveLimits
{
    public const string Schema = "desktop-archive/v1";
    public const int MaxFiles = 256;
    public const long MaxTotalBytes = 32L * 1024 * 1024;
    public const int MaxDepth = 4;
    public const int MaxFileBytes = 4 * 1024 * 1024;
}

public static class ArchiveReader
{
    public static ArchiveLoadResult OpenDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return Fail(ArchiveTrust.Invalid, "Directory does not exist.");
        }

        if (IsReparsePoint(directory))
        {
            return Fail(ArchiveTrust.Invalid, "Packet root is a reparse point.");
        }

        var manifestPath = Path.Combine(directory, "packet.json");
        if (!File.Exists(manifestPath))
        {
            return Fail(ArchiveTrust.Unsupported, "packet.json is missing.");
        }

        ArchiveManifestDto dto;
        try
        {
            dto = JsonSerializer.Deserialize<ArchiveManifestDto>(File.ReadAllText(manifestPath), JsonUtil.Options)
                  ?? throw new InvalidOperationException("null manifest");
        }
        catch (Exception ex)
        {
            return Fail(ArchiveTrust.Invalid, "packet.json is not valid JSON: " + ex.Message);
        }

        if (dto.SchemaVersion != ArchiveLimits.Schema)
        {
            return Fail(ArchiveTrust.Unsupported, $"Unsupported schema '{dto.SchemaVersion}'.");
        }

        if (dto.PacketType != "historical-evidence")
        {
            return Fail(ArchiveTrust.Unsupported, $"Unsupported packet_type '{dto.PacketType}'.");
        }

        if (dto.DeclaredFiles is null || dto.DeclaredFiles.Count == 0)
        {
            return Fail(ArchiveTrust.Invalid, "declared_files is required.");
        }

        if (dto.DeclaredFiles.Count > ArchiveLimits.MaxFiles)
        {
            return Fail(ArchiveTrust.Invalid, "Too many declared files.");
        }

        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var hashesMatch = true;
        long total = 0;
        foreach (var entry in dto.DeclaredFiles)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Path) || string.IsNullOrWhiteSpace(entry.Sha256))
            {
                return Fail(ArchiveTrust.Invalid, "Each declared file needs path and sha256.");
            }

            if (!IsSafeRelative(entry.Path, out var safeRel, out var why))
            {
                return Fail(ArchiveTrust.Invalid, why);
            }

            var full = Path.GetFullPath(Path.Combine(directory, safeRel));
            var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return Fail(ArchiveTrust.Invalid, "Path escaped the packet root: " + entry.Path);
            }

            if (!File.Exists(full))
            {
                return Fail(ArchiveTrust.Invalid, "Declared file missing: " + safeRel);
            }

            if (IsReparsePoint(full) || IsReparsePoint(Path.GetDirectoryName(full)!))
            {
                return Fail(ArchiveTrust.Invalid, "Reparse point inside packet: " + safeRel);
            }

            var info = new FileInfo(full);
            if (info.Length > ArchiveLimits.MaxFileBytes)
            {
                return Fail(ArchiveTrust.Invalid, "File exceeds size limit: " + safeRel);
            }

            total += info.Length;
            if (total > ArchiveLimits.MaxTotalBytes)
            {
                return Fail(ArchiveTrust.Invalid, "Packet exceeds total size limit.");
            }

            var bytes = File.ReadAllBytes(full);
            var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(digest, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                hashesMatch = false;
                return Fail(ArchiveTrust.Invalid, $"Hash mismatch for {safeRel}.");
            }

            if (LooksLikeText(safeRel))
            {
                texts[safeRel] = System.Text.Encoding.UTF8.GetString(bytes);
            }
        }

        var manifest = new ArchiveManifest(
            dto.SchemaVersion!,
            dto.PacketType!,
            dto.ObservationWindow?.Start ?? "unspecified",
            dto.ObservationWindow?.End ?? "unspecified",
            dto.DeclaredFiles.Select(e => new ArchiveFileEntry(e!.Path!, e.Sha256!, new FileInfo(Path.Combine(directory, e.Path!)).Length)).ToArray(),
            dto.ResultReferences?.ToArray() ?? Array.Empty<string>());

        var trust = hashesMatch ? ArchiveTrust.HashConsistent : ArchiveTrust.Unverified;
        return new ArchiveLoadResult(trust, hashesMatch ? "Declared digests match file bytes. Authorship is not established." : "Hashes not fully verified.", manifest, texts, Array.Empty<string>());
    }

    public static bool IsSafeRelative(string path, out string normalized, out string reason)
    {
        normalized = path.Replace('\\', '/').TrimStart('/');
        reason = "";
        if (Path.IsPathRooted(path) || normalized.Contains(':', StringComparison.Ordinal))
        {
            reason = "Absolute or drive-qualified paths are refused.";
            return false;
        }

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Length > ArchiveLimits.MaxDepth)
        {
            reason = "Path depth is outside the allowed range.";
            return false;
        }

        foreach (var part in parts)
        {
            if (part is "." or ".." || part.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                reason = "Traversal or shortcut names are refused.";
                return false;
            }
        }

        normalized = string.Join(Path.DirectorySeparatorChar, parts);
        return true;
    }

    private static bool LooksLikeText(string relative) =>
        relative.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
        || relative.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
        || relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
        || relative.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);

    private static bool IsReparsePoint(string path)
    {
        try
        {
            var attr = File.GetAttributes(path);
            return attr.HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return false;
        }
    }

    private static ArchiveLoadResult Fail(ArchiveTrust trust, string reason) =>
        new(trust, reason, null, new Dictionary<string, string>(), Array.Empty<string>());

    private sealed class ArchiveManifestDto
    {
        public string? SchemaVersion { get; set; }
        public string? PacketType { get; set; }
        public ObservationDto? ObservationWindow { get; set; }
        public List<FileDto?>? DeclaredFiles { get; set; }
        public List<string>? ResultReferences { get; set; }
    }

    private sealed class ObservationDto
    {
        public string? Start { get; set; }
        public string? End { get; set; }
    }

    private sealed class FileDto
    {
        public string? Path { get; set; }
        public string? Sha256 { get; set; }
    }
}

internal static class JsonUtil
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        AllowTrailingCommas = false
    };
}
