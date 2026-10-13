using System.Text.Json;

namespace Sovereign.Desktop.Client;

public enum DeskKeyVerdict { Absent, Valid, Rejected, Network, NoGrok, Unavailable }

public sealed record DeskSetupItem(string Name, bool Ok, string Fix)
{
    public string State => Ok ? "OK" : "Missing";
    public string Line => Ok ? "OK" : "Missing. " + Fix;
}

public sealed record DeskSetupReport(DeskSetupItem XaiApi, DeskSetupItem Cursor, DeskSetupItem Grok)
{
    public IReadOnlyList<DeskSetupItem> Items => [XaiApi, Cursor, Grok];
}

public sealed class DeskSetupSources
{
    public string? XaiApiKey { get; init; }
    public HttpMessageHandler? XaiHandler { get; init; }
    public bool CursorRuntimeInstalled { get; init; }
    public bool CursorSignedIn { get; init; }
    public bool GrokFound { get; init; }
}

/// <summary>
/// First-run and Settings check. Results are OK or Missing plus one fix line.
/// The API key is used only for the models request and is never copied into the report.
/// </summary>
public static class DeskSetupCheck
{
    public static DeskSetupReport Evaluate(DeskKeyVerdict xai, bool cursorRuntime, bool cursorSignedIn, bool grokFound) =>
        new(XaiItem(xai), CursorItem(cursorRuntime, cursorSignedIn), GrokItem(grokFound));

    public static async Task<DeskSetupReport> CollectAsync(DeskSetupSources sources, CancellationToken ct)
    {
        var verdict = await ProbeXaiKeyAsync(sources.XaiApiKey, ct, sources.XaiHandler);
        return Evaluate(verdict, sources.CursorRuntimeInstalled, sources.CursorSignedIn, sources.GrokFound);
    }

    public static async Task<DeskKeyVerdict> ProbeXaiKeyAsync(string? apiKey, CancellationToken ct, HttpMessageHandler? handler = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return DeskKeyVerdict.Absent;
        try
        {
            using var client = new XaiApiChatClient(apiKey, handler);
            await client.ModelsAsync(ct);
            return DeskKeyVerdict.Valid;
        }
        catch (InvalidOperationException)
        {
            return DeskKeyVerdict.Absent;
        }
        catch (HttpRequestException)
        {
            return DeskKeyVerdict.Network;
        }
        catch (OperationCanceledException)
        {
            return DeskKeyVerdict.Network;
        }
        catch (IOException ex) when (ex.Message.Contains("did not advertise a Grok model", StringComparison.Ordinal))
        {
            return DeskKeyVerdict.NoGrok;
        }
        catch (IOException ex) when (ex.Message.Contains("network", StringComparison.OrdinalIgnoreCase))
        {
            return DeskKeyVerdict.Network;
        }
        catch (IOException ex) when (IsRejected(ex.Message))
        {
            return DeskKeyVerdict.Rejected;
        }
        catch (IOException)
        {
            return DeskKeyVerdict.Unavailable;
        }
    }

    public static DeskSetupItem XaiItem(DeskKeyVerdict verdict) => verdict switch
    {
        DeskKeyVerdict.Valid => new("xAI API key", true, ""),
        DeskKeyVerdict.Absent => new("xAI API key", false, "Save an xAI API key in Settings, Connections."),
        DeskKeyVerdict.Rejected => new("xAI API key", false, "Auth failed. Replace the xAI API key in Settings, Connections."),
        DeskKeyVerdict.Network => new("xAI API key", false, "Network blocked the check. Connect to the internet and run Check setup again."),
        DeskKeyVerdict.NoGrok => new("xAI API key", false, "The key was accepted, but no Grok model was listed. Check the xAI account."),
        _ => new("xAI API key", false, "The key could not be checked. Save it again in Settings, Connections.")
    };

    public static DeskSetupItem CursorItem(bool runtimeInstalled, bool signedIn)
    {
        if (runtimeInstalled && signedIn) return new("Cursor login", true, "");
        if (!runtimeInstalled) return new("Cursor login", false, "Install the Cursor connector, then sign in from Settings.");
        return new("Cursor login", false, "Sign in with Cursor from Settings or Chat.");
    }

    public static DeskSetupItem GrokItem(bool found) => found
        ? new("Grok Build", true, "")
        : new("Grok Build", false, "Install Grok Build and make sure grok is on PATH.");

    private static bool IsRejected(string message) =>
        message.Contains("auth failed", StringComparison.OrdinalIgnoreCase)
        || message.Contains("rejected the saved key", StringComparison.OrdinalIgnoreCase)
        || message.Contains("(401)", StringComparison.Ordinal)
        || message.Contains("(403)", StringComparison.Ordinal);
}

/// <summary>Looks for a Cursor CLI credential without returning or logging its value.</summary>
public static class CursorLoginProbe
{
    public static bool IsSignedIn() =>
        IsSignedIn(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), File.Exists, SafeRead);

    public static bool IsSignedIn(string profile, Func<string, bool> exists, Func<string, string?> read)
    {
        if (string.IsNullOrWhiteSpace(profile)) return false;
        foreach (var name in new[] { "cli-config.json", "auth.json" })
        {
            var path = Path.Combine(profile, ".cursor", name);
            if (!exists(path)) continue;
            if (HasCredential(read(path))) return true;
        }
        return false;
    }

    public static bool HasCredential(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 1_048_576) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            return ContainsCredential(document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ContainsCredential(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (IsCredentialName(property.Name)
                        && property.Value.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                        return true;
                    if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                        && ContainsCredential(property.Value))
                        return true;
                }
                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    if (ContainsCredential(item)) return true;
                return false;
            default:
                return false;
        }
    }

    private static bool IsCredentialName(string name) =>
        name.Equals("accessToken", StringComparison.OrdinalIgnoreCase)
        || name.Equals("access_token", StringComparison.OrdinalIgnoreCase)
        || name.Equals("refreshToken", StringComparison.OrdinalIgnoreCase)
        || name.Equals("refresh_token", StringComparison.OrdinalIgnoreCase)
        || name.Equals("authToken", StringComparison.OrdinalIgnoreCase)
        || name.Equals("apiKey", StringComparison.OrdinalIgnoreCase);

    private static string? SafeRead(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 1_048_576) return null;
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

public static class DeskSetupPrompt
{
    public const string FileName = "setup-check.json";

    public static bool ShouldShow(string? profile)
    {
        if (string.IsNullOrWhiteSpace(profile)) return false;
        return !File.Exists(Path.Combine(profile, FileName));
    }

    public static void MarkShown(string profile)
    {
        Directory.CreateDirectory(profile);
        var path = Path.Combine(profile, FileName);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, "{\"shown\":true}");
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
