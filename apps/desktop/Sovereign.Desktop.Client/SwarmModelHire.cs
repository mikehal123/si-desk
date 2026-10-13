namespace Sovereign.Desktop.Client;

/// <summary>
/// Picks one advertised model id per swarm role. Display names are matched, never sent.
/// grokbot-research and grokbot-ops are wake roles and are not in this catalog.
/// </summary>
public static class SwarmModelHire
{
    public static readonly string[] EngineerNames = ["Claude Fable 5.1", "Claude Opus 5.5", "GPT-5.6 Sol", "Codex 5.3"];
    public static readonly string[] LookupNames = ["Gemini 3.8 Flash", "Composer 2.5", "Muse Spark"];

    public static string Catalog(IReadOnlyList<CursorChatModel> models, string? parentModelId)
    {
        var engineer = Resolve(FirstMatch(models, EngineerNames, null), parentModelId, null);
        var verifier = Resolve(FirstMatch(models, EngineerNames, engineer), parentModelId, engineer);
        var lookup = Resolve(FirstMatch(models, LookupNames, null), parentModelId, null);
        var lines = new System.Text.StringBuilder();
        lines.Append("Swarm hire catalog. Pass the modelId on the helper tool's model field. Do not pass the display name.\n");
        lines.Append("Parent modelId: ").Append(string.IsNullOrWhiteSpace(parentModelId) ? "(none)" : parentModelId).Append('\n');
        if (models.Count == 0) lines.Append("(no advertised models)\n");
        foreach (var model in models)
            lines.Append("- ").Append(model.Id).Append(" | ").Append(model.Name).Append('\n');
        lines.Append("engineer modelId: ").Append(engineer).Append('\n');
        lines.Append("verifier modelId: ").Append(verifier).Append('\n');
        lines.Append("researcher modelId: ").Append(lookup).Append('\n');
        lines.Append("investigator modelId: ").Append(lookup).Append('\n');
        lines.Append("Use those ids for engineer, researcher, investigator, and verifier. ");
        lines.Append("The verifier modelId must differ from the engineer modelId. ");
        lines.Append("grokbot-research and grokbot-ops stay wake roles and take no modelId from this catalog. ");
        lines.Append("If the helper tool rejects the model field, say so and use the parent modelId when it is allowed for that role.\n");
        return lines.ToString();
    }

    public static string? FirstMatch(IReadOnlyList<CursorChatModel> models, IReadOnlyList<string> phrases, string? excludeId)
    {
        foreach (var phrase in phrases)
        {
            foreach (var model in models)
            {
                if (string.IsNullOrWhiteSpace(model.Id) || string.Equals(model.Id, excludeId, StringComparison.Ordinal)) continue;
                if (model.Name.Contains(phrase, StringComparison.OrdinalIgnoreCase)) return model.Id;
            }
        }
        return null;
    }

    private static string Resolve(string? matched, string? parentModelId, string? forbid)
    {
        if (!string.IsNullOrWhiteSpace(matched) && !string.Equals(matched, forbid, StringComparison.Ordinal)) return matched;
        if (!string.IsNullOrWhiteSpace(parentModelId) && !string.Equals(parentModelId, forbid, StringComparison.Ordinal)) return parentModelId;
        return "(none)";
    }
}
