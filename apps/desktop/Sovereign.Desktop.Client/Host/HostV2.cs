using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Sovereign.Desktop.Client.Host;

/// <summary>
/// Framing, canonical JSON and HMAC for published <c>sovereign.host/v2</c>.
/// Ported from the published packet <c>protocol-v2-reference.py</c>; not a second server.
/// </summary>
public static class HostV2
{
    public const string Protocol = "sovereign.host/v2";
    public const int MaxMessageBytes = 1024 * 1024;
    public const int MaxDepth = 32;
    public const long Int64Min = long.MinValue;
    public const long Int64Max = long.MaxValue;
    public static readonly byte[] MacPrefix = "SOVEREIGN-HOST-V2\0"u8.ToArray();

    public static readonly string[] RequestMethods =
    [
        "runtime.get", "health.get", "runs.list", "runs.get", "account.get",
        "decisions.list", "evidence.list", "evidence.get", "orders.list",
        "events.list", "capabilities.list", "evaluation.get",
        "offline_demo.start", "operations.get", "operations.cancel", "session.revoke"
    ];

    public static readonly string[] ReplyStates =
        ["COMPLETED", "ACCEPTED", "REJECTED", "TIMED_OUT", "UNKNOWN", "UNAVAILABLE"];

    private static readonly Regex Identity = new("^[A-Za-z0-9][A-Za-z0-9_.:-]{0,119}$", RegexOptions.Compiled);
    private static readonly Regex Nonce = new("^[A-Za-z0-9_-]{22,120}$", RegexOptions.Compiled);
    private static readonly Regex Hex64 = new("^[a-f0-9]{64}$", RegexOptions.Compiled);

    public static byte[] CanonicalJson(HostJson value, int depth = 0)
    {
        if (depth > MaxDepth)
        {
            throw new HostProtocolException("MAXIMUM_DEPTH_EXCEEDED");
        }

        using var ms = new MemoryStream();
        WriteCanonical(ms, value, depth);
        return ms.ToArray();
    }

    public static byte[] MacBytes(HostJson message)
    {
        var unsigned = message.Without("mac");
        var json = CanonicalJson(unsigned);
        var output = new byte[MacPrefix.Length + json.Length];
        MacPrefix.CopyTo(output, 0);
        json.CopyTo(output, MacPrefix.Length);
        return output;
    }

    public static HostJson Sign(HostJson message, byte[] secret)
    {
        var mac = Convert.ToHexString(HMACSHA256.HashData(secret, MacBytes(message))).ToLowerInvariant();
        return message.With("mac", HostJson.Str(mac));
    }

    public static void VerifyMac(HostJson message, byte[] secret)
    {
        var supplied = message["mac"].AsString();
        var expected = Convert.ToHexString(HMACSHA256.HashData(secret, MacBytes(message))).ToLowerInvariant();
        if (supplied is null)
        {
            throw new HostProtocolException("INVALID_MAC");
        }

        var a = Encoding.ASCII.GetBytes(supplied);
        var b = Encoding.ASCII.GetBytes(expected);
        if (a.Length != b.Length || !CryptographicOperations.FixedTimeEquals(a, b))
        {
            throw new HostProtocolException("INVALID_MAC");
        }
    }

    public static byte[] SecretFromB64(string value)
    {
        try
        {
            var result = Convert.FromBase64String(value);
            if (result.Length != 32)
            {
                throw new HostProtocolException("INVALID_SESSION_SECRET");
            }

            return result;
        }
        catch (FormatException ex)
        {
            throw new HostProtocolException("INVALID_SESSION_SECRET", ex);
        }
    }

    public static string SecretToB64(byte[] secret)
    {
        if (secret.Length != 32)
        {
            throw new HostProtocolException("INVALID_SESSION_SECRET");
        }

        return Convert.ToBase64String(secret);
    }

    public static string NewNonce()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string NewIdentity(string prefix)
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return prefix + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static HostJson SessionOpen(string requestId, string requestedProfile, string clientName, string clientVersion) =>
        HostJson.Obj(
            ("protocol", HostJson.Str(Protocol)),
            ("kind", HostJson.Str("session_open")),
            ("request_id", HostJson.Str(requestId)),
            ("requested_profile", HostJson.Str(requestedProfile)),
            ("client", HostJson.Obj(("name", HostJson.Str(clientName)), ("version", HostJson.Str(clientVersion)))));

    public static HostJson Request(
        string requestId,
        string sessionId,
        string nonce,
        string method,
        string? idempotencyKey,
        HostJson parameters) =>
        HostJson.Obj(
            ("protocol", HostJson.Str(Protocol)),
            ("kind", HostJson.Str("request")),
            ("request_id", HostJson.Str(requestId)),
            ("session_id", HostJson.Str(sessionId)),
            ("nonce", HostJson.Str(nonce)),
            ("method", HostJson.Str(method)),
            ("idempotency_key", idempotencyKey is null ? HostJson.Null : HostJson.Str(idempotencyKey)),
            ("params", parameters),
            ("mac", HostJson.Str(new string('0', 64))));

    public static void ValidateMessage(HostJson value)
    {
        _ = CanonicalJson(value);
        if (!value.IsObject || value["protocol"].AsString() != Protocol)
        {
            throw new HostProtocolException("UNSUPPORTED_PROTOCOL");
        }

        var kind = value["kind"].AsString();
        if (kind == "session_open")
        {
            RequireExact(value, ["protocol", "kind", "request_id", "requested_profile", "client"], "INVALID_SESSION_OPEN_FIELDS");
            var client = value["client"];
            if (!client.IsObject)
            {
                throw new HostProtocolException("INVALID_SESSION_OPEN_FIELDS");
            }

            RequireExact(client, ["name", "version"], "INVALID_CLIENT_FIELDS");
        }
        else if (kind == "session_opened")
        {
            RequireExact(value, ["protocol", "kind", "request_id", "status", "endpoint", "session"], "INVALID_SESSION_OPENED_FIELDS");
            if (value["status"].AsString() != "COMPLETED")
            {
                throw new HostProtocolException("INVALID_SESSION_OPENED_FIELDS");
            }

            var endpoint = value["endpoint"];
            if (!endpoint.IsObject)
            {
                throw new HostProtocolException("INVALID_ENDPOINT_FIELDS");
            }

            RequireExact(endpoint, ["pipe_name", "instance_id", "profile_id"], "INVALID_ENDPOINT_FIELDS");
            var session = value["session"];
            if (!session.IsObject)
            {
                throw new HostProtocolException("INVALID_SESSION_FIELDS");
            }

            RequireExact(session, ["session_id", "operator_sid", "issued_at", "expires_at", "authenticator"], "INVALID_SESSION_FIELDS");
            var authenticator = session["authenticator"];
            if (!authenticator.IsObject)
            {
                throw new HostProtocolException("INVALID_AUTHENTICATOR_FIELDS");
            }

            RequireExact(authenticator, ["algorithm", "key_b64"], "INVALID_AUTHENTICATOR_FIELDS");
            if (authenticator["algorithm"].AsString() != "HMAC-SHA-256")
            {
                throw new HostProtocolException("INVALID_AUTHENTICATOR");
            }

            _ = SecretFromB64(authenticator["key_b64"].AsString() ?? "");
            if (!IsIdentity(session["session_id"].AsString()))
            {
                throw new HostProtocolException("INVALID_SESSION_ID");
            }
        }
        else if (kind == "request")
        {
            RequireExact(value, ["protocol", "kind", "request_id", "session_id", "nonce", "method", "idempotency_key", "params", "mac"], "INVALID_REQUEST_FIELDS");
            var method = value["method"].AsString();
            if (method is null || !RequestMethods.Contains(method))
            {
                throw new HostProtocolException("UNKNOWN_METHOD");
            }

            if (!value["params"].IsObject)
            {
                throw new HostProtocolException("INVALID_PARAMS");
            }

            if (!value["idempotency_key"].IsNull && !IsIdentity(value["idempotency_key"].AsString()))
            {
                throw new HostProtocolException("INVALID_IDEMPOTENCY_KEY");
            }

            if (!Nonce.IsMatch(value["nonce"].AsString() ?? ""))
            {
                throw new HostProtocolException("INVALID_NONCE");
            }

            if (!Hex64.IsMatch(value["mac"].AsString() ?? ""))
            {
                throw new HostProtocolException("INVALID_MAC_SHAPE");
            }
        }
        else if (kind == "reply")
        {
            RequireExact(value, ["protocol", "kind", "request_id", "session_id", "server_nonce", "status", "snapshot_revision", "result", "error", "mac"], "INVALID_REPLY_FIELDS");
            var status = value["status"].AsString();
            if (status is null || !ReplyStates.Contains(status))
            {
                throw new HostProtocolException("INVALID_REPLY_STATUS");
            }

            if (!value["snapshot_revision"].IsInt || value["snapshot_revision"].Int < 0)
            {
                throw new HostProtocolException("INVALID_SNAPSHOT_REVISION");
            }

            if (!value["result"].IsNull && !value["result"].IsObject)
            {
                throw new HostProtocolException("INVALID_REPLY_RESULT");
            }

            if (!value["error"].IsNull)
            {
                var error = value["error"];
                if (!error.IsObject)
                {
                    throw new HostProtocolException("INVALID_REPLY_ERROR");
                }

                RequireExact(error, ["code", "message", "retryable"], "INVALID_REPLY_ERROR");
                if (error["code"].AsString() is null || error["message"].AsString() is null || !error["retryable"].IsBool)
                {
                    throw new HostProtocolException("INVALID_REPLY_ERROR");
                }
            }

            if (status is "COMPLETED" or "ACCEPTED" && !value["error"].IsNull)
            {
                throw new HostProtocolException("SUCCESS_WITH_ERROR");
            }

            if (status is "REJECTED" or "TIMED_OUT" or "UNKNOWN" or "UNAVAILABLE" && value["error"].IsNull)
            {
                throw new HostProtocolException("FAILURE_WITHOUT_ERROR");
            }

            if (!Nonce.IsMatch(value["server_nonce"].AsString() ?? ""))
            {
                throw new HostProtocolException("INVALID_SERVER_NONCE");
            }

            if (!Hex64.IsMatch(value["mac"].AsString() ?? ""))
            {
                throw new HostProtocolException("INVALID_MAC_SHAPE");
            }
        }
        else
        {
            throw new HostProtocolException("INVALID_MESSAGE_KIND");
        }

        if (!IsIdentity(value["request_id"].AsString()))
        {
            throw new HostProtocolException("INVALID_REQUEST_ID");
        }

        foreach (var field in new[] { "requested_profile", "session_id" })
        {
            if (value.Contains(field) && !IsIdentity(value[field].AsString()))
            {
                throw new HostProtocolException("INVALID_" + field.ToUpperInvariant());
            }
        }
    }

    public static byte[] EncodeFrame(HostJson message)
    {
        ValidateMessage(message);
        var body = CanonicalJson(message);
        if (body.Length > MaxMessageBytes)
        {
            throw new HostProtocolException("MESSAGE_TOO_LARGE");
        }

        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }

    public static HostJson DecodeFrame(ReadOnlySpan<byte> frame)
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

        var value = HostJsonParser.ParseUtf8(frame[4..].ToArray());
        if (!value.IsObject)
        {
            throw new HostProtocolException("MESSAGE_NOT_OBJECT");
        }

        ValidateMessage(value);
        return value;
    }

    public static HostJson ParseJsonObject(byte[] body)
    {
        var value = HostJsonParser.ParseUtf8(body);
        if (!value.IsObject)
        {
            throw new HostProtocolException("MESSAGE_NOT_OBJECT");
        }

        return value;
    }

    public static bool IsIdentity(string? value) => value is not null && Identity.IsMatch(value);

    public static void RequireUnicodeScalars(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsSurrogate(value[i]))
            {
                if (i + 1 >= value.Length || !char.IsSurrogatePair(value[i], value[i + 1]))
                {
                    throw new HostProtocolException("INVALID_UNICODE_SCALAR");
                }

                i++;
            }
        }
    }

    private static void RequireExact(HostJson value, string[] required, string error)
    {
        if (!value.IsObject || value.Count != required.Length || required.Any(k => !value.Contains(k)))
        {
            throw new HostProtocolException(error);
        }
    }

    private static void WriteCanonical(Stream dest, HostJson value, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new HostProtocolException("MAXIMUM_DEPTH_EXCEEDED");
        }

        switch (value.Kind)
        {
            case HostJsonKind.Null:
                dest.Write("null"u8);
                return;
            case HostJsonKind.Bool:
                dest.Write(value.Bool ? "true"u8 : "false"u8);
                return;
            case HostJsonKind.Int:
                dest.Write(Encoding.ASCII.GetBytes(value.Int.ToString(CultureInfo.InvariantCulture)));
                return;
            case HostJsonKind.String:
                WriteString(dest, value.String ?? throw new HostProtocolException("INVALID_UNICODE_SCALAR"));
                return;
            case HostJsonKind.Array:
                dest.WriteByte((byte)'[');
                var items = value.Array ?? [];
                for (var i = 0; i < items.Count; i++)
                {
                    if (i > 0)
                    {
                        dest.WriteByte((byte)',');
                    }

                    WriteCanonical(dest, items[i], depth + 1);
                }

                dest.WriteByte((byte)']');
                return;
            case HostJsonKind.Object:
                dest.WriteByte((byte)'{');
                var members = value.Object ?? new Dictionary<string, HostJson>();
                var keys = members.Keys.OrderBy(k => k, Utf8KeyComparer.Instance).ToArray();
                for (var i = 0; i < keys.Length; i++)
                {
                    if (i > 0)
                    {
                        dest.WriteByte((byte)',');
                    }

                    WriteString(dest, keys[i]);
                    dest.WriteByte((byte)':');
                    WriteCanonical(dest, members[keys[i]], depth + 1);
                }

                dest.WriteByte((byte)'}');
                return;
            default:
                throw new HostProtocolException("UNSUPPORTED_JSON_VALUE");
        }
    }

    private static void WriteString(Stream dest, string value)
    {
        RequireUnicodeScalars(value);
        dest.WriteByte((byte)'"');
        Span<byte> utf8 = stackalloc byte[4];
        foreach (var rune in value.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case '"':
                    dest.Write("\\\""u8);
                    break;
                case '\\':
                    dest.Write("\\\\"u8);
                    break;
                case '\b':
                    dest.Write("\\b"u8);
                    break;
                case '\f':
                    dest.Write("\\f"u8);
                    break;
                case '\n':
                    dest.Write("\\n"u8);
                    break;
                case '\r':
                    dest.Write("\\r"u8);
                    break;
                case '\t':
                    dest.Write("\\t"u8);
                    break;
                default:
                    if (rune.Value < 0x20)
                    {
                        dest.Write(Encoding.ASCII.GetBytes("\\u" + rune.Value.ToString("x4", CultureInfo.InvariantCulture)));
                    }
                    else
                    {
                        utf8.Clear();
                        var n = rune.EncodeToUtf8(utf8);
                        dest.Write(utf8[..n]);
                    }

                    break;
            }
        }

        dest.WriteByte((byte)'"');
    }

    private sealed class Utf8KeyComparer : IComparer<string>
    {
        public static readonly Utf8KeyComparer Instance = new();

        public int Compare(string? x, string? y) =>
            MemoryExtensions.SequenceCompareTo(Encoding.UTF8.GetBytes(x ?? ""), Encoding.UTF8.GetBytes(y ?? ""));
    }
}
