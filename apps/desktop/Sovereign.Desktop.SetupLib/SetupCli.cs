namespace Sovereign.Desktop.SetupLib;

public static class SetupCli
{
    public const string ProductionProductId = "xAIDesk";
    public const string ProductionProductName = "xAI Desk";
    public const string CurrentVersion = "0.3.3";
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
        if (!TryGetRequired(args, "--install-dir", out var installDir, out var flagPresent) || string.IsNullOrWhiteSpace(installDir))
        {
            var why = flagPresent
                ? "MISSING_REQUIRED_ARGUMENT: --install-dir was present without a value. Default LocalAppData install is refused."
                : "MISSING_REQUIRED_ARGUMENT: --install-dir is required. Default LocalAppData install is refused.";
            return new SetupResult(MissingInstallDirExitCode, why, [why]);
        }

        var payload = Get(args, "--payload") ?? Path.Combine(AppContext.BaseDirectory, "payload");
        var profile = Get(args, "--profile-dir");
        int? failAfter = int.TryParse(Get(args, "--fail-after-files"), out var n) ? n : null;
        var sentinel = Get(args, "--sentinel");
        var action = uninstall ? SetupAction.Uninstall : repair ? SetupAction.Repair : upgrade ? SetupAction.Upgrade : SetupAction.Install;
        request = new SetupRequest(action, installDir, payload, productId, productName, version, profile, startMenu, keepData, failAfter, sentinel);
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
