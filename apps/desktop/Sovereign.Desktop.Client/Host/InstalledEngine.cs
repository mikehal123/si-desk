using Sovereign.Desktop.Core;

namespace Sovereign.Desktop.Client.Host;

public sealed record InstalledEngine(
    string InstallDir,
    string EngineRoot,
    string PythonExe,
    string HostEntry,
    HostBundlePin Pin,
    EngineVerifyResult Verify);

public static class InstalledEngineResolver
{
    public static InstalledEngine? TryResolve(string installDir, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
        {
            reason = "ENGINE_MISSING: install directory is absent. A development engine is not used. Preview fixtures are not substituted.";
            return null;
        }

        var root = Path.GetFullPath(installDir);
        var pinPath = Path.Combine(root, "engine-bundle-pin.json");
        var pin = HostBundlePinFile.TryRead(pinPath, out var pinReason);
        var relative = pin?.EngineRelative;
        if (string.IsNullOrWhiteSpace(relative))
        {
            relative = HostBundlePinFile.EngineRelativeDefault;
        }

        if (relative.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            reason = "ENGINE_CORRUPT: engine_relative must be a directory inside the install tree.";
            return null;
        }

        var engineRoot = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = root.TrimEnd('\\') + "\\";
        if (!engineRoot.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(engineRoot.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            reason = "ENGINE_CORRUPT: resolved engine root escaped the install directory.";
            return null;
        }

        if (EngineBundleVerifier.IsForbiddenEngineRoot(engineRoot, out var forbidden))
        {
            reason = forbidden + " The desktop will not fall back to a development engine. Preview fixtures are not substituted.";
            return null;
        }

        if (pin is not null && !string.IsNullOrWhiteSpace(pin.StageRoot)
            && EngineBundleVerifier.IsForbiddenEngineRoot(pin.StageRoot, out _))
        {
            // Pin may still carry a leftover staging field; it is ignored, never used.
        }

        var verify = EngineBundleVerifier.Verify(engineRoot);
        if (!verify.Ok)
        {
            reason = verify.Message;
            return null;
        }

        if (pin is null)
        {
            reason = pinReason + " Engine files hashed, but engine-bundle-pin.json is required beside the application.";
            return null;
        }

        reason = verify.Message;
        return new InstalledEngine(
            root,
            engineRoot,
            Path.Combine(engineRoot, "python", "python.exe"),
            Path.Combine(engineRoot, "native", "engine", "host_entry.py"),
            pin,
            verify);
    }
}
