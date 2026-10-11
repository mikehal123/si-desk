using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sovereign.Desktop.Core.Desk;

/// <summary>Closed subset used by the embedded v1 schemas, not a general JSON Schema implementation.</summary>
internal static class ContractShape
{
    private static readonly HashSet<string> Keywords = new("$ref $defs $schema $id type const enum properties required additionalProperties items minItems maxItems uniqueItems minimum maximum minLength maxLength pattern format anyOf allOf not if then else contains minContains maxContains propertyNames".Split(' '));
    public static void Require(JsonElement value, JsonElement schema, JsonElement definitions)
    {
        _ = CanonicalJson.Encode(value); // No floats, invalid Unicode, non-ASCII keys or oversized integers.
        if (!Matches(value, schema, definitions)) throw new DeskContractException("INVALID_INPUT", "Payload does not match the method contract.");
    }
    private static bool Matches(JsonElement v, JsonElement s, JsonElement defs)
    {
        foreach (var key in s.EnumerateObject()) if (!Keywords.Contains(key.Name)) throw new DeskContractException("SCHEMA_INCOMPATIBLE", "Unsupported contract schema keyword.");
        if (s.TryGetProperty("$ref", out var reference))
        {
            var name = reference.GetString()!;
            if (!name.StartsWith("#/$defs/", StringComparison.Ordinal)) return false;
            if (!Matches(v, defs.GetProperty(name[8..]), defs)) return false;
        }
        if (s.TryGetProperty("const", out var constant) && !JsonElement.DeepEquals(v, constant)) return false;
        if (s.TryGetProperty("enum", out var choices) && !choices.EnumerateArray().Any(c => JsonElement.DeepEquals(v, c))) return false;
        if (s.TryGetProperty("anyOf", out var any) && !any.EnumerateArray().Any(c => Matches(v, c, defs))) return false;
        if (s.TryGetProperty("allOf", out var all) && !all.EnumerateArray().All(c => Matches(v, c, defs))) return false;
        if (s.TryGetProperty("not", out var not) && Matches(v, not, defs)) return false;
        if (s.TryGetProperty("if", out var condition))
        {
            var branch = Matches(v, condition, defs) ? "then" : "else";
            if (s.TryGetProperty(branch, out var body) && !Matches(v, body, defs)) return false;
        }
        if (s.TryGetProperty("type", out var type))
        {
            var valid = type.GetString() switch
            {
                "object" => v.ValueKind == JsonValueKind.Object, "array" => v.ValueKind == JsonValueKind.Array,
                "string" => v.ValueKind == JsonValueKind.String, "integer" => v.TryGetInt64Safe(),
                "boolean" => v.ValueKind is JsonValueKind.True or JsonValueKind.False, "null" => v.ValueKind == JsonValueKind.Null,
                _ => false
            };
            if (!valid) return false;
        }
        if (v.ValueKind == JsonValueKind.Object)
        {
            var hasProps = s.TryGetProperty("properties", out var props);
            if (s.TryGetProperty("required", out var required) && required.EnumerateArray().Any(k => !v.TryGetProperty(k.GetString()!, out _))) return false;
            foreach (var item in v.EnumerateObject())
            {
                if (s.TryGetProperty("propertyNames", out var names) && !Matches(JsonSerializer.SerializeToElement(item.Name), names, defs)) return false;
                if (hasProps && props.TryGetProperty(item.Name, out var child)) { if (!Matches(item.Value, child, defs)) return false; }
                else if (s.TryGetProperty("additionalProperties", out var extra))
                {
                    if (extra.ValueKind == JsonValueKind.False) return false;
                    if (extra.ValueKind == JsonValueKind.Object && !Matches(item.Value, extra, defs)) return false;
                }
            }
        }
        if (v.ValueKind == JsonValueKind.Array)
        {
            var elements = v.EnumerateArray().ToArray();
            if (!Bounds(elements.Length, s, "minItems", "maxItems")) return false;
            if (s.TryGetProperty("items", out var items) && !elements.All(i => Matches(i, items, defs))) return false;
            if (s.TryGetProperty("uniqueItems", out var unique) && unique.GetBoolean())
                for (var i = 0; i < elements.Length; i++) for (var j = 0; j < i; j++) if (JsonElement.DeepEquals(elements[i], elements[j])) return false;
            if (s.TryGetProperty("contains", out var contains))
            {
                var count = elements.Count(i => Matches(i, contains, defs));
                if (count < (s.TryGetProperty("minContains", out var min) ? min.GetInt32() : 1) || !Bounds(count, s, "minContains", "maxContains")) return false;
            }
        }
        if (v.ValueKind == JsonValueKind.String)
        {
            var text = v.GetString()!;
            if (!Bounds(text.EnumerateRunes().Count(), s, "minLength", "maxLength")) return false;
            if (s.TryGetProperty("pattern", out var pattern) && !Regex.IsMatch(text, pattern.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) return false;
            if (s.TryGetProperty("format", out var format))
            {
                var valid = format.GetString() switch
                {
                    "date" => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                    "date-time" => text.Contains('T') && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                    "uri" => Uri.TryCreate(text, UriKind.Absolute, out _), _ => false
                };
                if (!valid) return false;
            }
        }
        return v.ValueKind != JsonValueKind.Number || v.TryGetInt64(out var n) && Bounds(n, s, "minimum", "maximum");
    }
    private static bool TryGetInt64Safe(this JsonElement v) => v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out _);
    private static bool Bounds(long n, JsonElement s, string lower, string upper) =>
        (!s.TryGetProperty(lower, out var lo) || n >= lo.GetInt64()) && (!s.TryGetProperty(upper, out var hi) || n <= hi.GetInt64());
}
