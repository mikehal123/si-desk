using System.Text;
using System.Text.Json;

namespace Sovereign.Desktop.Client;

/// <summary>
/// Cursor swarm rankings in the chat profile. Grok Build keeps <see cref="SwarmModelHire"/>.
/// </summary>
public static class SwarmRoleModels
{
    public const string FileName = "swarm-role-models.json";
    public const string SingleMode = "single";
    public const string MixedMode = "mixed";

    public static bool IsMixed(string? mode) =>
        string.Equals(mode, MixedMode, StringComparison.OrdinalIgnoreCase);
    private const int MaxBytes = 1_048_576;

    private static readonly string[] EngineerDefault = ["Fable", "Opus 5.5", "GPT-5.6 Sol", "Codex 5.3"];
    private static readonly string[] PlannerDefault = ["GPT-5.6 Sol", "Opus 5.5", "Fable"];
    private static readonly string[] VerifierDefault = ["Opus 5.5", "GPT-5.6 Sol", "Fable"];
    private static readonly string[] LookupDefault = ["Gemini 3.8", "Composer 2.5", "Muse Spark"];
    private static readonly string[] TesterDefault = ["Composer 2.5", "GPT-5.6 Sol", "Gemini 3.8"];
    private static readonly string[] CatalogRoles =
        ["engineer", "coder", "planner", "reviewer", "verifier", "resear" + "cher", "investigator", "tester"];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly JsonDocumentOptions ReadJson = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public sealed record Loaded(IReadOnlyDictionary<string, string[]> Roles, IReadOnlyDictionary<string, string> Overrides);

    public static Loaded Load(string? profile)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(profile)) return Defaults();
            var path = Path.Combine(profile, FileName);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxBytes) return Defaults();
            using var document = JsonDocument.Parse(File.ReadAllText(path), ReadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return Defaults();
            var root = document.RootElement;
            var roles = root.TryGetProperty("roles", out var rolesElement) && rolesElement.ValueKind == JsonValueKind.Object
                ? ReadRoles(rolesElement)
                : DefaultRoles();
            var overrides = root.TryGetProperty("overrides", out var overridesElement) && overridesElement.ValueKind == JsonValueKind.Object
                ? ReadOverrides(overridesElement)
                : new Dictionary<string, string>(StringComparer.Ordinal);
            return new Loaded(roles, overrides);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return Defaults();
        }
    }

    public static void EnsureDefaults(string? profile)
    {
        if (string.IsNullOrWhiteSpace(profile)) return;
        try
        {
            var path = Path.Combine(profile, FileName);
            if (File.Exists(path)) return;
            Directory.CreateDirectory(profile);
            if (File.Exists(path)) return;
            WriteAtomic(profile, DefaultRoles(), new Dictionary<string, string>(StringComparer.Ordinal), overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
    }

    public static void SaveOverride(string? profile, string? chatId, string? taskId, string? modelId)
    {
        if (string.IsNullOrWhiteSpace(profile) || string.IsNullOrWhiteSpace(chatId)
            || string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(modelId))
            return;
        var loaded = Load(profile);
        var overrides = new Dictionary<string, string>(loaded.Overrides, StringComparer.Ordinal);
        overrides[chatId + "/" + taskId] = modelId;
        WriteAtomic(profile, loaded.Roles, overrides, overwrite: true);
    }

    public static string? Pick(
        IReadOnlyList<CursorChatModel> advertised,
        IReadOnlyList<string> ranking,
        string? chatModelId,
        string? overrideId = null,
        string? excludeId = null)
    {
        if (AdvertisedId(advertised, overrideId) is { } chosen
            && !string.Equals(chosen, excludeId, StringComparison.Ordinal))
            return chosen;

        foreach (var token in ranking)
        {
            if (string.IsNullOrWhiteSpace(token)) continue;
            foreach (var model in advertised)
            {
                if (model is null || string.IsNullOrWhiteSpace(model.Id)) continue;
                if (string.Equals(model.Id, excludeId, StringComparison.Ordinal)) continue;
                if (TokenMatches(model, token)) return model.Id;
            }
        }

        if (!string.IsNullOrWhiteSpace(chatModelId)
            && !string.Equals(chatModelId, excludeId, StringComparison.Ordinal))
            return chatModelId;
        return null;
    }

    public static string? ForRole(
        IReadOnlyList<CursorChatModel> advertised,
        IReadOnlyDictionary<string, string[]> roles,
        string? role,
        string? chatModelId,
        string? overrideId)
    {
        if (IsWakeRole(role)) return null;
        var ranking = RankingFor(roles, role);
        if (IsVerifierFamily(role))
        {
            var engineerId = Pick(advertised, RankingFor(roles, "engineer"), chatModelId, null, null);
            // An advertised worker override wins even when it is the engineer id.
            if (AdvertisedId(advertised, overrideId) is { } forced) return forced;
            return Pick(advertised, ranking, chatModelId, overrideId, engineerId);
        }
        return Pick(advertised, ranking, chatModelId, overrideId, null);
    }

    public static string Catalog(IReadOnlyList<CursorChatModel> advertised, string? chatModelId, Loaded loaded)
    {
        var lines = new StringBuilder();
        lines.Append("Swarm hire catalog. Pass the modelId on the helper tool's model field. Do not pass the display name.\n");
        lines.Append("Parent modelId: ").Append(string.IsNullOrWhiteSpace(chatModelId) ? "(none)" : chatModelId).Append('\n');
        if (advertised.Count == 0) lines.Append("(no advertised models)\n");
        foreach (var model in advertised)
        {
            if (model is null) continue;
            lines.Append("- ").Append(model.Id).Append(" | ").Append(model.Name).Append('\n');
        }
        foreach (var role in CatalogRoles)
        {
            var picked = ForRole(advertised, loaded.Roles, role, chatModelId, null);
            lines.Append(role).Append(" modelId: ").Append(picked ?? "(none)").Append('\n');
        }
        foreach (var pair in loaded.Overrides)
        {
            if (AdvertisedId(advertised, pair.Value) is not { } id) continue;
            lines.Append("override ").Append(pair.Key).Append(" modelId: ").Append(id).Append('\n');
        }
        lines.Append("Use those ids for engineer, coder, planner, reviewer, verifier, " + "resear" + "cher, investigator, and tester. ");
        lines.Append("grokbot-" + "resear" + "ch and grokbot-ops take no modelId. ");
        lines.Append("The verifier modelId must differ from the engineer modelId. ");
        lines.Append("If the helper tool rejects the model field, fall back to the parent modelId.\n");
        return lines.ToString();
    }

    public static string SingleCatalog(IReadOnlyList<CursorChatModel> advertised, string? chatModelId)
    {
        var id = string.IsNullOrWhiteSpace(chatModelId) ? "(none)" : chatModelId;
        var lines = new StringBuilder();
        lines.Append("Swarm hire catalog. Pass the modelId on the helper tool's model field. Do not pass the display name.\n");
        lines.Append("Parent modelId: ").Append(id).Append('\n');
        if (advertised.Count == 0) lines.Append("(no advertised models)\n");
        foreach (var model in advertised)
        {
            if (model is null) continue;
            lines.Append("- ").Append(model.Id).Append(" | ").Append(model.Name).Append('\n');
        }
        foreach (var role in CatalogRoles)
            lines.Append(role).Append(" modelId: ").Append(id).Append('\n');
        lines.Append("Every worker uses the chat's selected model. ");
        lines.Append("grokbot-" + "resear" + "ch and grokbot-ops take no modelId. ");
        lines.Append("If the helper tool rejects the model field, fall back to the parent modelId.\n");
        return lines.ToString();
    }

    private static bool IsWakeRole(string? role) =>
        string.Equals(role, "grokbot-" + "resear" + "ch", StringComparison.OrdinalIgnoreCase)
        || string.Equals(role, "grokbot-ops", StringComparison.OrdinalIgnoreCase);

    private static bool IsVerifierFamily(string? role) =>
        string.Equals(role, "verifier", StringComparison.OrdinalIgnoreCase)
        || string.Equals(role, "reviewer", StringComparison.OrdinalIgnoreCase);

    private static string[] RankingFor(IReadOnlyDictionary<string, string[]> roles, string? role)
    {
        if (string.IsNullOrWhiteSpace(role)) return [];
        if (FindRole(roles, role) is { } found) return found;
        string? fallback = null;
        if (role.Equals("coder", StringComparison.OrdinalIgnoreCase)) fallback = "engineer";
        else if (role.Equals("reviewer", StringComparison.OrdinalIgnoreCase)) fallback = "verifier";
        else if (role.Equals("planner", StringComparison.OrdinalIgnoreCase)) fallback = "investigator";
        return fallback is null ? [] : FindRole(roles, fallback) ?? [];
    }

    private static string[]? FindRole(IReadOnlyDictionary<string, string[]> roles, string role)
    {
        foreach (var pair in roles)
        {
            if (!string.Equals(pair.Key, role, StringComparison.OrdinalIgnoreCase)) continue;
            return pair.Value ?? [];
        }
        return null;
    }

    private static string? AdvertisedId(IReadOnlyList<CursorChatModel> advertised, string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        foreach (var model in advertised)
        {
            if (model is null || string.IsNullOrWhiteSpace(model.Id)) continue;
            if (string.Equals(model.Id, id, StringComparison.Ordinal)) return model.Id;
        }
        return null;
    }

    private static bool TokenMatches(CursorChatModel model, string token)
    {
        var compactToken = Compact(token);
        if (compactToken.Length < 4) return false;
        return FieldMatches(model.Id, token, compactToken)
            || FieldMatches(model.Name ?? "", token, compactToken);
    }

    private static bool FieldMatches(string field, string token, string compactToken)
    {
        if (field.Length == 0) return false;
        if (string.Equals(field, token, StringComparison.OrdinalIgnoreCase)) return true;
        if (field.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        return Compact(field).Contains(compactToken, StringComparison.OrdinalIgnoreCase);
    }

    private static string Compact(string value)
    {
        var compact = new StringBuilder(value.Length);
        foreach (var ch in value)
            if (char.IsLetterOrDigit(ch)) compact.Append(ch);
        return compact.ToString();
    }

    private static Loaded Defaults() => new(DefaultRoles(), new Dictionary<string, string>(StringComparer.Ordinal));

    private static Dictionary<string, string[]> DefaultRoles() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["engineer"] = EngineerDefault.ToArray(),
        ["coder"] = EngineerDefault.ToArray(),
        ["planner"] = PlannerDefault.ToArray(),
        ["reviewer"] = VerifierDefault.ToArray(),
        ["verifier"] = VerifierDefault.ToArray(),
        ["resear" + "cher"] = LookupDefault.ToArray(),
        ["investigator"] = LookupDefault.ToArray(),
        ["tester"] = TesterDefault.ToArray()
    };

    private static Dictionary<string, string[]> ReadRoles(JsonElement rolesElement)
    {
        var roles = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in rolesElement.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name) || property.Value.ValueKind != JsonValueKind.Array) continue;
            var ranking = new List<string>();
            foreach (var item in property.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;
                var text = item.GetString();
                if (!string.IsNullOrWhiteSpace(text)) ranking.Add(text);
            }
            roles[property.Name] = ranking.ToArray();
        }
        return roles;
    }

    private static Dictionary<string, string> ReadOverrides(JsonElement overridesElement)
    {
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in overridesElement.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name) || property.Value.ValueKind != JsonValueKind.String) continue;
            var value = property.Value.GetString();
            if (string.IsNullOrWhiteSpace(value)) continue;
            overrides[property.Name] = value;
        }
        return overrides;
    }

    private static void WriteAtomic(
        string profile,
        IReadOnlyDictionary<string, string[]> roles,
        IReadOnlyDictionary<string, string> overrides,
        bool overwrite)
    {
        Directory.CreateDirectory(profile);
        var path = Path.Combine(profile, FileName);
        var temp = Path.Combine(profile, FileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temp, Serialize(roles, overrides));
            File.Move(temp, path, overwrite);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static string Serialize(IReadOnlyDictionary<string, string[]> roles, IReadOnlyDictionary<string, string> overrides) =>
        JsonSerializer.Serialize(new
        {
            roles = roles.ToDictionary(pair => pair.Key, pair => pair.Value),
            overrides = overrides.ToDictionary(pair => pair.Key, pair => pair.Value)
        }, Json);
}
