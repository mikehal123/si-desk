using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sovereign.Desktop.Core.Support;

public sealed record SupportExportResult(
    string ZipPath,
    IReadOnlyList<string> IncludedRelativePaths,
    IReadOnlyList<string> ExcludedReasons,
    bool ContainsPlantedSecretMarker)
{
    public bool RefusedSecretLikeContent { get; init; }
}

public static class SupportExporter
{
    public const int MaximumFileBytes = 2 * 1024 * 1024;
    public const int MaximumContentBytes = 16 * 1024 * 1024;
    public const int MaximumDraftFiles = 100;
    public static readonly string[] AllowlistedRelativePaths =
    [
        "app-version.json",
        "preferences.json",
        "last-client-errors.json",
        "connection-state.json",
        "native-recovery.json"
    ];

    private static readonly Regex SecretMarker = new(
        "API_KEY|SECRET_MARKER|BEGIN (?:[A-Z]+ )?PRIVATE KEY|(?<![a-z0-9])(?:sk_live_|sk-[a-z0-9_-]{8,}|xoxb-)|key_b64|authenticator|HMAC-SHA-256|(?:Bearer|Basic)\\s+\\S+|(?:api[ _-]?key|password|client[ _-]?secret|access[ _-]?token)\\s*[:=]\\s*\\S+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] DeniedFileNames =
    [
        "endpoint-v2.json",
        "host-v2.db",
        "session-authenticator.json"
    ];

    public static SupportExportResult Export(string profileDirectory, string destinationZip, string appVersion, string modeLabel,
        bool includePrivateDrafts = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        if (SecretMarker.IsMatch(appVersion) || SecretMarker.IsMatch(modeLabel))
            throw new IOException("Support metadata contains secret-like text and cannot be exported.");
        var root = Path.GetFullPath(profileDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(root) || HasReparseAncestor(root))
            throw new IOException("Support export requires a real profile directory, not a linked path.");
        var destination = Path.GetFullPath(destinationZip);
        if (!string.Equals(Path.GetExtension(destination), ".zip", StringComparison.OrdinalIgnoreCase)
            || IsReparse(destination))
            throw new IOException("Choose an ordinary .zip file for the support export.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".support-" + Guid.NewGuid().ToString("N") + ".tmp");
        var included = new List<string>();
        var excluded = new List<string>();
        var planted = false;
        var totalBytes = 0;
        try
        {
          using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
          {
            AddText(zip, "export-manifest.json", JsonSerializer.Serialize(new
            {
                schema_version = "desktop-support-export/v1",
                app_version = appVersion,
                mode = modeLabel,
                private_drafts_requested = includePrivateDrafts,
                maximum_file_bytes = MaximumFileBytes,
                maximum_content_bytes = MaximumContentBytes,
                note = "Metadata by default. Private drafts require the explicit include option. Credentials, env files, source documents, databases, linked paths and oversized files are excluded."
            }, JsonUtil.Options));
            included.Add("export-manifest.json");

            void AddProfileFile(string rel)
            {
                var full = Path.GetFullPath(Path.Combine(root, rel));
                if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || HasReparseAncestor(full))
                {
                    excluded.Add(rel + ": linked path or path outside profile refused");
                    return;
                }
                if (!File.Exists(full))
                {
                    excluded.Add(rel + ": not present");
                    return;
                }
                if (DeniedFileNames.Contains(Path.GetFileName(full), StringComparer.OrdinalIgnoreCase))
                {
                    excluded.Add(rel + ": host authenticator/secret file refused");
                    return;
                }
                byte[] bytes;
                try
                {
                    // Read once while writes/deletes are denied. The same inspected bytes enter the ZIP.
                    using var source = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (HasReparseAncestor(full))
                    {
                        excluded.Add(rel + ": linked path refused");
                        return;
                    }
                    if (source.Length > MaximumFileBytes || source.Length > MaximumContentBytes - totalBytes)
                    {
                        excluded.Add(rel + ": support size limit exceeded");
                        return;
                    }
                    bytes = new byte[checked((int)source.Length)];
                    source.ReadExactly(bytes);
                    var text = new UTF8Encoding(false, true).GetString(bytes);
                    using var document = JsonDocument.Parse(text.TrimStart('\uFEFF'));
                    if (SecretMarker.IsMatch(text) || HasSecretProperty(document.RootElement))
                    {
                        planted = true;
                        excluded.Add(rel + ": credential or secret-like content refused");
                        return;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or DecoderFallbackException)
                {
                    excluded.Add(rel + ": unreadable or invalid JSON refused");
                    return;
                }
                var name = rel.Replace('\\', '/');
                using (var stream = zip.CreateEntry(name).Open()) stream.Write(bytes);
                totalBytes += bytes.Length;
                included.Add(name);
            }

            foreach (var rel in AllowlistedRelativePaths) AddProfileFile(rel);
            var draftsDir = Path.Combine(root, "drafts");
            if (includePrivateDrafts && Directory.Exists(draftsDir) && !HasReparseAncestor(draftsDir))
            {
                var count = 0;
                foreach (var file in Directory.EnumerateFiles(draftsDir, "*.json", SearchOption.TopDirectoryOnly))
                {
                    if (++count > MaximumDraftFiles)
                    {
                        excluded.Add("drafts/: file count limit reached; remaining drafts excluded");
                        break;
                    }
                    AddProfileFile(Path.GetRelativePath(root, file));
                }
            }
            else if (Directory.Exists(draftsDir))
                excluded.Add("drafts/: private content not requested or linked directory refused");

            // Never traverse unrelated case/chat/source directories merely to describe exclusions.
            foreach (var extra in Directory.EnumerateFileSystemEntries(root).Take(128))
            {
                var rel = Path.GetFileName(extra);
                if (rel.Equals("drafts", StringComparison.OrdinalIgnoreCase)
                    || AllowlistedRelativePaths.Contains(rel, StringComparer.OrdinalIgnoreCase)
                    || extra.Equals(destination, StringComparison.OrdinalIgnoreCase)
                    || extra.Equals(temporary, StringComparison.OrdinalIgnoreCase)) continue;
                excluded.Add(rel + ": outside allowlist; contents not read");
            }
          }
          if (IsReparse(destination)) throw new IOException("The export destination became a linked file.");
          File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        // A rejected source is not a secret included in the resulting archive.
        return new SupportExportResult(destinationZip, included, excluded, false)
        { RefusedSecretLikeContent = planted };
    }

    private static bool HasSecretProperty(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                var name = Regex.Replace(property.Name, "[^a-zA-Z0-9]", "").ToLowerInvariant();
                if (name is "password" or "secret" or "clientsecret" or "accesstoken" or "refreshtoken"
                    or "idtoken" or "authorization" or "authenticator" or "keyb64" or "credentials"
                    or "privatekey" or "token" or "accesskey" || name.EndsWith("apikey", StringComparison.Ordinal)) return true;
                if (HasSecretProperty(property.Value)) return true;
            }
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) if (HasSecretProperty(item)) return true;
        return false;
    }

    private static bool IsReparse(string path) =>
        (File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool HasReparseAncestor(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (IsReparse(current)) return true;
        return false;
    }

    private static void AddText(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.Write(text);
    }
}

internal static class JsonUtil
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };
}
