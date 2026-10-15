using System.Text.Json;

namespace Sovereign.Desktop.Client;

/// <summary>
/// Per-user confirmation before a chat switches to Always allow.
/// A missing or unreadable file still shows the warning. It does not change saved project permissions.
/// </summary>
public static class AlwaysAllowWarningPreference
{
    public const string FileName = "always-allow-warning.json";

    public const string Message =
        "Always allow lets the agent run commands and edit files anywhere your Windows account can reach, not just this project folder, without asking. Only use it on projects you trust.";

    /// <summary>True when this user chose Don't show again. Missing, empty, or unreadable settings still warn.</summary>
    public static bool IsDismissed(string profile)
    {
        try
        {
            var path = Path.Combine(profile, FileName);
            if (!File.Exists(path)) return false;
            using var body = JsonDocument.Parse(File.ReadAllText(path));
            if (body.RootElement.ValueKind != JsonValueKind.Object) return false;
            foreach (var name in new[] { "dismissed", "Dismissed" })
                if (body.RootElement.TryGetProperty(name, out var dismissed) && dismissed.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    return dismissed.GetBoolean();
            return false;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void SaveDismissed(string profile, bool dismissed)
    {
        Directory.CreateDirectory(profile);
        var path = Path.Combine(profile, FileName);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, "{\"dismissed\":" + (dismissed ? "true" : "false") + "}");
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>
    /// Warn only when the next mode is Always allow, the chat is not already there, and this user has not dismissed the warning.
    /// </summary>
    public static bool ShouldWarn(
        CursorProjectMode currentMode,
        CursorPermissionPolicy currentPermissions,
        CursorProjectMode nextMode,
        CursorPermissionPolicy nextPermissions,
        bool warningDismissed)
    {
        var turningOn = nextMode == CursorProjectMode.Agent
            && nextPermissions == CursorPermissionPolicy.AlwaysAllow
            && !(currentMode == CursorProjectMode.Agent && currentPermissions == CursorPermissionPolicy.AlwaysAllow);
        return turningOn && !warningDismissed;
    }
}
