using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sovereign.Desktop.Core.Desk;

/// <summary>Desk canonical JSON v1. Decimal quantities remain strings; only integer JSON numbers are allowed.</summary>
public static partial class CanonicalJson
{
    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex DecimalPattern();

    public static string NormalizeDecimal(string value)
    {
        if (value.Length > 256 || !DecimalPattern().IsMatch(value))
            throw new DeskContractException("INVALID_INPUT", "Expected a finite plain decimal string.");
        var normalized = value.Contains('.') ? value.TrimEnd('0').TrimEnd('.') : value;
        return normalized == "-0" ? "0" : normalized;
    }

    public static byte[] Encode(JsonElement value)
    {
        var output = new StringBuilder();
        Write(value, output);
        return Encoding.UTF8.GetBytes(output.ToString());
    }

    public static string Hash(JsonElement value) => Convert.ToHexStringLower(SHA256.HashData(Encode(value)));

    private static void Write(JsonElement value, StringBuilder output)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = value.EnumerateObject().ToArray();
                if (properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
                    throw new DeskContractException("INVALID_INPUT", "Duplicate object property.");
                if (properties.Any(p => p.Name.Any(c => c is < ' ' or > '~')))
                    throw new DeskContractException("INVALID_INPUT", "Canonical object keys must be printable ASCII.");
                output.Append('{');
                var first = true;
                foreach (var property in properties.OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first) output.Append(',');
                    first = false;
                    Quote(property.Name, output); output.Append(':'); Write(property.Value, output);
                }
                output.Append('}'); break;
            case JsonValueKind.Array:
                output.Append('['); var separator = false;
                foreach (var child in value.EnumerateArray())
                {
                    if (separator) output.Append(','); separator = true; Write(child, output);
                }
                output.Append(']'); break;
            case JsonValueKind.String: Quote(value.GetString()!, output); break;
            case JsonValueKind.Number:
                if (!value.TryGetInt64(out var number))
                    throw new DeskContractException("INVALID_INPUT", "JSON numbers must be signed 64-bit integers. Use decimal strings for quantities.");
                output.Append(number.ToString(CultureInfo.InvariantCulture)); break;
            case JsonValueKind.True: output.Append("true"); break;
            case JsonValueKind.False: output.Append("false"); break;
            case JsonValueKind.Null: output.Append("null"); break;
            default: throw new DeskContractException("INVALID_INPUT", "Unsupported JSON value.");
        }
    }

    // ASCII escaping avoids runtime-specific HTML/Unicode escaping. Unicode is represented
    // as lowercase UTF-16 escape pairs; this is still a UTF-8 encoded JSON document.
    private static void Quote(string value, StringBuilder output)
    {
        output.Append('"');
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && (i + 1 == value.Length || !char.IsLowSurrogate(value[i + 1])) ||
                char.IsLowSurrogate(c) && (i == 0 || !char.IsHighSurrogate(value[i - 1])))
                throw new DeskContractException("INVALID_INPUT", "Unpaired Unicode surrogate.");
            switch (c)
            {
                case '"': output.Append("\\\""); break;
                case '\\': output.Append("\\\\"); break;
                case '\b': output.Append("\\b"); break;
                case '\f': output.Append("\\f"); break;
                case '\n': output.Append("\\n"); break;
                case '\r': output.Append("\\r"); break;
                case '\t': output.Append("\\t"); break;
                default:
                    if (c < ' ' || c > '~') output.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else output.Append(c);
                    break;
            }
        }
        output.Append('"');
    }
}
