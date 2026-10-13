using Sovereign.Desktop.Core;

namespace Sovereign.Desktop.Client;

/// <summary>Chat connections offered by xAI Desk. Indexes stay inside the saved-chat range.</summary>
public static class DeskChatConnections
{
    public const int Cursor = 0;
    public const int GrokBuild = 1;
    public const int XaiApi = 2;
    public const int CursorStored = 4;
    public const int GrokBuildStored = 0;
    public const int XaiApiStored = 1;

    public static readonly string[] Labels = ["Cursor", "Grok Build", "xAI API"];

    public static string Key(int comboIndex) => comboIndex switch
    {
        GrokBuild => "grok-build",
        XaiApi => "xai-api",
        _ => "cursor"
    };

    /// <summary>Combo position to the saved provider id. Cursor stays 4, matching the original desk's chats.</summary>
    public static int Stored(int comboIndex) => comboIndex switch
    {
        GrokBuild => GrokBuildStored,
        XaiApi => XaiApiStored,
        _ => CursorStored
    };

    public static int Combo(int stored) => stored switch
    {
        CursorStored => Cursor,
        XaiApiStored => XaiApi,
        GrokBuildStored => GrokBuild,
        _ => Cursor
    };

    public static string PickerLabel(int provider, string modelLabel)
    {
        var connection = provider switch
        {
            GrokBuild => "Grok Build",
            XaiApi => "xAI API",
            _ => "Cursor"
        };
        var prefix = connection + " · ";
        return modelLabel.StartsWith(prefix, StringComparison.Ordinal) ? modelLabel : prefix + modelLabel;
    }

    public static CursorChatModel[] GrokModels(IEnumerable<CursorChatModel> models) =>
        models.Where(model => XaiDeskIdentity.IsDeskModel(model.Id, model.Name))
            .OrderByDescending(PrefersGrok47)
            .ThenBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static bool PrefersGrok47(CursorChatModel model) =>
        model.Id.Contains("grok-4.7", StringComparison.OrdinalIgnoreCase)
        || model.Name.Contains("4.7", StringComparison.OrdinalIgnoreCase);
}
