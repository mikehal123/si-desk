using System.Text.Json;

namespace Sovereign.Desktop.Core.Desk;

public sealed record PendingDeskRequest(string RequestId, string Method, JsonElement Parameters);
public static class DeskMethods
{
    private static readonly JsonDocument Parameters = Load("Desk.MethodParameters");
    private static readonly JsonDocument Results = Load("Desk.MethodResults");
    private static JsonDocument Load(string name) => JsonDocument.Parse(typeof(DeskMethods).Assembly.GetManifestResourceStream(name)!);
    public static PendingDeskRequest ValidateRequest(ReadOnlyMemory<byte> frame, string direction, Guid nonce)
    {
        using var doc = DeskProtocol.ParseFrame(frame, direction, sessionNonce: nonce);
        var root = doc.RootElement; var method = root.GetProperty("method").GetString()!;
        var parameters = root.GetProperty("params");
        ContractShape.Require(parameters, Parameters.RootElement.GetProperty("methods").GetProperty(method).GetProperty("schema"), Parameters.RootElement.GetProperty("$defs"));
        if (nonce == Guid.Empty || parameters.GetProperty("session_nonce").GetString() != nonce.ToString("D")) throw Invalid();
        Correlate(parameters, parameters);
        if (method == "decision.commit" && parameters.GetProperty("decision").GetProperty("approval_id").GetString() != parameters.GetProperty("approval_id").GetString()) throw Invalid();
        if (method.StartsWith("draft.", StringComparison.Ordinal)) DraftContract.ValidateRequest(method, parameters);
        return new(direction == "notification" ? "" : root.GetProperty("id").GetString()!, method, parameters.Clone());
    }
    public static JsonElement ValidateResponse(ReadOnlyMemory<byte> frame, PendingDeskRequest pending)
    {
        if (string.IsNullOrWhiteSpace(pending.RequestId) || !DeskProtocol.Methods.TryGetValue(pending.Method, out var method) || method.Direction == "notification") throw Invalid();
        Guid? Id(string key) => pending.Parameters.TryGetProperty(key, out var value) ? Guid.Parse(value.GetString()!) : null;
        using var doc = DeskProtocol.ParseFrame(frame, "response", pending.RequestId, Id("run_id"), Id("operation_id"), Id("session_nonce"));
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var error)) return error.Clone();
        var result = root.GetProperty("result");
        ContractShape.Require(result, Results.RootElement.GetProperty("methods").GetProperty(pending.Method), Results.RootElement.GetProperty("$defs"));
        Correlate(result, pending.Parameters);
        if (result.GetProperty("method").GetString() != pending.Method) throw Invalid();
        if (pending.Method == "approval.request")
        {
            var value = result.GetProperty("value");
            if (value.GetProperty("approved").GetBoolean() != (value.GetProperty("approval_id").ValueKind == JsonValueKind.String)) throw Invalid();
        }
        if (pending.Method.StartsWith("draft.", StringComparison.Ordinal)) DraftContract.ValidateResult(pending.Method, result.GetProperty("value"), pending.Parameters);
        return result.Clone();
    }
    private static void Correlate(JsonElement value, JsonElement request)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in value.EnumerateObject())
            {
                if (p.Name is "run_id" or "operation_id" or "session_nonce" or "case_id" or "reservation_id" or "scope_sha256")
                    if (request.TryGetProperty(p.Name, out var expected) && !JsonElement.DeepEquals(p.Value, expected)) throw Invalid();
                Correlate(p.Value, request);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Correlate(item, request);
    }
    private static DeskContractException Invalid() => new("INVALID_INPUT", "Method-specific correlation failed.");
}
