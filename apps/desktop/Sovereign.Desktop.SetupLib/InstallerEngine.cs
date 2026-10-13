using System.Text.Json;
using System.Text.Json.Nodes;
using Sovereign.Desktop.Core;

namespace Sovereign.Desktop.SetupLib;

public static class InstallerEngine
{
    public const string ProductionProductId = "xAIDesk";
    public const string ProductionProductName = "xAI Desk";
    public const string CurrentVersion = "0.3.3";
    public const int EngineVerifyFailedExitCode = 7;
    public const int HostShutdownFailedExitCode = InstalledHostShutdown.HostShutdownFailedExitCode;

    public static readonly string[] ForbiddenRoots = [];

    public static SetupResult Run(SetupRequest request, IWebView2PrerequisitePlatform? prerequisites = null, Action<string>? progress = null, IOpenWithStore? openWith = null)
    {
        var log = new List<string>();
        try
        {
            if (string.IsNullOrWhiteSpace(request.InstallDir))
            {
                return Fail(
                    SetupCli.MissingInstallDirExitCode,
                    "MISSING_REQUIRED_ARGUMENT: --install-dir is required. Default LocalAppData install is refused.",
                    log);
            }

            var installDir = Path.GetFullPath(request.InstallDir);
            log.Add("installDir=" + installDir);
            if (request.SentinelPath is not null)
            {
                log.Add("sentinel=" + Path.GetFullPath(request.SentinelPath));
            }

            if (IsForbidden(installDir, out var why))
            {
                return Fail(4, "REFUSED_FORBIDDEN_PATH: " + why, log);
            }

            if (LooksLikeGitCheckout(installDir))
            {
                return Fail(4, "REFUSED_GIT_CHECKOUT: installer will not target a source checkout.", log);
            }

            var sentinelBefore = ReadSentinel(request.SentinelPath);

            if (request.Action == SetupAction.Uninstall)
            {
                var shutdown = InstalledHostShutdown.Coordinate(installDir, request.ProfileDir, log);
                if (shutdown is not null)
                {
                    return shutdown;
                }

                var result = Uninstall(request, installDir, log, openWith);
                AssertSentinel(request.SentinelPath, sentinelBefore, log);
                return result;
            }

            if (string.IsNullOrWhiteSpace(request.PayloadDir) || !Directory.Exists(request.PayloadDir))
            {
                return Fail(2, "Payload directory not found: " + request.PayloadDir, log);
            }

            var payload = Path.GetFullPath(request.PayloadDir);
            if (IsForbidden(payload, out var payloadWhy))
            {
                return Fail(4, "REFUSED_PAYLOAD: " + payloadWhy, log);
            }

            if (request.Action is SetupAction.Repair or SetupAction.Upgrade && !Directory.Exists(installDir))
            {
                return Fail(5, request.Action + " requires an existing install directory.", log);
            }

            // Check before stopping the running app or replacing any app files. Uninstall
            // and non-WebView test payloads do not install prerequisites.
            if (File.Exists(Path.Combine(payload, "Microsoft.Web.WebView2.Core.dll")))
            {
                var prerequisite = WebView2Prerequisite.Ensure(prerequisites ?? new WindowsWebView2Prerequisite(), log, progress);
                if (prerequisite is not null) return prerequisite;
            }

            if (request.Action is SetupAction.Repair or SetupAction.Upgrade)
            {
                var shutdown = InstalledHostShutdown.Coordinate(installDir, request.ProfileDir, log);
                if (shutdown is not null)
                {
                    return shutdown;
                }
            }

            var staging = installDir + ".staging";
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            Directory.CreateDirectory(staging);
            var copied = 0;
            foreach (var file in Directory.GetFiles(payload, "*", SearchOption.AllDirectories))
            {
                if (request.FailAfterFileCount is int limit && copied >= limit)
                {
                    Directory.Delete(staging, recursive: true);
                    AssertSentinel(request.SentinelPath, sentinelBefore, log);
                    return Fail(3, "INCOMPLETE_INSTALL: interrupted after " + copied + " files. Staging discarded. Install directory left unchanged if it existed.", log);
                }

                var rel = Path.GetRelativePath(payload, file);
                var dest = Path.Combine(staging, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
                copied++;
            }

            Directory.CreateDirectory(installDir);
            foreach (var file in Directory.GetFiles(staging, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(staging, file);
                var dest = Path.Combine(installDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
            }

            Directory.Delete(staging, recursive: true);

            var engineCheck = VerifyPackagedEngine(payload, installDir, log);
            if (engineCheck is not null)
            {
                return engineCheck;
            }

            var previous = ReadReceipt(installDir);            var receipt = new Dictionary<string, object?>
            {
                ["product"] = request.ProductName,
                ["product_id"] = request.ProductId,
                ["version"] = request.Version,
                ["previous_version"] = previous?.Version,
                ["action"] = request.Action.ToString(),
                ["signed"] = false,
                ["install_dir"] = installDir,
                ["profile_dir"] = request.ProfileDir,
                ["note"] = "Unsigned internal preview. Distinct test instances use a separate product id. Engine bundle is pinned by hash; this installer does not register a native engine startup task."
            };
            File.WriteAllText(Path.Combine(installDir, "preview-setup-receipt.json"), JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
            RewriteEnginePin(installDir, request, log);

            WriteShortcut(request, installDir, log);
            WriteUninstallKey(request, installDir, log);
            ConfigureOpenWith(request, installDir, log, openWith, remove: false);
            AssertSentinel(request.SentinelPath, sentinelBefore, log);
            log.Add("UNSIGNED INTERNAL PREVIEW. Publisher signing is not claimed.");
            return new SetupResult(0, "Installed " + request.ProductName + " " + request.Version + " to " + installDir, log);
        }
        catch (Exception ex)
        {
            log.Add(ex.ToString());
            return new SetupResult(1, ex.Message, log);
        }
    }

    public static bool IsForbidden(string fullPath, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(fullPath) || ForbiddenRoots.Length == 0)
            return false;

        var full = Path.GetFullPath(fullPath).TrimEnd('\\') + "\\";
        foreach (var root in ForbiddenRoots)
        {
            var prefix = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                reason = "path is under protected tree " + root;
                return true;
            }
        }

        return false;
    }

    private static SetupResult? VerifyPackagedEngine(string payload, string installDir, List<string> log)
    {
        var payloadEngine = Path.Combine(payload, EngineBundleVerifier.RelativeDirectory);
        var pinPath = Path.Combine(installDir, "engine-bundle-pin.json");
        var pinRequiresEngine = File.Exists(pinPath) && File.ReadAllText(pinPath).Contains("engine_relative", StringComparison.Ordinal);
        if (!Directory.Exists(payloadEngine) && !pinRequiresEngine)
        {
            return null;
        }

        var installedEngine = Path.Combine(installDir, EngineBundleVerifier.RelativeDirectory);
        if (!Directory.Exists(installedEngine))
        {
            return Fail(
                EngineVerifyFailedExitCode,
                "ENGINE_MISSING: installer reported a payload that requires engine-bundle, but the installed tree has no engine files. A development engine is not used.",
                log);
        }

        var verify = EngineBundleVerifier.Verify(installedEngine);
        log.Add(verify.Message);
        foreach (var problem in verify.Problems.Take(20))
        {
            log.Add(problem);
        }

        if (!verify.Ok)
        {
            return Fail(EngineVerifyFailedExitCode, verify.Message, log);
        }

        log.Add("engine files verified " + verify.Checked);
        return null;
    }

    private static void RewriteEnginePin(string installDir, SetupRequest request, List<string> log)
    {
        var path = Path.Combine(installDir, "engine-bundle-pin.json");
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (node is null)
            {
                log.Add("engine-bundle-pin.json was not an object");
                return;
            }

            node.Remove("stage_root");
            node.Remove("profile_root");
            node.Remove("profile_id");
            node.Remove("archive");
            node["engine_relative"] = EngineBundleVerifier.RelativeDirectory;
            node["install_dir"] = installDir;
            if (!string.IsNullOrWhiteSpace(request.ProfileDir))
            {
                log.Add("user profile_dir=" + Path.GetFullPath(request.ProfileDir) + " (not written into pin)");
            }

            File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            log.Add("engine pin engine_relative=" + EngineBundleVerifier.RelativeDirectory + " install_dir=" + installDir);
        }
        catch (Exception ex)
        {
            log.Add("engine pin rewrite skipped: " + ex.Message);
        }
    }

    private static bool LooksLikeGitCheckout(string installDir) =>
        Directory.Exists(Path.Combine(installDir, ".git"));

    public static void ConfigureOpenWith(SetupRequest request, string installDir, List<string> log, IOpenWithStore? openWith, bool remove)
    {
        if (!OpenWithRegistration.AppliesTo(request.ProductId))
        {
            log.Add("open-with skipped " + request.ProductId);
            return;
        }

        var store = openWith ?? new HkcuOpenWithStore();
        if (remove)
        {
            OpenWithRegistration.Unregister(installDir, store);
            log.Add("open-with removed");
            return;
        }

        var launcher = OpenWithRegistration.Register(installDir, request.ProfileDir, store);
        log.Add("open-with launcher " + launcher);
    }

    private static SetupResult Uninstall(SetupRequest request, string installDir, List<string> log, IOpenWithStore? openWith)
    {
        ConfigureOpenWith(request, installDir, log, openWith, remove: true);
        if (Directory.Exists(installDir))
        {
            Directory.Delete(installDir, recursive: true);
            log.Add("deleted installDir");
        }

        var start = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", request.StartMenuName);
        if (Directory.Exists(start))
        {
            Directory.Delete(start, recursive: true);
            log.Add("deleted start menu " + start);
        }

        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + request.ProductId, throwOnMissingSubKey: false);

        if (!request.KeepData && request.ProfileDir is not null)
        {
            var profile = Path.GetFullPath(request.ProfileDir);
            if (IsForbidden(profile, out var why))
            {
                return Fail(4, "REFUSED_PROFILE_DELETE: " + why, log);
            }

            if (Directory.Exists(profile))
            {
                Directory.Delete(profile, recursive: true);
                log.Add("deleted profile " + profile);
            }
        }

        return new SetupResult(0, "Uninstalled " + request.ProductName + ". keepData=" + request.KeepData, log);
    }

    private static void WriteShortcut(SetupRequest request, string installDir, List<string> log)
    {
        try
        {
            var startDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", request.StartMenuName);
            Directory.CreateDirectory(startDir);
            var exe = Path.Combine(installDir, "xAIDesk.exe");
            var cmd = Path.Combine(startDir, request.ProductName + ".cmd");
            File.WriteAllText(cmd, "@echo off" + Environment.NewLine + "start \"\" \"" + exe + "\"" + Environment.NewLine);
            log.Add("wrote cmd shortcut " + cmd);
            var lnk = Path.Combine(startDir, request.ProductName + ".lnk");
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            var shortcut = shell.CreateShortcut(lnk);
            shortcut.TargetPath = exe;
            shortcut.WorkingDirectory = installDir;
            shortcut.Description = "xAI Desk unsigned internal";
            shortcut.Save();
            log.Add("wrote lnk " + lnk);
        }
        catch (Exception ex)
        {
            log.Add("shortcut fallback: " + ex.Message);
        }
    }

    private static void WriteUninstallKey(SetupRequest request, string installDir, List<string> log)
    {
        var setup = Environment.ProcessPath ?? "xAIDesk.Setup.exe";
        var uninstall = $"\"{setup}\" --uninstall --install-dir \"{installDir}\" --product-id \"{request.ProductId}\" --start-menu-name \"{request.StartMenuName}\" --keep-data";
        using var hkcu = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + request.ProductId);
        hkcu.SetValue("DisplayName", request.ProductName + " " + request.Version + " (unsigned internal preview)");
        hkcu.SetValue("Publisher", "xAI Desk - not a signed publisher");
        hkcu.SetValue("DisplayVersion", request.Version);
        hkcu.SetValue("UninstallString", uninstall);
        hkcu.SetValue("InstallLocation", installDir);
        hkcu.SetValue("NoModify", 0);
        hkcu.SetValue("NoRepair", 0);
        log.Add("wrote uninstall key " + request.ProductId);
    }

    private static Receipt? ReadReceipt(string installDir)
    {
        var path = Path.Combine(installDir, "preview-setup-receipt.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Receipt>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadSentinel(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        return File.ReadAllText(path);
    }

    private static void AssertSentinel(string? path, string? before, List<string> log)
    {
        if (path is null)
        {
            return;
        }

        var after = ReadSentinel(path);
        if (!string.Equals(before, after, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("SENTINEL_CHANGED: unrelated file was modified.");
        }

        log.Add("sentinel unchanged");
    }

    private static SetupResult Fail(int code, string message, List<string> log)
    {
        log.Add(message);
        return new SetupResult(code, message, log);
    }

    private sealed class Receipt
    {
        public string? Version { get; set; }
        public string? ProfileDir { get; set; }
    }
}
