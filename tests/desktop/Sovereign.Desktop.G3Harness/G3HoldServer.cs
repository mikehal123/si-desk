using System.Buffers.Binary;
using System.IO.Pipes;
using Sovereign.Desktop.Client.Adapters;
using Sovereign.Desktop.Client.Host;

namespace Sovereign.Desktop.G3Harness;

/// <summary>
/// Test host that records <c>offline_demo.start</c> as executed, then withholds the reply
/// until the client wait is interrupted. That is a dispatched in-flight request, not a
/// never-issued operation lookup.
/// </summary>
public sealed class G3HoldServer : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly string _pipe;
    private readonly string _profile;
    private readonly string _instance;
    private readonly byte[] _secret;
    private readonly TaskCompletionSource _dispatched = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseReply = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _task;
    private string? _startedRun;

    public G3HoldServer(string pipeName, string profileId, string instanceId, byte[] secret)
    {
        _pipe = NamedPipeTransport.NormalizePipeName(pipeName);
        _profile = profileId;
        _instance = instanceId;
        _secret = secret;
    }

    public int StartCalls { get; private set; }
    public int OperationsGetCalls { get; private set; }
    public Task Dispatched => _dispatched.Task;
    public IReadOnlyList<string> Methods => _methods;
    private readonly List<string> _methods = [];

    public void Start() => _task = Task.Run(() => RunAsync(_cts.Token));

    public void ReleaseHeldReply() => _releaseReply.TrySetResult();

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(_pipe, PipeDirection.InOut, 8, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try
            {
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                await HandleAsync(server, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
            }
            finally
            {
                await server.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        var header = await ReadExact(server, 4, ct).ConfigureAwait(false);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(header);
        var body = await ReadExact(server, (int)size, ct).ConfigureAwait(false);
        var message = HostV2.ParseJsonObject(body);
        HostJson reply;
        string? method = null;
        if (message["kind"].AsString() == "session_open")
        {
            reply = SessionOpened(message);
        }
        else
        {
            HostV2.VerifyMac(message, _secret);
            method = message["method"].AsString();
            reply = HandleRequest(message);
        }

        if (method == "offline_demo.start")
        {
            _dispatched.TrySetResult();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await Task.WhenAny(_releaseReply.Task, Task.Delay(Timeout.Infinite, linked.Token)).ConfigureAwait(false);
        }

        var frame = HostV2.EncodeFrame(reply);
        await server.WriteAsync(frame, ct).ConfigureAwait(false);
        await server.FlushAsync(ct).ConfigureAwait(false);
    }

    private HostJson SessionOpened(HostJson message) => HostJson.Obj(
        ("protocol", HostJson.Str(HostV2.Protocol)),
        ("kind", HostJson.Str("session_opened")),
        ("request_id", message["request_id"]),
        ("status", HostJson.Str("COMPLETED")),
        ("endpoint", HostJson.Obj(
            ("pipe_name", HostJson.Str(@"\\.\pipe\" + _pipe)),
            ("instance_id", HostJson.Str(_instance)),
            ("profile_id", HostJson.Str(_profile)))),
        ("session", HostJson.Obj(
            ("session_id", HostJson.Str("session-g3")),
            ("operator_sid", HostJson.Str("S-1-test")),
            ("issued_at", HostJson.Str("2026-09-15T00:00:00Z")),
            ("expires_at", HostJson.Str("2026-09-15T00:30:00Z")),
            ("authenticator", HostJson.Obj(
                ("algorithm", HostJson.Str("HMAC-SHA-256")),
                ("key_b64", HostJson.Str(HostV2.SecretToB64(_secret))))))));

    private HostJson HandleRequest(HostJson request)
    {
        var method = request["method"].AsString();
        _methods.Add(method ?? "");
        HostJson result;
        var status = "COMPLETED";
        var error = HostJson.Null;
        switch (method)
        {
            case "runtime.get":
                result = HostJson.Obj(
                    ("type", HostJson.Str("runtime")),
                    ("protocol", HostJson.Str(HostV2.Protocol)),
                    ("bundle_version", HostJson.Str("2.0.2")),
                    ("engine_version", HostJson.Str("n2-offline-2.0.2")),
                    ("python_version", HostJson.Str("3.12.10")),
                    ("profile_id", HostJson.Str(_profile)),
                    ("mode", HostJson.Str("OFFLINE")),
                    ("data_class", HostJson.Str("SYNTHETIC")),
                    ("trading_authority", HostJson.Str("NONE")));
                break;
            case "health.get":
                result = HostJson.Obj(
                    ("type", HostJson.Str("health")),
                    ("status", HostJson.Str("READY")),
                    ("host_instance_id", HostJson.Str(_instance)),
                    ("profile_id", HostJson.Str(_profile)),
                    ("snapshot_revision", HostJson.IntValue(1)),
                    ("paper", HostJson.Str("UNAVAILABLE")),
                    ("providers", HostJson.Str("UNAVAILABLE")),
                    ("checked_at", HostJson.Str("2026-09-15T00:00:00Z")));
                break;
            case "capabilities.list":
                result = HostJson.Obj(
                    ("type", HostJson.Str("capability_list")),
                    ("items", HostJson.Arr([
                        HostJson.Obj(("id", HostJson.Str("offline_demo.start")), ("status", HostJson.Str("AVAILABLE")), ("reason", HostJson.Null))
                    ])));
                break;
            case "runs.list":
                result = HostJson.Obj(
                    ("type", HostJson.Str("run_list")),
                    ("items", _startedRun is null
                        ? HostJson.Arr([])
                        : HostJson.Arr([
                            HostJson.Obj(
                                ("run_id", HostJson.Str(_startedRun)),
                                ("operation_id", HostJson.Str("op-g3-1")),
                                ("state", HostJson.Str("COMPLETED")),
                                ("mode", HostJson.Str("OFFLINE")),
                                ("data_class", HostJson.Str("SYNTHETIC")),
                                ("trading_authority", HostJson.Str("NONE")))
                        ])));
                break;
            case "offline_demo.start":
                StartCalls++;
                _startedRun ??= "run-g3-1";
                result = OperationBody();
                break;
            case "operations.get":
                OperationsGetCalls++;
                var requested = request["params"]["operation_id"].AsString();
                if (_startedRun is null || requested != "op-g3-1")
                {
                    status = "UNKNOWN";
                    result = HostJson.Obj(
                        ("type", HostJson.Str("operation")),
                        ("operation_id", HostJson.Str(requested ?? "")),
                        ("state", HostJson.Str("UNKNOWN")));
                    break;
                }

                result = OperationBody();
                break;
            case "account.get":
                result = HostJson.Obj(
                    ("type", HostJson.Str("account")),
                    ("run_id", HostJson.Str(_startedRun ?? "run-g3-1")),
                    ("cash_cents", HostJson.IntValue(91100)),
                    ("positions", HostJson.Obj(("AAA", HostJson.IntValue(11)))),
                    ("reserved_cash_cents", HostJson.IntValue(0)),
                    ("ending_equity_cents", HostJson.IntValue(101000)),
                    ("trading_authority", HostJson.Str("NONE")));
                break;
            case "orders.list":
                result = HostJson.Obj(
                    ("type", HostJson.Str("order_list")),
                    ("run_id", HostJson.Str(_startedRun ?? "run-g3-1")),
                    ("items", HostJson.Arr([
                        Order("cid-1"), Order("cid-2"), Order("cid-3")
                    ])));
                break;
            default:
                result = HostJson.Obj(("type", HostJson.Str("availability")), ("status", HostJson.Str("UNAVAILABLE")), ("reason", HostJson.Str("UNKNOWN_METHOD")));
                status = "REJECTED";
                error = HostJson.Obj(("code", HostJson.Str("UNKNOWN_METHOD")), ("message", HostJson.Str("UNKNOWN_METHOD")), ("retryable", HostJson.False));
                break;
        }

        var unsigned = HostJson.Obj(
            ("protocol", HostJson.Str(HostV2.Protocol)),
            ("kind", HostJson.Str("reply")),
            ("request_id", request["request_id"]),
            ("session_id", request["session_id"]),
            ("server_nonce", HostJson.Str(HostV2.NewNonce())),
            ("status", HostJson.Str(status)),
            ("snapshot_revision", HostJson.IntValue(1)),
            ("result", result),
            ("error", error),
            ("mac", HostJson.Str(new string('0', 64))));
        return HostV2.Sign(unsigned, _secret);
    }

    private HostJson OperationBody() => HostJson.Obj(
        ("type", HostJson.Str("operation")),
        ("operation_id", HostJson.Str("op-g3-1")),
        ("idempotency_key", HostJson.Str(NativeOperatorClient.OfflineDemoIdempotencyKey)),
        ("run_id", HostJson.Str(_startedRun ?? "run-g3-1")),
        ("state", HostJson.Str("COMPLETED")),
        ("created_at", HostJson.Str("2026-09-15T00:00:00Z")),
        ("updated_at", HostJson.Str("2026-09-15T00:00:01Z")),
        ("error", HostJson.Null));

    private static HostJson Order(string cid) => HostJson.Obj(
        ("client_order_id", HostJson.Str(cid)),
        ("id", HostJson.Str("broker-" + cid)),
        ("symbol", HostJson.Str("AAA")),
        ("qty", HostJson.Str("1")),
        ("side", HostJson.Str("buy")),
        ("status", HostJson.Str("filled")));

    private static async Task<byte[]> ReadExact(Stream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct).ConfigureAwait(false);
            if (n == 0)
            {
                throw new EndOfStreamException();
            }

            offset += n;
        }

        return buffer;
    }

    public async ValueTask DisposeAsync()
    {
        ReleaseHeldReply();
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_task is not null)
        {
            try
            {
                await _task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts.Dispose();
    }
}
