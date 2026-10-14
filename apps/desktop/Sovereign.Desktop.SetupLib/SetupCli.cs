using System.Globalization;

namespace Sovereign.Desktop.SetupLib;

public static class SetupCli
{
    public const string ProductionProductId = "SIDesk";
    public const string ProductionProductName = "SI Desk";
    public static string CurrentVersion
    {
        get
        {
            var version = typeof(SetupCli).Assembly.GetName().Version;
            if (version is null)
                return "0.0.0";
            var build = version.Build < 0 ? 0 : version.Build;
            return version.Major.ToString(CultureInfo.InvariantCulture)
                + "." + version.Minor.ToString(CultureInfo.InvariantCulture)
                + "." + build.ToString(CultureInfo.InvariantCulture);
        }
    }

    public static string DefaultInstallDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", ProductionProductName);

    public const int MissingInstallDirExitCode = 6;

    public static SetupResult? ParseOrError(string[] args, out SetupRequest? request)
    {
        request = null;
        var uninstall = Has(args, "--uninstall");
        var repair = Has(args, "--repair");
        var upgrade = Has(args, "--upgrade");
        var removeData = Has(args, "--remove-data");
        var keepData = Has(args, "--keep-data") || !removeData;
        var testInstance = Get(args, "--test-instance");
        var productId = Get(args, "--product-id")
                        ?? (testInstance is null ? ProductionProductId : ProductionProductId + "." + testInstance);
        var productName = Get(args, "--product-name")
                          ?? (testInstance is null ? ProductionProductName : ProductionProductName + " " + testInstance);
        var startMenu = Get(args, "--start-menu-name") ?? productName;
        var version = Get(args, "--version") ?? CurrentVersion;
        var desktopShortcut = Has(args, "--desktop-shortcut");
        string installDir;
        if (!TryGetRequired(args, "--install-dir", out var givenDir, out var flagPresent) || string.IsNullOrWhiteSpace(givenDir))
        {
            if (flagPresent)
            {
                var why = "MISSING_REQUIRED_ARGUMENT: --install-dir was present without a value.";
                return new SetupResult(MissingInstallDirExitCode, why, [why]);
            }

            installDir = DefaultInstallDir();
        }
        else
        {
            installDir = givenDir;
        }

        var payload = Get(args, "--payload") ?? Path.Combine(AppContext.BaseDirectory, "payload");
        var profile = Get(args, "--profile-dir");
        int? failAfter = int.TryParse(Get(args, "--fail-after-files"), out var n) ? n : null;
        var sentinel = Get(args, "--sentinel");
        var action = uninstall ? SetupAction.Uninstall : repair ? SetupAction.Repair : upgrade ? SetupAction.Upgrade : SetupAction.Install;
        request = new SetupRequest(action, installDir, payload, productId, productName, version, profile, startMenu, keepData, failAfter, sentinel, desktopShortcut);
        return null;
    }

    internal static bool Has(string[] args, string name) =>
        args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    internal static string? Get(string[] args, string name) =>
        TryGetRequired(args, name, out var value, out _) ? value : null;

    internal static bool TryGetRequired(string[] args, string name, out string? value, out bool flagPresent)
    {
        value = null;
        flagPresent = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            flagPresent = true;
            if (i + 1 >= args.Length)
            {
                return false;
            }

            var next = args[i + 1];
            if (next.StartsWith("--", StringComparison.Ordinal) && next != "--")
            {
                return false;
            }

            value = next;
            return !string.IsNullOrWhiteSpace(next);
        }

        return false;
    }
}
