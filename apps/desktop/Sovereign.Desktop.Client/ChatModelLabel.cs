using System.Text;

namespace Sovereign.Desktop.Client;

/// <summary>Readable chat-model labels. Provider IDs and advertised options stay unchanged.</summary>
public static class ChatModelLabel
{
    public const char MiddleDot = '\u00B7';

    public sealed record Choice(string Id, string Label, string Details);

    public static string Display(string id, string? name = null) => Assign([(id, name)])[0].Label;
    public static string Details(string id, string? name = null) => Assign([(id, name)])[0].Details;

    public static string RepairText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var replaced = text.Replace("Â" + MiddleDot, MiddleDot.ToString(), StringComparison.Ordinal)
            .Replace("Â·", MiddleDot.ToString(), StringComparison.Ordinal);
        if (replaced.IndexOf('\u00C2') < 0 && replaced.IndexOf('\u00C3') < 0) return replaced;
        var bytes = new byte[replaced.Length];
        for (var i = 0; i < replaced.Length; i++)
        {
            if (replaced[i] > 0xFF) return replaced;
            bytes[i] = (byte)replaced[i];
        }
        var decoded = Encoding.UTF8.GetString(bytes);
        return decoded.Contains('\uFFFD') ? replaced : decoded;
    }

    public static Choice[] Assign(IReadOnlyList<(string Id, string? Name)> models)
    {
        if (models.Count == 0) return [];
        var parsed = models.Select(model => Parse(model.Id ?? "", model.Name)).ToArray();
        var primary = parsed.Select(PrimaryLabel).ToArray();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var label in primary) counts[label] = counts.GetValueOrDefault(label) + 1;

        var labels = new string[parsed.Length];
        for (var i = 0; i < parsed.Length; i++)
        {
            labels[i] = counts[primary[i]] == 1
                ? primary[i]
                : Distinguish(parsed[i], parsed.Where((candidate, j) => j != i && primary[j] == primary[i]).ToArray(), primary[i]);
        }

        var remaining = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var label in labels) remaining[label] = remaining.GetValueOrDefault(label) + 1;
        for (var i = 0; i < parsed.Length; i++)
        {
            if (remaining[labels[i]] == 1) continue;
            var extra = parsed[i].Options.TryGetValue("context", out var context) && context.Length > 0
                ? context
                : parsed[i].BaseId;
            labels[i] = Compact(labels[i] + " " + extra);
        }

        return parsed.Select((item, i) => new Choice(item.Id, labels[i], BuildDetails(item, labels[i]))).ToArray();
    }

    private readonly record struct Parsed(string Id, string BaseId, string DisplayName, Dictionary<string, string> Options);

    private static Parsed Parse(string id, string? name)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var baseId = id;
        var open = id.IndexOf('[');
        var close = id.LastIndexOf(']');
        if (open > 0 && close > open)
        {
            baseId = id[..open];
            foreach (var part in id[(open + 1)..close].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0) options[part] = "true";
                else options[part[..eq].Trim()] = part[(eq + 1)..].Trim();
            }
        }
        return new Parsed(id, baseId, ChooseName(id, baseId, name), options);
    }

    private static string ChooseName(string id, string baseId, string? name)
    {
        var repaired = RepairText(name ?? "").Trim();
        var separator = repaired.IndexOf(MiddleDot);
        if (separator >= 0) repaired = repaired[..separator].Trim();
        repaired = repaired.Replace("Â", "", StringComparison.Ordinal).Trim();
        if (string.IsNullOrWhiteSpace(repaired)
            || string.Equals(repaired, id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(repaired, baseId, StringComparison.OrdinalIgnoreCase)
            || repaired.Contains('[', StringComparison.Ordinal)
            || LooksLikeId(repaired))
            return PrettySlug(baseId);
        return repaired;
    }

    private static bool LooksLikeId(string text) =>
        text.Length > 0 && !text.Contains(' ') && text.Any(ch => ch is '-' or '_' or '/');

    private static string PrettySlug(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return "Model";
        var tokens = slug.Replace('_', '-').Replace(':', '-').Replace('/', '-').Split('-', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", tokens.Select(PrettyToken));
    }

    private static string PrettyToken(string token)
    {
        if (token.Equals("gpt", StringComparison.OrdinalIgnoreCase)) return "GPT";
        if (token.Equals("glm", StringComparison.OrdinalIgnoreCase)) return "GLM";
        if (token.Equals("ai", StringComparison.OrdinalIgnoreCase)) return "AI";
        if (token.All(ch => char.IsDigit(ch) || ch == '.')) return token;
        return char.ToUpperInvariant(token[0]) + token[1..];
    }

    private static string PrimaryLabel(Parsed model)
    {
        var parts = new List<string> { model.DisplayName };
        if (MappedValue(model, "reasoning_effort", "effort") is { } effort) parts.Add(effort);
        if (ThinkingLabel(model) is { } thinking) parts.Add(thinking);
        if (IsTrue(model, "fast")) parts.Add("Fast");
        return Compact(string.Join(" ", parts));
    }

    private static string? ThinkingLabel(Parsed model)
    {
        if (!TryOption(model, "thinking", out var thinking) && !TryOption(model, "reasoning", out thinking)) return null;
        if (IsFalse(thinking)) return null;
        return "Thinking";
    }

    private static string? MappedValue(Parsed model, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!TryOption(model, key, out var value) || IsFalse(value)) continue;
            if (IsTrue(value)) return TitleWords(key.Replace('_', ' '));
            return TitleWords(value);
        }
        return null;
    }

    private static string Distinguish(Parsed model, Parsed[] others, string primary)
    {
        var keys = model.Options.Keys
            .Concat(others.SelectMany(other => other.Options.Keys))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(RankKey)
            .ThenBy(key => key, StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            model.Options.TryGetValue(key, out var mine);
            if (string.IsNullOrEmpty(mine)) continue;
            if (!others.Any(other => !other.Options.TryGetValue(key, out var theirs)
                || !string.Equals(theirs, mine, StringComparison.OrdinalIgnoreCase))) continue;
            if (IsDistinctionKey(key) && primary.Contains(TitleWords(mine), StringComparison.OrdinalIgnoreCase)) continue;
            return Compact(primary + " " + PrettyOptionValue(key, mine));
        }
        return Compact(primary + " " + model.BaseId);
    }

    private static bool IsDistinctionKey(string key) =>
        key.Equals("reasoning_effort", StringComparison.OrdinalIgnoreCase)
        || key.Equals("effort", StringComparison.OrdinalIgnoreCase)
        || key.Equals("thinking", StringComparison.OrdinalIgnoreCase)
        || key.Equals("reasoning", StringComparison.OrdinalIgnoreCase)
        || key.Equals("fast", StringComparison.OrdinalIgnoreCase);

    private static int RankKey(string key) => key.Equals("context", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

    private static string PrettyOptionValue(string key, string value) =>
        key.Equals("context", StringComparison.OrdinalIgnoreCase) ? value : TitleWords(value);

    private static string TitleWords(string text) =>
        string.Join(" ", text.Replace('_', '-').Split('-', StringSplitOptions.RemoveEmptyEntries).Select(PrettyToken));

    private static bool TryOption(Parsed model, string key, out string value) => model.Options.TryGetValue(key, out value!);

    private static bool IsTrue(Parsed model, string key) => TryOption(model, key, out var value) && IsTrue(value);
    private static bool IsTrue(string value) => value.Equals("true", StringComparison.OrdinalIgnoreCase) || value is "1" or "yes" or "on";
    private static bool IsFalse(string value) => value.Equals("false", StringComparison.OrdinalIgnoreCase)
        || value.Equals("none", StringComparison.OrdinalIgnoreCase) || value is "0" or "no" or "off";

    private static string Compact(string text)
    {
        var trimmed = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return trimmed.Length <= 72 ? trimmed : trimmed[..71] + "…";
    }

    private static string BuildDetails(Parsed model, string label)
    {
        var lines = new List<string> { label, "Provider ID: " + model.Id };
        foreach (var key in model.Options.Keys.OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
            lines.Add(key.Replace('_', ' ') + ": " + model.Options[key]);
        return string.Join("\r\n", lines);
    }
}
