using System.Text;
using System.Text.Json;

namespace Sovereign.Desktop.Core.Desk;

public sealed record MethodContract(string Direction, bool Mutates, bool RunBound, string? Capability);
public sealed record InitializeRequest(int ProtocolVersion, string AppVersion, Guid ProfileId, string Locale, string Timezone, string[] Capabilities, Guid SessionNonce);
public sealed record InitializeResult(int ProtocolVersion, string ComponentVersion, int StoreSchemaVersion, string RecoveryStatus, string[] Capabilities, Guid SessionNonce);
public sealed record DeskError(int Code, string Message, DeskErrorData Data);
public sealed record DeskErrorData(string Code, bool Retryable, Guid? OperationId, int? ProviderStatus, Guid DiagnosticId);

public static class DeskProtocol
{
    public const int Version = 1;
    public const int MaxFrameBytes = 1_048_576;
    public static readonly IReadOnlySet<string> ErrorCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "INVALID_INPUT", "NOT_CONFIGURED", "UNAUTHORIZED", "ENTITLEMENT_LIMITED", "RATE_LIMITED",
        "NETWORK_UNAVAILABLE", "PROVIDER_FAILED", "OUTPUT_INVALID", "BUDGET_LIMIT", "CANCELLED",
        "OUTCOME_UNKNOWN", "STORE_UNAVAILABLE", "SCHEMA_INCOMPATIBLE", "ARTIFACT_MISSING",
        "NOT_APPLICABLE", "PERMISSION_DENIED", "INTERNAL_ERROR"
    };
    public static readonly IReadOnlyDictionary<string, MethodContract> Methods = BuildMethods();

    private static Dictionary<string, MethodContract> BuildMethods()
    {
        var methods = new Dictionary<string, MethodContract>(StringComparer.Ordinal);
        methods["store.update_readiness"] = new("host_to_worker", false, false, null);
        foreach (var name in new[] { "initialize", "health", "case.list", "case.get", "run.get", "run.events", "followup.list_due", "store.check" })
            methods[name] = new("host_to_worker", false, name.StartsWith("run.", StringComparison.Ordinal), null);
        foreach (var name in new[] { "shutdown", "case.create", "case.revise", "case.link", "case.export", "artifact.import", "run.plan", "run.start", "run.cancel", "run.resume", "decision.commit", "followup.create", "followup.check", "store.backup", "store.prepare_update" })
            methods[name] = new("host_to_worker", true, name is "run.start" or "run.cancel" or "run.resume", null);
        foreach (var name in new[] { "broker.model_generate", "broker.evidence_search", "broker.source_fetch", "broker.market_read", "approval.request" })
            methods[name] = new("worker_to_host", true, true, name);
        methods["run.event"] = new("notification", false, true, null);
        methods["worker.health_changed"] = new("notification", false, false, null);
        methods["operation.mark_dispatched"] = new("host_to_worker", true, true, null);
        methods["case.revision_records"] = new("host_to_worker", false, false, null);
        methods["case.artifact_read"] = new("host_to_worker", false, false, null);
        methods["evidence.commit_selected"] = new("host_to_worker", true, false, null);
        methods["case.mark_review_ready"] = new("host_to_worker", true, false, null);
        methods["claim.record_reviewed"] = new("host_to_worker", true, false, null);
        methods["run.start_reviewed"] = new("host_to_worker", true, true, null);
        methods["valuation.record_review"] = new("host_to_worker", true, false, null);
        methods["calculation.commit_approved"] = new("host_to_worker", true, true, null);
        methods["decision.record_reviewed"] = new("host_to_worker", true, false, null);
        methods["case.decision_records"] = new("host_to_worker", false, false, null);
        methods["decision.list_due"] = new("host_to_worker", false, false, null);
        methods["draft.plan"] = new("host_to_worker", true, false, null);
        methods["draft.generate"] = new("host_to_worker", true, true, null);
        methods["draft.commit_result"] = new("host_to_worker", true, true, null);
        methods["investigation.commit_stage"] = new("host_to_worker", true, true, null);
        methods["investigation.read"] = new("host_to_worker", false, false, null);
        methods["investigation.cancel"] = new("host_to_worker", true, true, null);
        methods["investigation.fail_unavailable"] = new("host_to_worker", true, true, null);
        methods["investigation.mark_outcome_unknown"] = new("host_to_worker", true, true, null);
        methods["investigation.record_followup_rule"] = new("host_to_worker", true, false, null);
        methods["investigation.authorize_followup_check"] = new("host_to_worker", true, false, null);
        methods["followup.local_due"] = new("host_to_worker", false, false, null);
        methods["evidence.capture_search"] = new("host_to_worker", true, true, null);
        methods["evidence.read_captured"] = new("host_to_worker", false, true, null);
        methods["evidence.commit_search"] = new("host_to_worker", true, true, null);
        methods["investigation.settle_followup_check"] = new("host_to_worker", true, false, null);
        methods["investigation.cancel_followup"] = new("host_to_worker", true, false, null);
        methods["evidence.capture_fetch"] = new("host_to_worker", true, true, null);
        methods["evidence.commit_fetch"] = new("host_to_worker", true, true, null);
        methods["evidence.read_captured_fetch"] = new("host_to_worker", false, true, null);
        methods["investigation.acquire_source"] = new("host_to_worker", true, true, null);
        methods["followup.check_source"] = new("host_to_worker", true, false, null);
        methods["followup.source_status"] = new("host_to_worker", false, false, null);
        methods["investigation.generate_stage"] = new("host_to_worker", true, true, null);
        methods["investigation.plan_sources"] = new("host_to_worker", true, false, null);
        methods["investigation.collect_sources"] = new("host_to_worker", true, true, null);
        methods["investigation.read_model_operation"] = new("host_to_worker", false, true, null);
        methods["investigation.commit_model_result"] = new("host_to_worker", true, true, null);
        methods["investigation.continuation"] = new("host_to_worker", false, true, null);
        methods["investigation.resume_reviewed"] = new("host_to_worker", true, true, null);
        return methods;
    }

    // Framing excludes the terminating LF. The transport must bound its read buffer before calling this.
    public static JsonDocument ParseFrame(ReadOnlyMemory<byte> frame, string direction, string? expectedResponseId = null,
        Guid? expectedRunId = null, Guid? expectedOperationId = null, Guid? sessionNonce = null)
    {
        if (frame.Length is 0 or > MaxFrameBytes || frame.Span.Contains((byte)'\n') || frame.Span.Contains((byte)'\r'))
            throw Invalid("Invalid frame size or line boundary.");
        JsonDocument document;
        try { document = JsonDocument.Parse(frame, new JsonDocumentOptions { MaxDepth = 64 }); }
        catch (JsonException) { throw Invalid("Malformed JSON frame."); }
        try
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Invalid("Expected a JSON-RPC object.");
            CheckDuplicates(root);
            if (Text(root, "jsonrpc") != "2.0") throw Invalid("Unsupported JSON-RPC version.");
            if (!root.TryGetProperty("method", out var methodElement))
            {
                RequireKeys(root, ["jsonrpc", "id", "result", "error"]);
                if (expectedResponseId is null || Text(root, "id") != expectedResponseId) throw Invalid("Response ID mismatch.");
                if (root.TryGetProperty("result", out _) == root.TryGetProperty("error", out var error)) throw Invalid("Response must contain exactly one result or error.");
                if (root.TryGetProperty("error", out error))
                {
                    var typed = error.Deserialize<DeskError>(DeskJson.Options) ?? throw Invalid("Missing error.");
                    if (!ErrorCodes.Contains(typed.Data.Code) || typed.Data.DiagnosticId == Guid.Empty || typed.Code != -32000)
                        throw Invalid("Unknown error contract.");
                    if (expectedOperationId is not null && typed.Data.OperationId != expectedOperationId) throw Invalid("Error operation ID mismatch.");
                }
                return document;
            }
            if (expectedResponseId is not null) throw Invalid("Expected a response.");
            var method = methodElement.GetString();
            if (method is null || !Methods.TryGetValue(method, out var contract)) throw Invalid("Unknown method.");
            RequireKeys(root, ["jsonrpc", "id", "method", "params"]);
            if (contract.Direction != direction) throw Invalid("Method direction mismatch.");
            if (direction == "notification")
            {
                if (root.TryGetProperty("id", out _)) throw Invalid("Notification cannot have an ID.");
            }
            else if (string.IsNullOrWhiteSpace(Text(root, "id"))) throw Invalid("Request ID required.");
            var parameters = root.GetProperty("params");
            if (parameters.ValueKind != JsonValueKind.Object) throw Invalid("Params must be an object.");
            if (contract.RunBound) RequireId(parameters, "run_id", expectedRunId);
            if (contract.Mutates) RequireId(parameters, "operation_id", expectedOperationId);
            if (method != "initialize") RequireId(parameters, "session_nonce", sessionNonce);
            return document;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { document.Dispose(); throw Invalid("Invalid frame fields."); }
        catch { document.Dispose(); throw; }
    }

    public static void ValidateHandshake(InitializeRequest request, InitializeResult result, int minStoreSchema, int maxStoreSchema)
    {
        if (request.ProtocolVersion != Version || result.ProtocolVersion != Version || request.ProfileId == Guid.Empty ||
            request.SessionNonce == Guid.Empty || result.SessionNonce != request.SessionNonce ||
            result.StoreSchemaVersion < minStoreSchema || result.StoreSchemaVersion > maxStoreSchema ||
            result.Capabilities.Except(request.Capabilities, StringComparer.Ordinal).Any() ||
            request.Capabilities.Any(c => !DeskAuthority.Capabilities.ContainsKey(c)) ||
            result.Capabilities.Distinct(StringComparer.Ordinal).Count() != result.Capabilities.Length ||
            result.RecoveryStatus is not ("clean" or "recovered" or "requires_review"))
            throw new DeskContractException("SCHEMA_INCOMPATIBLE", "Worker handshake is incompatible.");
    }

    private static void CheckDuplicates(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw Invalid("Duplicate JSON field.");
                CheckDuplicates(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray()) CheckDuplicates(item);
    }
    private static void RequireKeys(JsonElement value, string[] allowed)
    {
        if (value.EnumerateObject().Any(p => !allowed.Contains(p.Name, StringComparer.Ordinal))) throw Invalid("Unknown envelope field.");
    }
    private static string Text(JsonElement value, string key) => value.GetProperty(key).GetString() ?? throw Invalid("Missing text field.");
    private static void RequireId(JsonElement parameters, string name, Guid? expected)
    {
        var text = Text(parameters, name);
        if (!Guid.TryParseExact(text, "D", out var id) || text != id.ToString("D") || id == Guid.Empty || expected is not null && id != expected)
            throw Invalid($"Invalid {name}.");
    }
    private static DeskContractException Invalid(string message) => new("INVALID_INPUT", message);
}
