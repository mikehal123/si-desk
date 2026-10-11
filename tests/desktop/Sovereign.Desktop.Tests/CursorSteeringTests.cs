using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Sovereign.Desktop.Client;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class CursorSteeringTests
{
    private const string Model = "grok-4.7[context=256k,reasoning_effort=high,fast=true]";
    private static readonly TimeSpan TestWait = TimeSpan.FromSeconds(3);
    private static string Reply(int id, object result) => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result });
    private static string SessionReply => Reply(2, new { sessionId = "s1", models = new { availableModels = new[] { new { modelId = Model, name = "grok-4.7" } } } });
    private static string Chunk(string text) => JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "session/update", @params = new {
        sessionId = "s1", update = new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text } } } });
    private static string Permission(string id) => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method = "session/request_permission", @params = new {
        sessionId = "s1", toolCall = new { toolCallId = id, title = "Read fixture", kind = "read", status = "pending" },
        options = new[] { new { optionId = "once", name = "Allow once", kind = "allow_once" } } } });
    private static string Method(JsonElement frame) => frame.TryGetProperty("method", out var method) ? method.GetString()! : "";

    private sealed class FrameReader : TextReader
    {
        private readonly Channel<string> _input = Channel.CreateUnbounded<string>();
        private readonly Channel<int> _reads = Channel.CreateUnbounded<int>();
        private int _readCount;
        private int _active;
        public int MaxConcurrentReads { get; private set; }
        public void Feed(params string[] frames) { foreach (var frame in frames) _input.Writer.TryWrite(frame); }
        public void Complete() => _input.Writer.TryComplete();
        public async Task WaitForReadAsync(int count)
        {
            while (Volatile.Read(ref _readCount) < count)
                await _reads.Reader.ReadAsync().AsTask().WaitAsync(TestWait);
        }
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            var concurrent = Interlocked.Increment(ref _active);
            MaxConcurrentReads = Math.Max(MaxConcurrentReads, concurrent);
            _reads.Writer.TryWrite(Interlocked.Increment(ref _readCount));
            try { return await _input.Reader.ReadAsync(cancellationToken); }
            catch (ChannelClosedException) { return null; }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private sealed class FrameWriter : TextWriter
    {
        private readonly ConcurrentQueue<JsonElement> _frames = new();
        private readonly Channel<JsonElement> _written = Channel.CreateUnbounded<JsonElement>();
        private int _active;
        public override Encoding Encoding => Encoding.UTF8;
        public JsonElement[] Frames => _frames.ToArray();
        public int MaxConcurrentWrites { get; private set; }
        public Func<JsonElement, CancellationToken, Task>? BeforeWrite { get; set; }
        public override async Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            var concurrent = Interlocked.Increment(ref _active);
            MaxConcurrentWrites = Math.Max(MaxConcurrentWrites, concurrent);
            try
            {
                using var json = JsonDocument.Parse(buffer.ToString());
                var frame = json.RootElement.Clone();
                if (BeforeWrite is not null) await BeforeWrite(frame, cancellationToken);
                await Task.Yield(); // Let another attempted write expose missing serialization.
                cancellationToken.ThrowIfCancellationRequested();
                _frames.Enqueue(frame);
                _written.Writer.TryWrite(frame);
            }
            finally { Interlocked.Decrement(ref _active); }
        }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public async Task<JsonElement> NextAsync(Func<JsonElement, bool> matches)
        {
            using var timeout = new CancellationTokenSource(TestWait);
            while (true)
            {
                var frame = await _written.Reader.ReadAsync(timeout.Token);
                if (matches(frame)) return frame;
            }
        }
        public Task<JsonElement> NextMethodAsync(string method) => NextAsync(frame => Method(frame) == method);
        public Task<JsonElement> NextResponseAsync(string id) => NextAsync(frame => frame.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.String && value.GetString() == id);
    }

    private sealed class Protocol(CursorProjectOptions? project = null, TimeSpan? timeout = null)
    {
        public readonly FrameReader Reader = new();
        public readonly FrameWriter Writer = new();
        public CursorAcpSession Session { get; private set; } = null!;
        public async Task ConnectAsync()
        {
            Session = new CursorAcpSession(Reader, Writer, project, steeringDrainTimeout: timeout);
            Reader.Feed(Reply(1, new { protocolVersion = 1 }), SessionReply, Reply(3, new { }));
            await Session.ConnectAsync("fixture-workspace", default).WaitAsync(TestWait);
        }
        public async Task<Task<CursorTurnOutcome>> BeginAsync(Action<string>? delta = null, CancellationToken ct = default)
        {
            Reader.Feed(Reply(4, new { }));
            var send = Session.SendAsync(Model, "Original task", delta ?? (_ => { }), ct);
            await Writer.NextMethodAsync("session/prompt");
            await Reader.WaitForReadAsync(5);
            return send;
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("partial")]
    public async Task Steering_drains_late_frames_and_continues_the_same_session(string partial)
    {
        var protocol = new Protocol();
        await protocol.ConnectAsync();
        var text = "";
        var first = await protocol.BeginAsync(piece => text += piece);
        if (partial.Length > 0) protocol.Reader.Feed(Chunk(partial));
        Assert.True(await protocol.Session.RequestSteeringAsync(default));
        Assert.True(await protocol.Session.RequestSteeringAsync(default));
        var cancel = await protocol.Writer.NextMethodAsync("session/cancel");
        Assert.False(cancel.TryGetProperty("id", out _));
        Assert.Equal("s1", cancel.GetProperty("params").GetProperty("sessionId").GetString());
        Assert.False(first.IsCompleted);
        protocol.Reader.Feed(Chunk("late output"), Reply(5, new { stopReason = "cancelled" }));
        Assert.Equal(CursorTurnOutcome.Redirected, await first.WaitAsync(TestWait));
        Assert.Equal(partial + "late output", text);
        Assert.Equal("cancelled", protocol.Session.LastStopReason);
        Assert.False(await protocol.Session.RequestSteeringAsync(default));

        protocol.Reader.Feed(Reply(6, new { }));
        var second = protocol.Session.SendAsync(Model, "Correction", _ => { }, default);
        await protocol.Writer.NextMethodAsync("session/prompt");
        protocol.Reader.Feed(Chunk("Corrected answer"), Reply(7, new { stopReason = "end_turn" }));
        Assert.Equal(CursorTurnOutcome.Completed, await second.WaitAsync(TestWait));
        var frames = protocol.Writer.Frames;
        Assert.Single(frames, frame => Method(frame) == "initialize");
        Assert.Single(frames, frame => Method(frame) == "session/new");
        Assert.Single(frames, frame => Method(frame) == "session/cancel");
        Assert.All(frames.Where(frame => Method(frame) == "session/prompt"), frame => Assert.Equal("s1", frame.GetProperty("params").GetProperty("sessionId").GetString()));
        Assert.Equal(1, protocol.Reader.MaxConcurrentReads);
        Assert.Equal(1, protocol.Writer.MaxConcurrentWrites);
    }

    [Fact]
    public async Task Intentional_cancelled_acknowledgement_can_have_no_output()
    {
        var protocol = new Protocol();
        await protocol.ConnectAsync();
        var send = await protocol.BeginAsync();
        Assert.True(await protocol.Session.RequestSteeringAsync(default));
        protocol.Reader.Feed(Reply(5, new { stopReason = "cancelled" }));
        Assert.Equal(CursorTurnOutcome.Redirected, await send.WaitAsync(TestWait));
    }

    [Fact]
    public async Task Completion_winning_the_cancel_race_remains_completed_and_cannot_cancel_a_followup()
    {
        var protocol = new Protocol();
        await protocol.ConnectAsync();
        var send = await protocol.BeginAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        protocol.Writer.BeforeWrite = async (frame, ct) => { if (Method(frame) == "session/cancel") { entered.TrySetResult(); await release.Task.WaitAsync(ct); } };
        var steering = protocol.Session.RequestSteeringAsync(default);
        await entered.Task.WaitAsync(TestWait);
        var repeated = protocol.Session.RequestSteeringAsync(default);
        protocol.Reader.Feed(Chunk("Finished just in time"), Reply(5, new { stopReason = "end_turn" }));
        Assert.False(send.IsCompleted);
        release.TrySetResult();
        Assert.True(await steering.WaitAsync(TestWait));
        await repeated.WaitAsync(TestWait);
        Assert.Equal(CursorTurnOutcome.Completed, await send.WaitAsync(TestWait));

        protocol.Reader.Feed(Reply(6, new { }));
        var followup = protocol.Session.SendAsync(Model, "Correction", _ => { }, default);
        await protocol.Writer.NextMethodAsync("session/prompt");
        protocol.Reader.Feed(Chunk("Followup"), Reply(7, new { stopReason = "end_turn" }));
        Assert.Equal(CursorTurnOutcome.Completed, await followup.WaitAsync(TestWait));
        Assert.Single(protocol.Writer.Frames, frame => Method(frame) == "session/cancel");
    }

    [Fact]
    public async Task Steering_cancels_a_permission_callback_even_when_it_ignores_the_token()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callback = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var protocol = new Protocol(new() { RequestPermission = (_, ct) => {
            Interlocked.Increment(ref calls); ct.Register(() => callbackCancelled.TrySetResult()); entered.TrySetResult(); return callback.Task;
        } });
        await protocol.ConnectAsync();
        var send = await protocol.BeginAsync();
        protocol.Reader.Feed(Permission("permission-1"));
        await entered.Task.WaitAsync(TestWait);
        Assert.True(await protocol.Session.RequestSteeringAsync(default));
        await callbackCancelled.Task.WaitAsync(TestWait);
        var reply = await protocol.Writer.NextResponseAsync("permission-1");
        Assert.Equal("cancelled", reply.GetProperty("result").GetProperty("outcome").GetProperty("outcome").GetString());
        protocol.Reader.Feed(Permission("permission-2"));
        reply = await protocol.Writer.NextResponseAsync("permission-2");
        Assert.Equal("cancelled", reply.GetProperty("result").GetProperty("outcome").GetProperty("outcome").GetString());
        callback.TrySetResult("once");
        protocol.Reader.Feed(Reply(5, new { stopReason = "cancelled" }));
        Assert.Equal(CursorTurnOutcome.Redirected, await send.WaitAsync(TestWait));
        Assert.Equal(1, calls);
        Assert.DoesNotContain(protocol.Writer.Frames, frame => frame.TryGetProperty("result", out var result) && result.TryGetProperty("outcome", out var outcome) && outcome.GetProperty("outcome").GetString() == "selected");
    }

    [Fact]
    public async Task Permission_responses_and_steering_use_one_serial_writer()
    {
        var protocol = new Protocol(new() { Permissions = CursorPermissionPolicy.AlwaysAllow });
        await protocol.ConnectAsync();
        var send = await protocol.BeginAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        protocol.Writer.BeforeWrite = async (frame, ct) => {
            if (frame.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == "permission-1") { entered.TrySetResult(); await release.Task.WaitAsync(ct); }
        };
        protocol.Reader.Feed(Permission("permission-1"));
        await entered.Task.WaitAsync(TestWait);
        var steering = protocol.Session.RequestSteeringAsync(default);
        Assert.False(steering.IsCompleted);
        release.TrySetResult();
        Assert.True(await steering.WaitAsync(TestWait));
        protocol.Reader.Feed(Permission("permission-2"));
        var permission = await protocol.Writer.NextResponseAsync("permission-2");
        Assert.Equal("cancelled", permission.GetProperty("result").GetProperty("outcome").GetProperty("outcome").GetString());
        protocol.Reader.Feed(Reply(5, new { stopReason = "cancelled" }));
        Assert.Equal(CursorTurnOutcome.Redirected, await send.WaitAsync(TestWait));
        Assert.Equal(1, protocol.Writer.MaxConcurrentWrites);
        var frames = protocol.Writer.Frames;
        Assert.True(Array.FindIndex(frames, frame => frame.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == "permission-1") < Array.FindIndex(frames, frame => Method(frame) == "session/cancel"));
    }

    [Fact]
    public async Task A_missing_cancel_acknowledgement_fails_the_transport_without_resubmitting()
    {
        var protocol = new Protocol(timeout: TimeSpan.FromMilliseconds(100));
        await protocol.ConnectAsync();
        var send = await protocol.BeginAsync();
        Assert.True(await protocol.Session.RequestSteeringAsync(default));
        var failure = await Assert.ThrowsAsync<IOException>(() => send.WaitAsync(TestWait));
        Assert.Contains("acknowledge", failure.Message);
        var writes = protocol.Writer.Frames.Length;
        await Assert.ThrowsAsync<IOException>(() => protocol.Session.SendAsync(Model, "Correction", _ => { }, default));
        Assert.Equal(writes, protocol.Writer.Frames.Length);
        Assert.False(await protocol.Session.RequestSteeringAsync(default));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Eof_or_wrong_response_identity_after_cancel_cannot_resubmit(bool eof)
    {
        var protocol = new Protocol();
        await protocol.ConnectAsync();
        var send = await protocol.BeginAsync();
        Assert.True(await protocol.Session.RequestSteeringAsync(default));
        if (eof) protocol.Reader.Complete(); else protocol.Reader.Feed(Reply(99, new { stopReason = "cancelled" }));
        await Assert.ThrowsAsync<IOException>(() => send.WaitAsync(TestWait));
        var writes = protocol.Writer.Frames.Length;
        await Assert.ThrowsAsync<IOException>(() => protocol.Session.SendAsync(Model, "Correction", _ => { }, default));
        Assert.Equal(writes, protocol.Writer.Frames.Length);
    }

    [Fact]
    public async Task Steering_does_not_cancel_initialize_mode_or_model_control_requests()
    {
        var reader = new FrameReader(); var writer = new FrameWriter();
        var session = new CursorAcpSession(reader, writer);
        var connect = session.ConnectAsync("fixture-workspace", default);
        await writer.NextMethodAsync("initialize"); Assert.False(await session.RequestSteeringAsync(default));
        reader.Feed(Reply(1, new { protocolVersion = 1 }));
        await writer.NextMethodAsync("session/new"); Assert.False(await session.RequestSteeringAsync(default));
        reader.Feed(SessionReply);
        await writer.NextMethodAsync("session/set_mode"); Assert.False(await session.RequestSteeringAsync(default));
        reader.Feed(Reply(3, new { })); await connect.WaitAsync(TestWait);
        var send = session.SendAsync(Model, "Original", _ => { }, default);
        await writer.NextMethodAsync("session/set_model"); Assert.False(await session.RequestSteeringAsync(default));
        reader.Feed(Reply(4, new { }));
        await writer.NextMethodAsync("session/prompt"); await reader.WaitForReadAsync(5);
        Assert.True(await session.RequestSteeringAsync(default));
        reader.Feed(Reply(5, new { stopReason = "cancelled" }));
        Assert.Equal(CursorTurnOutcome.Redirected, await send.WaitAsync(TestWait));
        Assert.Single(writer.Frames, frame => Method(frame) == "session/cancel");
    }

    [Fact]
    public async Task Hard_stop_cancellation_remains_a_failed_turn_after_steering()
    {
        var protocol = new Protocol();
        await protocol.ConnectAsync();
        using var stop = new CancellationTokenSource();
        var send = await protocol.BeginAsync(ct: stop.Token);
        Assert.True(await protocol.Session.RequestSteeringAsync(default));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send.WaitAsync(TestWait));
        Assert.Null(protocol.Session.LastStopReason);
        Assert.False(await protocol.Session.RequestSteeringAsync(default));
    }

    [Fact]
    public async Task Hard_stop_during_the_cancel_write_preserves_cancellation_semantics()
    {
        var protocol = new Protocol();
        await protocol.ConnectAsync();
        using var stop = new CancellationTokenSource();
        var send = await protocol.BeginAsync(ct: stop.Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        protocol.Writer.BeforeWrite = async (frame, ct) => {
            if (Method(frame) == "session/cancel") { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
        };
        var steering = protocol.Session.RequestSteeringAsync(stop.Token);
        await entered.Task.WaitAsync(TestWait);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => steering.WaitAsync(TestWait));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send.WaitAsync(TestWait));
        Assert.Null(protocol.Session.LastStopReason);
    }

    [Fact]
    public async Task Public_client_steering_bypasses_the_long_running_send_gate()
    {
        var reader = new FrameReader(); var writer = new FrameWriter();
        using var client = new CursorChatClient("fixture-workspace", reader, writer);
        reader.Feed(Reply(1, new { protocolVersion = 1 }), SessionReply, Reply(3, new { }));
        await client.ModelsAsync(default).WaitAsync(TestWait);
        Assert.Null(client.InternalProcessId);
        Assert.Equal("s1", client.InternalSessionId);
        reader.Feed(Reply(4, new { }));
        var first = client.SendAsync(Model, "Original", _ => { }, default);
        await writer.NextMethodAsync("session/prompt"); await reader.WaitForReadAsync(5);
        Assert.True(await client.RequestSteeringAsync(default).WaitAsync(TestWait));
        reader.Feed(Reply(5, new { stopReason = "cancelled" }));
        Assert.Equal(CursorTurnOutcome.Redirected, await first.WaitAsync(TestWait));
        reader.Feed(Reply(6, new { }));
        var second = client.SendAsync(Model, "Correction", _ => { }, default);
        await writer.NextMethodAsync("session/prompt");
        reader.Feed(Chunk("Done"), Reply(7, new { stopReason = "end_turn" }));
        Assert.Equal(CursorTurnOutcome.Completed, await second.WaitAsync(TestWait));
        Assert.Equal("s1", client.InternalSessionId);
        Assert.Single(writer.Frames, frame => Method(frame) == "initialize");
    }
}
