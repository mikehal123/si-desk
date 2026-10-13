namespace Sovereign.Desktop.Client;

public enum DeskConnectKind { XaiApi, Cursor, GrokBuild, Other }

/// <summary>
/// User-facing connect and run failures. Messages name what is missing and the next step.
/// They never include keys, tokens, or local paths.
/// </summary>
public static class DeskConnectionErrors
{
    public const string XaiKeyMissing =
        "No xAI API key is saved. Open Settings, paste a key from the xAI console, save it, then click Connect / refresh models.";

    public const string XaiAuthFailed =
        "The xAI API rejected the saved key (auth failed). Open Settings, replace the key, save it, then click Connect / refresh models.";

    public const string XaiNetwork =
        "xAI Desk could not reach the xAI API (network). Check this PC's connection, then click Connect / refresh models.";

    public const string XaiRefreshFailed =
        "Could not refresh xAI models. Save an xAI API key in Settings, check the network, then click Connect / refresh models.";

    public const string CursorNotInstalled =
        "The Cursor connector is not installed. Install it, sign in with Cursor, then click Connect / refresh models.";

    public const string CursorNotSignedIn =
        "Cursor is not signed in. Click Sign in with Cursor, finish in the browser, then click Connect / refresh models.";

    public const string CursorAuthFailed =
        "Cursor auth failed. Click Sign in with Cursor, finish in the browser, then click Connect / refresh models.";

    public const string CursorNetwork =
        "Cursor could not be reached (network). Check this PC's connection, then click Connect / refresh models.";

    public const string CursorRefreshFailed =
        "Could not refresh Cursor models. Install the Cursor connector, sign in with Cursor, check the network, then click Connect / refresh models.";

    public const string GrokNotInstalled =
        "Grok CLI / Grok Build was not found on PATH. Install Grok Build, or set XAI_DESK_GROK to the grok executable, then click Connect / refresh models.";

    public const string GrokNotSignedIn =
        "Grok Build is not signed in. Open Settings, click Sign in next to Grok Build, then click Connect / refresh models.";

    public const string GrokAuthFailed =
        "Grok Build auth failed. Open Settings, sign in again next to Grok Build, then click Connect / refresh models.";

    public const string GrokNetwork =
        "Grok Build could not reach xAI (network). Check this PC's connection, then click Connect / refresh models.";

    public const string GrokRefreshFailed =
        "Could not refresh Grok Build models. Install the Grok CLI and put it on PATH, sign in from Settings, check the network, then click Connect / refresh models.";

    public const string GrokSilence =
        "Grok Build response is incomplete (silence). No answer arrived after a few seconds. Check that the Grok CLI is installed and on PATH, that Grok Build is signed in (Settings, Grok Build, Sign in), and that this PC can reach xAI. Then click Connect / refresh models and send again.";

    public const string ConnectTimedOut =
        "Model connect timed out. Check the network, the xAI API key, Cursor sign-in, and that the Grok CLI is on PATH, then click Connect / refresh models.";

    public const string CachedModelsNote = " Showing the last saved model list. Connect again before sending.";

    private static readonly string[] Authored =
    [
        XaiKeyMissing, XaiAuthFailed, XaiNetwork, XaiRefreshFailed,
        CursorNotInstalled, CursorNotSignedIn, CursorAuthFailed, CursorNetwork, CursorRefreshFailed,
        GrokNotInstalled, GrokNotSignedIn, GrokAuthFailed, GrokNetwork, GrokRefreshFailed,
        GrokSilence, ConnectTimedOut
    ];

    public static bool IsAuthored(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        foreach (var item in Authored)
            if (string.Equals(message, item, StringComparison.Ordinal)) return true;
        return false;
    }

    public static string WithCachedModels(string message) => message + CachedModelsNote;

    public static string ForConnect(Exception error, DeskConnectKind kind)
    {
        if (error is OperationCanceledException) return ConnectTimedOut;
        if (IsAuthored(error.Message)) return error.Message;
        if (error is FileNotFoundException)
            return kind == DeskConnectKind.Cursor ? CursorNotInstalled : GrokNotInstalled;
        if (error is HttpRequestException) return Network(kind);
        var text = error.Message ?? "";
        if (LooksLikeAuth(text)) return Auth(kind);
        if (LooksLikeNetwork(text)) return Network(kind);
        if (text.Contains("API key", StringComparison.OrdinalIgnoreCase)) return XaiKeyMissing;
        if (text.Contains("not found", StringComparison.OrdinalIgnoreCase))
            return kind == DeskConnectKind.Cursor ? CursorNotInstalled : GrokNotInstalled;
        return Fallback(kind);
    }

    private static bool LooksLikeAuth(string text) =>
        text.Contains("401", StringComparison.Ordinal)
        || text.Contains("403", StringComparison.Ordinal)
        || text.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
        || text.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
        || text.Contains("auth failed", StringComparison.OrdinalIgnoreCase)
        || text.Contains("not signed in", StringComparison.OrdinalIgnoreCase)
        || text.Contains("not logged in", StringComparison.OrdinalIgnoreCase)
        || text.Contains("sign-in", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeNetwork(string text) =>
        text.Contains("network", StringComparison.OrdinalIgnoreCase)
        || text.Contains("timed out", StringComparison.OrdinalIgnoreCase)
        || text.Contains("timeout", StringComparison.OrdinalIgnoreCase)
        || text.Contains("unreachable", StringComparison.OrdinalIgnoreCase)
        || text.Contains("no such host", StringComparison.OrdinalIgnoreCase)
        || text.Contains("name resolution", StringComparison.OrdinalIgnoreCase);

    private static string Network(DeskConnectKind kind) => kind switch
    {
        DeskConnectKind.Cursor => CursorNetwork,
        DeskConnectKind.GrokBuild => GrokNetwork,
        _ => XaiNetwork
    };

    private static string Auth(DeskConnectKind kind) => kind switch
    {
        DeskConnectKind.Cursor => CursorNotSignedIn,
        DeskConnectKind.GrokBuild => GrokNotSignedIn,
        _ => XaiAuthFailed
    };

    private static string Fallback(DeskConnectKind kind) => kind switch
    {
        DeskConnectKind.Cursor => CursorRefreshFailed,
        DeskConnectKind.GrokBuild => GrokRefreshFailed,
        DeskConnectKind.XaiApi => XaiRefreshFailed,
        _ => ConnectTimedOut
    };
}
