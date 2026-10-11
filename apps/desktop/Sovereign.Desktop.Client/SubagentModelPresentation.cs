namespace Sovereign.Desktop.Client;

/// <summary>Shared sidebar labels from persisted helper metadata. Configuration elsewhere is not model evidence.</summary>
public static class SubagentModelPresentation
{
    public const string Unknown = "Model unknown";

    public static string Display(CursorSubagent child)
    {
        if (IsRequestedSource(child.ModelSource)) return Unknown;
        return ModelDisplay(child.ModelId, child.ModelName);
    }

    private static string ModelDisplay(string? modelId, string? modelName)
    {
        var id = modelId?.Trim();
        var name = modelName?.Trim();
        if (string.IsNullOrEmpty(id) && string.IsNullOrEmpty(name)) return Unknown;
        // An unresolved routing alias does not identify the model that ran.
        if ((string.Equals(id, "auto", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(id, "default", StringComparison.OrdinalIgnoreCase)) && string.IsNullOrWhiteSpace(name)) return Unknown;
        return ChatModelLabel.Display(id ?? name!, name);
    }

    public static string ProviderLabel(CursorSubagent child) => ProviderDisplay(IsRequestedSource(child.ModelSource) ? null : child.ModelProvider);

    /// <summary>Provider family for a model id the runtime actually assigned. Unknown ids stay unnamed.</summary>
    public static string? KnownProvider(string? modelId)
    {
        var id = (modelId ?? "").Trim().ToLowerInvariant();
        if (id == "grok" || id.StartsWith("grok-", StringComparison.Ordinal) || id.StartsWith("grok ", StringComparison.Ordinal)) return "xai";
        if (id == "gpt" || id.StartsWith("gpt-", StringComparison.Ordinal) || id.StartsWith("gpt ", StringComparison.Ordinal)) return "openai";
        return null;
    }

    private static string ProviderDisplay(string? provider) => provider?.Trim().ToLowerInvariant() switch
    {
        "openai" => "OpenAI",
        "xai" or "x.ai" => "xAI",
        null or "" => "Provider unknown",
        _ => provider!.Trim()
    };

    public static bool IsRequestedSource(string? source) => source?.Trim().ToLowerInvariant() is
        "runtime configuration" or "runtime request" or "requested" or "configured";

    public static bool HasRequestedModel(CursorSubagent child) => !string.IsNullOrWhiteSpace(child.RequestedModelId)
        || !string.IsNullOrWhiteSpace(child.RequestedModelName) || IsRequestedSource(child.ModelSource);

    public static string RequestedDisplay(CursorSubagent child) => ModelDisplay(child.RequestedModelId ??
        (IsRequestedSource(child.ModelSource) ? child.ModelId : null), child.RequestedModelName ??
        (IsRequestedSource(child.ModelSource) ? child.ModelName : null));

    public static string RequestedProviderLabel(CursorSubagent child) => ProviderDisplay(child.RequestedModelProvider ??
        (IsRequestedSource(child.ModelSource) ? child.ModelProvider : null));

    public static string RequestedCaption(CursorSubagent child) => string.Equals(child.RequestedModelSource ?? child.ModelSource,
        "runtime configuration", StringComparison.OrdinalIgnoreCase) ? "Configured model" : "Requested model";

    public static string SourceLabel(CursorSubagent child) => string.IsNullOrWhiteSpace(child.ModelSource)
        ? "Not reported" : child.ModelSource.Trim();

    /// <summary>Artwork family from reported metadata; does not assert a subscription, endpoint, or per-call model verification.</summary>
    public static string Family(CursorSubagent child)
    {
        if (IsRequestedSource(child.ModelSource)) return "generic";
        var id = (child.ModelId ?? child.ModelName ?? "").Trim().ToLowerInvariant();
        if (id == "grok" || id.StartsWith("grok-", StringComparison.Ordinal) || id.StartsWith("grok ", StringComparison.Ordinal)) return "xai";
        if (id == "gpt" || id.StartsWith("gpt-", StringComparison.Ordinal) || id.StartsWith("gpt ", StringComparison.Ordinal)) return "openai";
        return child.ModelProvider?.Trim().ToLowerInvariant() switch { "openai" => "openai", "xai" or "x.ai" => "xai", _ => "generic" };
    }
}
