using System.Security.Cryptography;
using System.Text.Json;

namespace Sovereign.Desktop.Core;

public sealed record EngineVerifyResult(bool Ok, string Message, int Checked, IReadOnlyList<string> Problems)
{
    public static EngineVerifyResult Pass(int checkedCount, string message) =>
        new(true, message, checkedCount, []);

    public static EngineVerifyResult Fail(string message, IReadOnlyList<string>? problems = null, int checkedCount = 0) =>
        new(false, message, checkedCount, problems ?? []);
}

/// <summary>
/// Hash-checks an extracted engine tree against <c>bundle-manifest.json</c>.
/// Does not start Python and does not read development staging paths.
/// </summary>
public static class EngineBundleVerifier
{
    public const string ManifestFileName = "bundle-manifest.json";
    public const string RelativeDirectory = "engine-bundle";
    public const string PythonRelative = "python/python.exe";
    public const string HostEntryRelative = "native/engine/host_entry.py";
    public const string StartScript = "Start-SovereignHost.ps1";
    public const string StopScript = "Stop-SovereignHost.ps1";

    public const string IndependentQaWorkspace = "";
    public const string IndependentQaInstalls = "";

    public static readonly string[] ForbiddenEngineRoots = [];

    public static bool IsForbiddenEngineRoot(string fullPath, out string reason)
    {
        var full = Path.GetFullPath(fullPath).TrimEnd('\\') + "\\";
        if (!string.IsNullOrWhiteSpace(IndependentQaWorkspace)
            && Under(full, IndependentQaWorkspace)
            && !Under(full, IndependentQaInstalls))
        {
            reason = "ENGINE_STAGING_REFUSED: " + IndependentQaWorkspace +
                     " staging/extract trees are not used. A hash-verified install under installs\\ is allowed.";
            return true;
        }

        foreach (var root in ForbiddenEngineRoots)
        {
            if (Under(full, root))
            {
                reason = "ENGINE_STAGING_REFUSED: " + root;
                return true;
            }
        }

        reason = "";
        return false;
    }

    public static bool Under(string fullDirectoryWithSlash, string root)
    {
        var prefix = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        return fullDirectoryWithSlash.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static EngineVerifyResult Verify(string engineRoot)
    {
        if (string.IsNullOrWhiteSpace(engineRoot) || !Directory.Exists(engineRoot))
        {
            return EngineVerifyResult.Fail(
                "ENGINE_MISSING: installed engine directory is absent. A development or QA staging engine is not used. Preview fixtures are not substituted.");
        }

        var root = Path.GetFullPath(engineRoot);
        if (IsForbiddenEngineRoot(root, out var forbidden))
        {
            return EngineVerifyResult.Fail(forbidden + " The desktop will not fall back to a development engine.");
        }

        var manifestPath = Path.Combine(root, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return EngineVerifyResult.Fail(
                "ENGINE_CORRUPT: bundle-manifest.json is missing from the installed engine. Preview fixtures are not substituted.");
        }

        List<ManifestFile> files;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!doc.RootElement.TryGetProperty("files", out var array) || array.ValueKind != JsonValueKind.Array)
            {
                return EngineVerifyResult.Fail("ENGINE_CORRUPT: bundle-manifest.json has no files array.");
            }

            files = [];
            foreach (var item in array.EnumerateArray())
            {
                var rel = item.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
                var hash = item.TryGetProperty("sha256", out var h) ? h.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(rel) || hash.Length != 64)
                {
                    return EngineVerifyResult.Fail("ENGINE_CORRUPT: bundle-manifest.json entry is missing path or sha256.");
                }

                files.Add(new ManifestFile(rel.Replace('/', Path.DirectorySeparatorChar), hash));
            }
        }
        catch (Exception ex)
        {
            return EngineVerifyResult.Fail("ENGINE_CORRUPT: bundle-manifest.json unreadable: " + ex.Message);
        }

        if (files.Count == 0)
        {
            return EngineVerifyResult.Fail("ENGINE_CORRUPT: bundle-manifest.json lists no files.");
        }

        var problems = new List<string>();
        var checkedCount = 0;
        foreach (var file in files)
        {
            var dest = Path.Combine(root, file.Relative);
            if (!File.Exists(dest))
            {
                problems.Add("MISSING " + file.Relative.Replace(Path.DirectorySeparatorChar, '/'));
                continue;
            }

            var actual = HashFile(dest);
            checkedCount++;
            if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add("MISMATCH " + file.Relative.Replace(Path.DirectorySeparatorChar, '/'));
            }
        }

        var python = Path.Combine(root, PythonRelative.Replace('/', Path.DirectorySeparatorChar));
        var entry = Path.Combine(root, HostEntryRelative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(python))
        {
            problems.Add("MISSING python/python.exe");
        }

        if (!File.Exists(entry))
        {
            problems.Add("MISSING native/engine/host_entry.py");
        }

        if (problems.Count > 0)
        {
            return EngineVerifyResult.Fail(
                "ENGINE_CORRUPT: " + problems.Count + " installed engine file problem(s). A development engine is not used. Preview fixtures are not substituted.",
                problems,
                checkedCount);
        }

        return EngineVerifyResult.Pass(checkedCount, "ENGINE_OK files=" + checkedCount);
    }

    public static string HashFile(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    private sealed record ManifestFile(string Relative, string Sha256);
}
