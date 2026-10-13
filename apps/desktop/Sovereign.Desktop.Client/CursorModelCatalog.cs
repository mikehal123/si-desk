using System.Text.Json;

namespace Sovereign.Desktop.Client;

/// <summary>Model list for the Cursor subscription picker. Id and display name only; no credentials.</summary>
public static class CursorModelCatalog
{
    public const string FileName = "cursor-subscription-models.json";

    public sealed record Listing(CursorChatModel[] Models, string? CurrentModelId);

    public static Listing Parse(JsonElement models)
    {
        string? currentModelId = null;
        if (models.ValueKind == JsonValueKind.Object
            && models.TryGetProperty("currentModelId", out var current)
            && current.ValueKind == JsonValueKind.String)
            currentModelId = current.GetString();

        if (models.ValueKind != JsonValueKind.Object
            || !models.TryGetProperty("availableModels", out var available)
            || available.ValueKind != JsonValueKind.Array)
            return new Listing([], currentModelId);

        var parsed = new List<CursorChatModel>();
        foreach (var entry in available.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("modelId", out var idElement)
                || idElement.ValueKind != JsonValueKind.String)
                continue;
            var id = idElement.GetString();
            if (string.IsNullOrWhiteSpace(id)) continue;
            var name = entry.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString() ?? ""
                : "";
            parsed.Add(new CursorChatModel(id, name));
        }

        var ordered = parsed
            .OrderByDescending(model => model.Id.StartsWith("grok-4.7[", StringComparison.Ordinal))
            .ThenBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(model => model.Id, StringComparer.Ordinal)
            .ToArray();
        return new Listing(ordered, currentModelId);
    }

    public static string? SelectDefault(IReadOnlyList<CursorChatModel> models, string? savedModelId, string? currentModelId = null)
    {
        if (models.Count == 0) return null;
        if (!string.IsNullOrEmpty(savedModelId) && models.Any(model => model.Id == savedModelId))
            return savedModelId;
        foreach (var model in models)
            if (model.Id.StartsWith("grok-4.7[", StringComparison.Ordinal)) return model.Id;
        if (!string.IsNullOrEmpty(currentModelId) && models.Any(model => model.Id == currentModelId))
            return currentModelId;
        return models[0].Id;
    }

    public static CursorChatModel[] Resolve(CursorChatModel[]? live, CursorChatModel[] cached) =>
        live is { Length: > 0 } ? live : cached ?? [];

    public static void Save(string profileDirectory, IReadOnlyList<CursorChatModel> models)
    {
        if (string.IsNullOrWhiteSpace(profileDirectory) || models is not { Count: > 0 }) return;
        var path = Path.Combine(profileDirectory, FileName);
        var temp = Path.Combine(profileDirectory, FileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                models = models.Select(model => new { id = model.Id, name = model.Name }).ToArray()
            });
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static CursorChatModel[] Load(string profileDirectory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(profileDirectory)) return [];
            var path = Path.Combine(profileDirectory, FileName);
            if (!File.Exists(path)) return [];
            if (new FileInfo(path).Length > 1_048_576) return [];
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("models", out var models)
                || models.ValueKind != JsonValueKind.Array)
                return [];
            var loaded = new List<CursorChatModel>();
            foreach (var item in models.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("id", out var idElement)
                    || idElement.ValueKind != JsonValueKind.String)
                    continue;
                var id = idElement.GetString();
                if (string.IsNullOrWhiteSpace(id)) continue;
                var name = item.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString() ?? ""
                    : "";
                loaded.Add(new CursorChatModel(id, name));
            }
            return loaded.ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }
}
