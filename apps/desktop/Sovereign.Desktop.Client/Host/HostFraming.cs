using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Sovereign.Desktop.Client.Host;

/// <summary>
/// Framing compatible with the observed native host-v1 contract.
/// Desktop proposals for query methods are separate; this only encodes command/reply envelopes.
/// </summary>
public static class HostFraming
{
    public const string Protocol = "sovereign.host/v1";
    public const int MaxMessageBytes = 1024 * 1024;
    public static readonly string[] ReplyStates = ["ACCEPTED", "COMPLETED", "REJECTED", "TIMED_OUT", "UNKNOWN"];

    private static readonly Regex Identity = new("^[A-Za-z0-9][A-Za-z0-9_.:-]{0,119}$", RegexOptions.Compiled);
    private static readonly Regex Hex64 = new("^[a-f0-9]{64}$", RegexOptions.Compiled);
    private static readonly Regex Nonce = new("^[A-Za-z0-9_.:-]{16,120}$", RegexOptions.Compiled);

    public static byte[] EncodeFrame(JsonObject message)
    {
        ValidateEnvelope(message);
        var json = CanonicalJson(message);
        var body = Encoding.UTF8.GetBytes(json);
        if (body.Length > MaxMessageBytes)
        {
            throw new HostProtocolException("MESSAGE_TOO_LARGE");
        }

        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }

    public static JsonObject DecodeFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 4)
        {
            throw new HostProtocolException("INCOMPLETE_FRAME");
        }

        var size = BinaryPrimitives.ReadUInt32LittleEndian(frame);
        if (size > MaxMessageBytes)
        {
            throw new HostProtocolException("MESSAGE_TOO_LARGE");
        }

        if (frame.Length != size + 4)
        {
            throw new HostProtocolException("FRAME_LENGTH_MISMATCH");
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(Encoding.UTF8.GetString(frame[4..]));
        }
        catch (Exception ex)
        {
            throw new HostProtocolException("INVALID_JSON", ex);
        }

        if (node is not JsonObject obj)
        {
            throw new HostProtocolException("INVALID_JSON");
        }

        ValidateEnvelope(obj);
        return obj;
    }

    public static void ValidateEnvelope(JsonObject value)
    {
        if (!value.TryGetPropertyValue("protocol", out var protocol) || protocol?.GetValue<string>() != Protocol)
        {
            throw new HostProtocolException("UNSUPPORTED_PROTOCOL");
        }

        var kind = value["kind"]?.GetValue<string>();
        if (kind == "command")
        {
            RequireExact(value, ["protocol", "kind", "request_id", "session_id", "authority_digest", "account_id", "expected_revision", "owner_generation", "nonce", "action", "payload", "mac"], "INVALID_COMMAND_FIELDS");
            if (value["payload"] is not JsonObject)
            {
                throw new HostProtocolException("INVALID_COMMAND_PAYLOAD");
            }

            RequireNonNegativeInt(value, "expected_revision");
            RequireNonNegativeInt(value, "owner_generation");
            RequireIdentity(value, "session_id", "INVALID_COMMAND_SESSION_ID");
            RequireIdentity(value, "account_id", "INVALID_COMMAND_ACCOUNT_ID");
            RequireIdentity(value, "action", "INVALID_COMMAND_ACTION");
            RequireMatch(value, "authority_digest", Hex64, "INVALID_COMMAND_AUTHORITY_DIGEST");
            RequireMatch(value, "mac", Hex64, "INVALID_COMMAND_MAC");
            RequireMatch(value, "nonce", Nonce, "INVALID_COMMAND_NONCE");
        }
        else if (kind == "reply")
        {
            RequireExact(value, ["protocol", "kind", "request_id", "status", "result", "error"], "INVALID_REPLY_FIELDS");
            var status = value["status"]?.GetValue<string>();
            if (status is null || !ReplyStates.Contains(status))
            {
                throw new HostProtocolException("INVALID_REPLY_STATUS");
            }

            if (value["result"] is not null && value["result"] is not JsonObject)
            {
                throw new HostProtocolException("INVALID_REPLY_RESULT");
            }

            if (value["error"] is not null)
            {
                if (value["error"] is not JsonObject error)
                {
                    throw new HostProtocolException("INVALID_REPLY_ERROR");
                }

                foreach (var prop in error)
                {
                    if (prop.Key is not ("code" or "message"))
                    {
                        throw new HostProtocolException("INVALID_REPLY_ERROR");
                    }
                }

                if (error["code"] is null)
                {
                    throw new HostProtocolException("INVALID_REPLY_ERROR");
                }
            }
        }
        else
        {
            throw new HostProtocolException("INVALID_MESSAGE_KIND");
        }

        RequireIdentity(value, "request_id", "INVALID_REQUEST_ID");
    }

    public static string CanonicalJson(JsonObject obj)
    {
        // Match Python json.dumps(sort_keys=True, separators=(",", ":"), allow_nan=False)
        var ordered = SortNode(obj);
        return ordered.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    private static JsonNode SortNode(JsonNode node)
    {
        if (node is JsonObject o)
        {
            var n = new JsonObject();
            foreach (var kv in o.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                n[kv.Key] = kv.Value is null ? null : SortNode(kv.Value);
            }

            return n;
        }

        if (node is JsonArray a)
        {
            var n = new JsonArray();
            foreach (var item in a)
            {
                n.Add(item is null ? null : SortNode(item));
            }

            return n;
        }

        return node.DeepClone();
    }

    private static void RequireExact(JsonObject value, string[] required, string error)
    {
        if (value.Count != required.Length || required.Any(k => !value.ContainsKey(k)))
        {
            throw new HostProtocolException(error);
        }
    }

    private static void RequireNonNegativeInt(JsonObject value, string field)
    {
        if (value[field] is not JsonValue jv || !jv.TryGetValue<int>(out var n) || n < 0)
        {
            throw new HostProtocolException("INVALID_COMMAND_REVISION");
        }
    }

    private static void RequireIdentity(JsonObject value, string field, string error)
    {
        var s = value[field]?.GetValue<string>();
        if (s is null || !Identity.IsMatch(s))
        {
            throw new HostProtocolException(error);
        }
    }

    private static void RequireMatch(JsonObject value, string field, Regex regex, string error)
    {
        var s = value[field]?.GetValue<string>();
        if (s is null || !regex.IsMatch(s))
        {
            throw new HostProtocolException(error);
        }
    }
}

public sealed class HostProtocolException : Exception
{
    public HostProtocolException(string code, Exception? inner = null) : base(code, inner) => Code = code;

    public string Code { get; }
}
