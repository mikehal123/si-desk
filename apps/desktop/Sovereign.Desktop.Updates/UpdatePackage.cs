using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sovereign.Desktop.Updates;

public sealed record UpdateRelease(string Version, string Url, string Sha256, long Bytes, string Notes, DateTimeOffset ExpiresUtc);
public sealed record SignedUpdate(string Payload, string Signature);

public static class UpdatePackage
{
    public const long MaxPackageBytes = 1_000_000_000;
    public static Version ParseVersion(string value) => Regex.IsMatch(value, @"^\d+\.\d+\.\d+$")
        ? Version.Parse(value) : throw new InvalidDataException("Invalid release version.");

    public static UpdateRelease Verify(string envelope, string publicKey, DateTimeOffset now)
    {
        if (envelope.Length > 64_000) throw new InvalidDataException("Update information is too large.");
        var signed = JsonSerializer.Deserialize<SignedUpdate>(envelope) ?? throw new InvalidDataException("Missing update information.");
        var bytes = Convert.FromBase64String(signed.Payload);
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        if (!rsa.VerifyData(bytes, Convert.FromBase64String(signed.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new InvalidDataException("Update signature is invalid. Nothing was installed.");
        var release = JsonSerializer.Deserialize<UpdateRelease>(bytes) ?? throw new InvalidDataException("Missing release.");
        _ = ParseVersion(release.Version);
        if (release.ExpiresUtc <= now || release.Bytes <= 0 || release.Bytes > MaxPackageBytes ||
            !Regex.IsMatch(release.Sha256, "^[a-fA-F0-9]{64}$") || release.Notes.Length > 8000)
            throw new InvalidDataException("Update information is expired or invalid.");
        var url = new Uri(release.Url);
        if (url.Scheme != "https" || url.Host != "github.com" || !url.IsDefaultPort || url.UserInfo.Length != 0 ||
            !url.AbsolutePath.StartsWith("/mikehal123/si-desk/releases/download/", StringComparison.Ordinal))
            throw new InvalidDataException("Unexpected update download location.");
        return release;
    }

    public static async Task VerifyArchiveAsync(string path, UpdateRelease release, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length != release.Bytes || !Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded update failed verification. Nothing was installed.");
    }

    public static void Extract(string archive, string destination, string version)
    {
        if (Directory.Exists(destination)) throw new IOException("Update extraction directory already exists.");
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > 30_000) throw new InvalidDataException("Too many archive entries.");
        long total = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<(ZipArchiveEntry Entry, string Path)>();
        var prefix = "SIDesk-Windows-" + version + "/";
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Unexpected package root.");
            var relative = name[prefix.Length..];
            if (relative.Length == 0) continue;
            if (relative.Contains(':') || relative.Split('/').Any(s => s is "." or ".." || s.EndsWith(' ') || s.EndsWith('.')) ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("Unsafe archive path.");
            var target = Path.GetFullPath(Path.Combine(root, relative));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !names.Add(target)) throw new InvalidDataException("Invalid or duplicate archive path.");
            total = checked(total + entry.Length);
            if (total > 4_000_000_000) throw new InvalidDataException("Extracted update is too large.");
            entries.Add((entry, target));
        }
        if (!entries.Any(e => e.Path == Path.Combine(destination, "SovereignAI.ps1"))) throw new InvalidDataException("Installer missing.");
        // Extraction plus installation copy coexist; the previous app is retained in place.
        DeskUpdateSafety.RequireDiskSpace(destination, checked(total * 2 + 64 * 1024 * 1024));
        Directory.CreateDirectory(destination);
        foreach (var (entry, path) in entries)
        {
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, overwrite: false);
        }
    }
}
