using System.Text;
using System.Text.Json;

namespace Sovereign.Desktop.Connector;

/// <summary>MCP dispatch for the in-app swarm manager. Stdio is one JSON-RPC message per line.</summary>
public static class SwarmManagerMcp
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static int RunStdio()
    {
        var input = Console.OpenStandardInput();
        var output = Console.OpenStandardOutput();
        while (ReadMessage(input) is { } body)
        {
            using var document = JsonDocument.Parse(body);
            var message = document.RootElement;
            if (!message.TryGetProperty("method", out var methodElement)) continue;
            var hasId = message.TryGetProperty("id", out var id);
            var result = Handle(methodElement.GetString() ?? "", message, null);
            if (!hasId || result is null) continue;
            WriteMessage(output, JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }, Json));
        }
        return 0;
    }

    public static object? Handle(string method, JsonElement message, string? root)
    {
        if (method == "notifications/initialized") return null;
        if (method == "initialize")
            return new
            {
                protocolVersion = "2025-03-26",
                capabilities = new { tools = new { } },
                serverInfo = new { name = "si-desk-swarm-manager", version = "swarm-manager" }
            };
        if (method == "tools/list") return new { tools = SwarmManagerTools.Definitions() };
        if (method != "tools/call") return Error("Unknown method.");
        if (!message.TryGetProperty("params", out var parameters) || !parameters.TryGetProperty("name", out var nameElement))
            return Error("Missing tool name.");
        var name = nameElement.GetString() ?? "";
        var arguments = parameters.TryGetProperty("arguments", out var args) ? args : default;
        try
        {
            var text = SwarmManagerTools.Call(name, arguments, root);
            return new { content = new[] { new { type = "text", text } }, isError = false };
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or KeyNotFoundException or JsonException)
        {
            return Error(ex.Message);
        }
    }

    public static byte[]? ReadMessage(Stream input)
    {
        using var buffer = new MemoryStream();
        while (true)
        {
            var next = input.ReadByte();
            if (next < 0)
            {
                if (buffer.Length == 0) return null;
                throw new IOException("MCP stdio message ended without a newline.");
            }
            if (next == '\n')
            {
                var bytes = buffer.ToArray();
                buffer.SetLength(0);
                if (bytes.Length > 0 && bytes[^1] == '\r')
                    bytes = bytes[..^1];
                if (bytes.Length == 0) continue;
                return bytes;
            }
            buffer.WriteByte((byte)next);
            if (buffer.Length > 8_000_000) throw new IOException("MCP stdio message is too large.");
        }
    }

    public static void WriteMessage(Stream output, string json)
    {
        if (json.Contains('\n') || json.Contains('\r'))
            throw new IOException("MCP stdio message must be one line.");
        var body = Encoding.UTF8.GetBytes(json);
        output.Write(body);
        output.WriteByte((byte)'\n');
        output.Flush();
    }

    static object Error(string message) => new { content = new[] { new { type = "text", text = message } }, isError = true };
}
