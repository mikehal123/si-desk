using System.Security.Cryptography;
using System.Text.Json;

namespace Sovereign.Desktop.Updates;

public sealed record DeskUpdateSnapshot(string TargetBuildId, string ManifestPath, string ManifestSha256,
    int SchemaVersion, long WriteGeneration);

/// <summary>Verifies the worker's SQLite and artifact snapshot. No network or database writes.</summary>
public static class DeskUpdateSafety
{
    public static async Task CheckWorkerAsync(Func<string, JsonElement, CancellationToken, Task<JsonElement>> call,
        Guid nonce, CancellationToken cancellation)
    {
        var parameters = JsonSerializer.SerializeToElement(new { session_nonce = nonce });
        // Ordinary health deliberately reports degraded for historical uncertain dispatches.
        // A dedicated read distinguishes that history from corruption, and scans artifacts once.
        var readiness = Value(await call("store.update_readiness", parameters, cancellation).ConfigureAwait(false));
        var blockers = readiness.GetProperty("blockers").EnumerateArray().Select(item => item.GetString()).ToArray();
        var unknown = readiness.GetProperty("unknown_operations").GetInt32();
        if (readiness.GetProperty("status").GetString() != "ready" || blockers.Length != 0 ||
            readiness.GetProperty("store_schema_version").GetInt32() < 1 || unknown < 0 ||
            readiness.GetProperty("history_requires_review").GetBoolean() != (unknown > 0))
            throw new IOException("The lookup store cannot safely update: " +
                (blockers.Length == 0 ? "invalid readiness result" : string.Join(", ", blockers)) + ". Saved data was retained.");
    }

    public static async Task<DeskUpdateSnapshot> PrepareAsync(string profileDirectory, string targetBuildId, int schemaVersion,
        Guid nonce, Func<string, JsonElement, CancellationToken, Task<JsonElement>> call, CancellationToken cancellation)
    {
        await CheckWorkerAsync(call, nonce, cancellation).ConfigureAwait(false);
        var desk = Path.Combine(Path.GetFullPath(profileDirectory), "desk");
        if (Directory.Exists(desk))
        {
            long backupBytes = 0;
            foreach (var name in new[] { "desk.sqlite", "desk.sqlite-wal" })
                if (File.Exists(Path.Combine(desk, name))) backupBytes = checked(backupBytes + new FileInfo(Path.Combine(desk, name)).Length);
            var artifacts = Path.Combine(desk, "artifacts");
            if (Directory.Exists(artifacts)) backupBytes = checked(backupBytes + DirectoryBytes(artifacts));
            RequireDiskSpace(desk, checked(backupBytes + 64 * 1024 * 1024));
        }
        var result = Value(await call("store.prepare_update", JsonSerializer.SerializeToElement(new
        {
            session_nonce = nonce, target_build_id = targetBuildId, target_schema_version = schemaVersion,
            operation_id = Guid.NewGuid()
        }), cancellation).ConfigureAwait(false));
        if (result.GetProperty("target_build_id").GetString() != targetBuildId ||
            result.GetProperty("target_schema_version").GetInt32() != schemaVersion)
            throw new IOException("Update backup belongs to another target.");
        var digest = result.GetProperty("backup").GetProperty("sha256").GetString()!;
        RequireDigest(digest);
        var backups = Path.Combine(Path.GetFullPath(profileDirectory), "desk", "backups");
        CheckNoLinks(backups);
        foreach (var directory in Directory.EnumerateDirectories(backups, "update-*", SearchOption.TopDirectoryOnly))
        {
            cancellation.ThrowIfCancellationRequested();
            CheckNoLinks(directory);
            var manifestPath = Path.Combine(directory, "manifest.json");
            if (!File.Exists(manifestPath)) continue;
            CheckNoLinks(manifestPath);
            if (new FileInfo(manifestPath).Length > 16 * 1024 * 1024) continue;
            if (HashFile(manifestPath) != digest) continue;
            using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath, cancellation).ConfigureAwait(false));
            var root = manifest.RootElement;
            var snapshot = new DeskUpdateSnapshot(targetBuildId, manifestPath, digest,
                root.GetProperty("schema_version").GetInt32(), root.GetProperty("write_generation").GetInt64());
            VerifySnapshot(profileDirectory, targetBuildId, snapshot);
            return snapshot;
        }
        throw new IOException("The committed full update backup is missing. The app was not replaced.");
    }

    public static void VerifySnapshot(string profileDirectory, string targetBuildId, DeskUpdateSnapshot snapshot)
    {
        if (snapshot.TargetBuildId != targetBuildId || snapshot.SchemaVersion < 1 || snapshot.WriteGeneration < 0)
            throw new IOException("Update backup identity is invalid.");
        var backups = Path.Combine(Path.GetFullPath(profileDirectory), "desk", "backups");
        var path = Path.GetFullPath(snapshot.ManifestPath);
        var directory = Path.GetDirectoryName(path)!;
        if (!string.Equals(Path.GetDirectoryName(directory), backups, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(directory).StartsWith("update-", StringComparison.Ordinal) || Path.GetFileName(path) != "manifest.json")
            throw new IOException("Update backup is outside this lookup profile.");
        CheckNoLinks(path);
        RequireDigest(snapshot.ManifestSha256);
        if (new FileInfo(path).Length > 16 * 1024 * 1024 || HashFile(path) != snapshot.ManifestSha256)
            throw new IOException("Update backup manifest changed.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var manifest = document.RootElement;
        if (!string.Equals(Path.GetFullPath(manifest.GetProperty("directory").GetString()!), directory, StringComparison.OrdinalIgnoreCase) ||
            manifest.GetProperty("schema_version").GetInt32() != snapshot.SchemaVersion ||
            manifest.GetProperty("write_generation").GetInt64() != snapshot.WriteGeneration)
            throw new IOException("Update backup generation does not match its receipt.");
        var database = Path.Combine(directory, "desk.sqlite");
        CheckNoLinks(database);
        if (HashFile(database) != manifest.GetProperty("sqlite_sha256").GetString())
            throw new IOException("Update backup database changed.");
        foreach (var item in manifest.GetProperty("artifacts").EnumerateArray())
        {
            var relative = item.GetProperty("relative_path").GetString()!;
            var artifact = Path.GetFullPath(Path.Combine(directory, relative));
            var artifactRoot = Path.Combine(directory, "artifacts") + Path.DirectorySeparatorChar;
            if (!artifact.StartsWith(artifactRoot, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Update backup artifact path is invalid.");
            CheckNoLinks(artifact);
            if (new FileInfo(artifact).Length != item.GetProperty("bytes").GetInt64() || HashFile(artifact) != item.GetProperty("sha256").GetString())
                throw new IOException("Update backup artifact changed or is missing.");
        }
    }

    // Capture only while the old app and worker are stopped. Include WAL, artifacts and desktop
    // workflow journals. The shared-memory index and writer lock are not durable user data.
    public static string FingerprintClosedProfile(string profileDirectory)
    {
        var root = Path.Combine(Path.GetFullPath(profileDirectory), "desk");
        if (!Directory.Exists(root)) return "absent";
        CheckNoLinks(root);
        var entries = new List<(string Path, long Bytes, string Hash)>();
        Visit(root);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var item in entries.OrderBy(item => item.Path, StringComparer.Ordinal))
            hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(new { path = item.Path, bytes = item.Bytes, sha256 = item.Hash }));
        return Convert.ToHexStringLower(hash.GetHashAndReset());

        void Visit(string directory)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var name = Path.GetFileName(entry);
                if (directory == root && name == "backups") continue;
                CheckNoLinks(entry);
                if (Directory.Exists(entry)) { Visit(entry); continue; }
                if (name == "desk.sqlite-shm" || name.EndsWith(".lock", StringComparison.Ordinal)) continue;
                entries.Add((Path.GetRelativePath(root, entry).Replace('\\', '/'), new FileInfo(entry).Length, HashFile(entry)));
            }
        }
    }

    public static void RequireUnchangedProfile(string profileDirectory, string priorFingerprint)
    {
        if (!string.Equals(priorFingerprint, FingerprintClosedProfile(profileDirectory), StringComparison.Ordinal))
            throw new IOException("Lookup data changed after installation. Automatic rollback to the old binary was refused. " +
                "The latest profile and both app versions were preserved; do not open this profile with the previous app.");
    }

    public static void VerifyCompatibility(string packageDirectory, string version, int currentSchema)
    {
        var path = Path.Combine(packageDirectory, "desk-compatibility.json");
        CheckNoLinks(path);
        if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024)
            throw new IOException("The update has no bounded lookup compatibility manifest. Nothing was replaced.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        if (root.GetProperty("format").GetString() != "sovereign-desk-compatibility-v1" ||
            root.GetProperty("app_version").GetString() != version || root.GetProperty("protocol_version").GetInt32() != 1)
            throw new IOException("Lookup update protocol or version is incompatible.");
        var minimum = root.GetProperty("min_read_schema").GetInt32();
        var maximum = root.GetProperty("max_read_schema").GetInt32();
        var write = root.GetProperty("write_schema").GetInt32();
        if (minimum < 1 || maximum < minimum || write < minimum || write > maximum ||
            currentSchema < minimum || currentSchema > maximum || write < currentSchema)
            throw new IOException("The update cannot safely read and write this lookup schema. Nothing was replaced.");
    }

    public static void RequireDiskSpace(string directory, long requiredBytes)
    {
        var available = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace;
        RequireDiskSpace(available, requiredBytes);
    }

    public static void RequireDiskSpace(long availableBytes, long requiredBytes)
    {
        if (requiredBytes < 0 || availableBytes < requiredBytes)
            throw new IOException("There is not enough free disk space to stage the update and preserve a complete backup. Nothing was replaced.");
    }

    private static long DirectoryBytes(string directory)
    {
        CheckNoLinks(directory);
        long bytes = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            CheckNoLinks(entry);
            bytes = checked(bytes + (Directory.Exists(entry) ? DirectoryBytes(entry) : new FileInfo(entry).Length));
        }
        return bytes;
    }

    public static void CheckNoLinks(string path)
    {
        var full = Path.GetFullPath(path);
        if (File.Exists(full) && File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Update backup paths cannot be links.");
        for (var directory = new DirectoryInfo(Directory.Exists(full) ? full : Path.GetDirectoryName(full)!); directory is not null; directory = directory.Parent)
            if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Update backup paths cannot be links.");
    }

    private static JsonElement Value(JsonElement response)
    {
        if (response.TryGetProperty("value", out var value)) return value;
        var code = response.TryGetProperty("data", out var data) && data.TryGetProperty("code", out var reason)
            ? reason.GetString() : "OUTPUT_INVALID";
        throw new IOException("Lookup worker refused update preparation (" + code + "). Finish active lookup or review recovery status before updating.");
    }
    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
    private static void RequireDigest(string digest)
    {
        if (digest.Length != 64 || digest.Any(c => !char.IsAsciiHexDigit(c))) throw new IOException("Invalid backup digest.");
    }
}
