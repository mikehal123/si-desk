using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Sovereign.Desktop.Client.Host;

public enum HostJsonKind
{
    Null,
    Bool,
    Int,
    String,
    Array,
    Object
}

public sealed class HostJson
{
    public HostJsonKind Kind { get; }
    public bool Bool { get; }
    public long Int { get; }
    public string? String { get; }
    public IReadOnlyList<HostJson>? Array { get; }
    public IReadOnlyDictionary<string, HostJson>? Object { get; }

    private HostJson(HostJsonKind kind, bool b = false, long n = 0, string? s = null, IReadOnlyList<HostJson>? a = null, IReadOnlyDictionary<string, HostJson>? o = null)
    {
        Kind = kind;
        Bool = b;
        Int = n;
        String = s;
        Array = a;
        Object = o;
    }

    public static HostJson Null { get; } = new(HostJsonKind.Null);
    public static HostJson True { get; } = new(HostJsonKind.Bool, b: true);
    public static HostJson False { get; } = new(HostJsonKind.Bool, b: false);
    public static HostJson BoolValue(bool value) => value ? True : False;
    public static HostJson IntValue(long value) => new(HostJsonKind.Int, n: value);
    public static HostJson Str(string value) => new(HostJsonKind.String, s: value);
    public static HostJson Arr(IReadOnlyList<HostJson> items) => new(HostJsonKind.Array, a: items);
    public static HostJson Obj(IReadOnlyDictionary<string, HostJson> members) => new(HostJsonKind.Object, o: members);

    public static HostJson Obj(params (string Key, HostJson Value)[] members)
    {
        var d = new Dictionary<string, HostJson>(members.Length, StringComparer.Ordinal);
        foreach (var (key, value) in members)
        {
            d[key] = value;
        }

        return Obj(d);
    }

    public bool IsNull => Kind == HostJsonKind.Null;
    public bool IsObject => Kind == HostJsonKind.Object;
    public bool IsInt => Kind == HostJsonKind.Int;
    public bool IsString => Kind == HostJsonKind.String;
    public bool IsArray => Kind == HostJsonKind.Array;
    public bool IsBool => Kind == HostJsonKind.Bool;

    public HostJson this[string key] =>
        Object is not null && Object.TryGetValue(key, out var value) ? value : Null;

    public bool Contains(string key) => Object is not null && Object.ContainsKey(key);

    public string? AsString() => Kind == HostJsonKind.String ? String : null;

    public long? AsInt() => Kind == HostJsonKind.Int ? Int : null;

    public bool? AsBool() => Kind == HostJsonKind.Bool ? Bool : null;

    public IReadOnlyList<HostJson> AsArray() => Array ?? [];

    public IReadOnlyCollection<string> Keys => Object?.Keys.ToArray() ?? [];

    public int Count => Object?.Count ?? 0;

    public HostJson Without(string key)
    {
        if (Object is null)
        {
            return this;
        }

        var copy = new Dictionary<string, HostJson>(Object, StringComparer.Ordinal);
        copy.Remove(key);
        return Obj(copy);
    }

    public HostJson With(string key, HostJson value)
    {
        var copy = Object is null
            ? new Dictionary<string, HostJson>(StringComparer.Ordinal)
            : new Dictionary<string, HostJson>(Object, StringComparer.Ordinal);
        copy[key] = value;
        return Obj(copy);
    }
}

public static class HostJsonParser
{
    public static HostJson Parse(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64
        });
        try
        {
            var value = ReadValue(ref reader, 0);
            if (reader.Read())
            {
                throw new HostProtocolException("INVALID_JSON");
            }

            return value;
        }
        catch (HostProtocolException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new HostProtocolException("INVALID_JSON", ex);
        }
    }

    public static HostJson ParseUtf8(byte[] body)
    {
        var strict = Encoding.GetEncoding("utf-8", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        try
        {
            _ = strict.GetString(body);
        }
        catch (DecoderFallbackException ex)
        {
            throw new HostProtocolException("INVALID_UTF8", ex);
        }

        return Parse(body);
    }

    private static HostJson ReadValue(ref Utf8JsonReader reader, int depth)
    {
        if (depth > HostV2.MaxDepth)
        {
            throw new HostProtocolException("MAXIMUM_DEPTH_EXCEEDED");
        }

        if (!reader.Read())
        {
            throw new HostProtocolException("INVALID_JSON");
        }

        return reader.TokenType switch
        {
            JsonTokenType.Null => HostJson.Null,
            JsonTokenType.True => HostJson.True,
            JsonTokenType.False => HostJson.False,
            JsonTokenType.String => ReadString(ref reader),
            JsonTokenType.Number => ReadNumber(ref reader),
            JsonTokenType.StartObject => ReadObject(ref reader, depth + 1),
            JsonTokenType.StartArray => ReadArray(ref reader, depth + 1),
            _ => throw new HostProtocolException("INVALID_JSON")
        };
    }

    private static HostJson ReadString(ref Utf8JsonReader reader)
    {
        var s = reader.GetString() ?? throw new HostProtocolException("INVALID_JSON");
        HostV2.RequireUnicodeScalars(s);
        return HostJson.Str(s);
    }

    private static HostJson ReadNumber(ref Utf8JsonReader reader)
    {
        string raw;
        if (reader.HasValueSequence)
        {
            raw = Encoding.UTF8.GetString(reader.ValueSequence.ToArray());
        }
        else
        {
            raw = Encoding.UTF8.GetString(reader.ValueSpan);
        }
        if (raw.Contains('.') || raw.Contains('e', StringComparison.OrdinalIgnoreCase))
        {
            throw new HostProtocolException("FLOAT_NOT_SUPPORTED");
        }

        if (!long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
        {
            throw new HostProtocolException("INTEGER_OUT_OF_RANGE");
        }

        return HostJson.IntValue(n);
    }

    private static HostJson ReadObject(ref Utf8JsonReader reader, int depth)
    {
        if (depth > HostV2.MaxDepth)
        {
            throw new HostProtocolException("MAXIMUM_DEPTH_EXCEEDED");
        }

        var members = new Dictionary<string, HostJson>(StringComparer.Ordinal);
        while (true)
        {
            if (!reader.Read())
            {
                throw new HostProtocolException("INVALID_JSON");
            }

            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return HostJson.Obj(members);
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new HostProtocolException("INVALID_JSON");
            }

            var key = reader.GetString() ?? throw new HostProtocolException("OBJECT_KEY_NOT_STRING");
            HostV2.RequireUnicodeScalars(key);
            if (members.ContainsKey(key))
            {
                throw new HostProtocolException("DUPLICATE_JSON_KEY");
            }

            members[key] = ReadValue(ref reader, depth);
        }
    }

    private static HostJson ReadArray(ref Utf8JsonReader reader, int depth)
    {
        if (depth > HostV2.MaxDepth)
        {
            throw new HostProtocolException("MAXIMUM_DEPTH_EXCEEDED");
        }

        var items = new List<HostJson>();
        while (true)
        {
            if (!reader.Read())
            {
                throw new HostProtocolException("INVALID_JSON");
            }

            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return HostJson.Arr(items);
            }

            items.Add(ReadValueFromCurrent(ref reader, depth));
        }
    }

    private static HostJson ReadValueFromCurrent(ref Utf8JsonReader reader, int depth)
    {
        if (depth > HostV2.MaxDepth)
        {
            throw new HostProtocolException("MAXIMUM_DEPTH_EXCEEDED");
        }

        return reader.TokenType switch
        {
            JsonTokenType.Null => HostJson.Null,
            JsonTokenType.True => HostJson.True,
            JsonTokenType.False => HostJson.False,
            JsonTokenType.String => ReadString(ref reader),
            JsonTokenType.Number => ReadNumber(ref reader),
            JsonTokenType.StartObject => ReadObject(ref reader, depth + 1),
            JsonTokenType.StartArray => ReadArray(ref reader, depth + 1),
            _ => throw new HostProtocolException("INVALID_JSON")
        };
    }
}
