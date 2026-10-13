using System.Text;
using System.Text.Json.Nodes;
using Sovereign.Desktop.Client.Host;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class HostV2Tests
{
    private static string Packet => Path.Combine(AppContext.BaseDirectory, "contracts", "observed", "n2-host-contract", "2.0.0");

    [Theory]
    [InlineData("2.0.0")]
    [InlineData("2.1.0")]
    public void Published_mac_vector_matches_byte_for_byte(string packetVersion)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "contracts", "observed", "n2-host-contract", packetVersion);
        var vectors = HostJsonParser.ParseUtf8(File.ReadAllBytes(Path.Combine(root, "mac-vectors-v2.json")));
        var first = vectors["vectors"].AsArray()[0];
        var messageFile = first["message_file"].AsString()!;
        var message = HostV2.ParseJsonObject(File.ReadAllBytes(Path.Combine(root, messageFile)));
        var secret = Convert.FromHexString(vectors["secret_hex"].AsString()!);
        Assert.Equal(first["mac_input_hex"].AsString(), Convert.ToHexString(HostV2.MacBytes(message)).ToLowerInvariant());
        var signed = HostV2.Sign(message, secret);
        Assert.Equal(first["hmac_sha256_hex"].AsString(), signed["mac"].AsString());
        HostV2.VerifyMac(signed, secret);
    }

    [Fact]
    public void Reordered_fields_do_not_change_mac()
    {
        var original = HostV2.ParseJsonObject(File.ReadAllBytes(Path.Combine(Packet, "offline-start-v2.json")));
        var reordered = HostJson.Obj(
            ("mac", original["mac"]),
            ("params", original["params"]),
            ("method", original["method"]),
            ("idempotency_key", original["idempotency_key"]),
            ("nonce", original["nonce"]),
            ("session_id", original["session_id"]),
            ("request_id", original["request_id"]),
            ("kind", original["kind"]),
            ("protocol", original["protocol"]));
        var secret = Convert.FromHexString("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        Assert.Equal(HostV2.Sign(original, secret)["mac"].AsString(), HostV2.Sign(reordered, secret)["mac"].AsString());
    }

    [Fact]
    public void Session_open_example_validates()
    {
        var open = HostV2.ParseJsonObject(File.ReadAllBytes(Path.Combine(Packet, "session-open-v2.json")));
        HostV2.ValidateMessage(open);
        var frame = HostV2.EncodeFrame(open);
        var decoded = HostV2.DecodeFrame(frame);
        Assert.Equal("session_open", decoded["kind"].AsString());
        Assert.Equal("grok-integration-1", decoded["requested_profile"].AsString());
    }

    [Fact]
    public void Duplicate_keys_floats_and_nonfinite_are_refused()
    {
        var dup = Encoding.UTF8.GetBytes("""{"protocol":"sovereign.host/v2","protocol":"x"}""");
        var ex = Assert.Throws<HostProtocolException>(() => HostV2.DecodeFrame(Frame(dup)));
        Assert.Equal("DUPLICATE_JSON_KEY", ex.Code);

        var flt = Encoding.UTF8.GetBytes("""{"x":1.5}""");
        Assert.Equal("FLOAT_NOT_SUPPORTED", Assert.Throws<HostProtocolException>(() => HostJsonParser.ParseUtf8(flt)).Code);
    }

    [Fact]
    public void Int64_boundaries_are_accepted_and_overflows_refused()
    {
        var min = HostJsonParser.ParseUtf8(Encoding.UTF8.GetBytes("{\"n\":-9223372036854775808}"));
        var max = HostJsonParser.ParseUtf8(Encoding.UTF8.GetBytes("{\"n\":9223372036854775807}"));
        Assert.Equal(long.MinValue, min["n"].Int);
        Assert.Equal(long.MaxValue, max["n"].Int);
        Assert.Equal("INTEGER_OUT_OF_RANGE", Assert.Throws<HostProtocolException>(() => HostJsonParser.ParseUtf8(Encoding.UTF8.GetBytes("{\"n\":9223372036854775808}"))).Code);
        Assert.Equal("INTEGER_OUT_OF_RANGE", Assert.Throws<HostProtocolException>(() => HostJsonParser.ParseUtf8(Encoding.UTF8.GetBytes("{\"n\":-9223372036854775809}"))).Code);
    }

    [Fact]
    public void Bool_is_not_a_snapshot_revision()
    {
        var reply = HostJson.Obj(
            ("protocol", HostJson.Str(HostV2.Protocol)),
            ("kind", HostJson.Str("reply")),
            ("request_id", HostJson.Str("r")),
            ("session_id", HostJson.Str("s")),
            ("server_nonce", HostJson.Str("MDEyMzQ1Njc4OWFiY2RlZg")),
            ("status", HostJson.Str("COMPLETED")),
            ("snapshot_revision", HostJson.True),
            ("result", HostJson.Obj()),
            ("error", HostJson.Null),
            ("mac", HostJson.Str(new string('0', 64))));
        Assert.Equal("INVALID_SNAPSHOT_REVISION", Assert.Throws<HostProtocolException>(() => HostV2.EncodeFrame(reply)).Code);
    }

    [Fact]
    public void Failure_without_error_is_refused()
    {
        var reply = HostJson.Obj(
            ("protocol", HostJson.Str(HostV2.Protocol)),
            ("kind", HostJson.Str("reply")),
            ("request_id", HostJson.Str("r")),
            ("session_id", HostJson.Str("s")),
            ("server_nonce", HostJson.Str("MDEyMzQ1Njc4OWFiY2RlZg")),
            ("status", HostJson.Str("REJECTED")),
            ("snapshot_revision", HostJson.IntValue(0)),
            ("result", HostJson.Null),
            ("error", HostJson.Null),
            ("mac", HostJson.Str(new string('0', 64))));
        Assert.Equal("FAILURE_WITHOUT_ERROR", Assert.Throws<HostProtocolException>(() => HostV2.EncodeFrame(reply)).Code);
    }

    [Fact]
    public void Excessive_nesting_and_size_refuse()
    {
        HostJson nested = HostJson.Null;
        for (var i = 0; i < 34; i++)
        {
            nested = HostJson.Arr([nested]);
        }

        Assert.Equal("MAXIMUM_DEPTH_EXCEEDED", Assert.Throws<HostProtocolException>(() => HostV2.CanonicalJson(nested)).Code);
        Assert.Equal("MESSAGE_TOO_LARGE", Assert.Throws<HostProtocolException>(() => HostV2.DecodeFrame(FrameTooLarge())).Code);
    }

    [Fact]
    public void V1_envelope_is_not_v2()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "contracts", "observed", "pause-command-v1.json");
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var bytes = Encoding.UTF8.GetBytes(node.ToJsonString());
        var ex = Assert.Throws<HostProtocolException>(() => HostV2.DecodeFrame(Frame(bytes)));
        Assert.Equal("UNSUPPORTED_PROTOCOL", ex.Code);
    }

    private static byte[] Frame(byte[] body)
    {
        var frame = new byte[4 + body.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }

    private static byte[] FrameTooLarge()
    {
        var frame = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)HostV2.MaxMessageBytes + 1);
        return frame;
    }
}
