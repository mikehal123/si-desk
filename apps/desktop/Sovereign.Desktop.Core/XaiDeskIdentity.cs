namespace Sovereign.Desktop.Core;

/// <summary>Identity for the xAI Desk clone. Kept separate from the original desk's data, keys, and single-instance names.</summary>
public static class XaiDeskIdentity
{
    public const string AppName = "xAI Desk";
    public const string ExeName = "xAIDesk";
    public const string AppUserModelId = "xAI.Desk";
    public const string DataFolderName = "xAIDesk";
    public const string SingleInstanceMutex = @"Local\xAIDesk.SingleInstance";
    public const string OpenFileMutexPrefix = @"Local\xAIDesk.SingleInstance.";
    public const string OpenFilePipePrefix = "xAIDesk.SingleInstance.";
    public const string CredentialPrefix = "xAIDesk";
    public const string XaiApiCredentialResource = "xAIDesk.XaiApi";
    public const string XaiApiCredentialAccount = "default";
    public const string GrokBotCredentialResource = "xAIDesk.GrokBotConnector";
    public const string GrokBotCredentialAccount = "token";
    public const int GrokBotPort = 8790;
    public const int PhonePort = 8789;
    public const bool UpdaterEnabled = false;
    public static readonly string[] Navigation = ["chat", "room", "grokbot", "settings"];

    public static string DataDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DataFolderName);

    public static bool IsGrokModel(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Contains("grok", StringComparison.OrdinalIgnoreCase);

    /// <summary>Cursor, Grok Build, and the xAI API may only offer Grok or xAI models.</summary>
    public static bool IsDeskModel(string? id, string? name = null)
    {
        if (OtherVendor(id) || OtherVendor(name)) return false;
        return IsGrokModel(id) || IsGrokModel(name) || MentionsXai(id) || MentionsXai(name);
    }

    private static bool MentionsXai(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains("xai", StringComparison.OrdinalIgnoreCase);

    private static bool OtherVendor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        foreach (var token in new[] { "claude", "gemini", "gpt", "openai", "ollama", "kimi", "glm", "composer", "sonnet", "opus" })
            if (value.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static string NavigationLabel(string tag) => tag switch
    {
        "chat" => "Chat",
        "room" => "Shared room",
        "grokbot" => "Grokbot",
        "settings" => "Settings",
        _ => "Settings"
    };
}
