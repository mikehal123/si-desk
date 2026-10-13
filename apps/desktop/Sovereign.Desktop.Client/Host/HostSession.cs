using System.Security.Cryptography;

namespace Sovereign.Desktop.Client.Host;

public sealed class HostSession : IAsyncDisposable
{
    private readonly byte[] _secret;
    private readonly HashSet<string> _usedNonces = new(StringComparer.Ordinal);
    private readonly TimeSpan _timeout;
    private readonly HostTransportSeam? _seam;

    private HostSession(byte[] secret, string sessionId, HostEndpoint endpoint, TimeSpan timeout, HostTransportSeam? seam)
    {
        _secret = secret;
        SessionId = sessionId;
        Endpoint = endpoint;
        _timeout = timeout;
        _seam = seam;
    }

    public string SessionId { get; }
    public HostEndpoint Endpoint { get; }

    public static async Task<HostSession> OpenAsync(
        HostEndpoint endpoint,
        string clientName,
        string clientVersion,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        HostTransportSeam? seam = null)
    {
        await using var transport = await ConnectAsync(endpoint.PipeName, timeout, cancellationToken).ConfigureAwait(false);
        var requestId = HostV2.NewIdentity("open");
        var open = HostV2.SessionOpen(requestId, endpoint.ProfileId, clientName, clientVersion);
        await transport.WriteMessageAsync(open, timeout, cancellationToken).ConfigureAwait(false);
        var opened = await transport.ReadMessageAsync(timeout, cancellationToken).ConfigureAwait(false);
        if (opened["kind"].AsString() != "session_opened")
        {
            throw new HostProtocolException("SESSION_REJECTED");
        }

        HostV2.ValidateMessage(opened);
        var session = opened["session"];
        var secret = HostV2.SecretFromB64(session["authenticator"]["key_b64"].AsString() ?? "");
        var sessionId = session["session_id"].AsString() ?? throw new HostProtocolException("INVALID_SESSION_ID");
        var openedPipe = opened["endpoint"]["pipe_name"].AsString();
        var openedProfile = opened["endpoint"]["profile_id"].AsString();
        var openedInstance = opened["endpoint"]["instance_id"].AsString();
        if (!string.Equals(openedPipe, endpoint.PipeName, StringComparison.Ordinal)
            || !string.Equals(openedProfile, endpoint.ProfileId, StringComparison.Ordinal)
            || !string.Equals(openedInstance, endpoint.InstanceId, StringComparison.Ordinal))
        {
            CryptographicClear(secret);
            throw new HostProtocolException("ENDPOINT_MISMATCH");
        }

        return new HostSession(secret, sessionId, endpoint, timeout, seam);
    }

    public async Task<HostJson> CallAsync(
        string method,
        HostJson parameters,
        string? idempotencyKey,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        var wait = timeout ?? _timeout;
        var requestId = HostV2.NewIdentity("req");
        string nonce;
        lock (_usedNonces)
        {
            do
            {
                nonce = HostV2.NewNonce();
            } while (!_usedNonces.Add(nonce));
        }

        var unsigned = HostV2.Request(requestId, SessionId, nonce, method, idempotencyKey, parameters);
        var signed = HostV2.Sign(unsigned, _secret);
        var sent = false;
        try
        {
            await using var transport = await ConnectAsync(Endpoint.PipeName, wait, cancellationToken).ConfigureAwait(false);
            await transport.WriteMessageAsync(signed, wait, cancellationToken).ConfigureAwait(false);
            sent = true;
            if (_seam?.AfterSendBeforeReply is { } afterSend)
            {
                await afterSend(new HostTransportSendContext(method, Endpoint.PipeName, true), cancellationToken).ConfigureAwait(false);
            }

            var reply = await transport.ReadMessageAsync(wait, cancellationToken).ConfigureAwait(false);
            HostV2.VerifyMac(reply, _secret);
            if (reply["kind"].AsString() != "reply")
            {
                throw new HostProtocolException("INVALID_MESSAGE_KIND");
            }

            if (reply["request_id"].AsString() != requestId || reply["session_id"].AsString() != SessionId)
            {
                throw new HostProtocolException("REPLY_MISMATCH");
            }

            return reply;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new HostCallCanceledException(sent);
        }
        catch (HostProtocolException ex) when (sent && ex.Code is "READ_TIMEOUT" or "PIPE_CLOSED" or "WRITE_TIMEOUT")
        {
            throw new HostCallUnknownException(ex.Code, sent);
        }
    }

    public ValueTask DisposeAsync()
    {
        CryptographicClear(_secret);
        return ValueTask.CompletedTask;
    }

    private static async Task<NamedPipeTransport> ConnectAsync(string pipeName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Exception? last = null;
        var deadline = DateTime.UtcNow + timeout;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var transport = new NamedPipeTransport();
            try
            {
                var slice = remaining > TimeSpan.FromMilliseconds(400) ? TimeSpan.FromMilliseconds(400) : remaining;
                await transport.ConnectAsync(pipeName, slice, cancellationToken).ConfigureAwait(false);
                return transport;
            }
            catch (Exception ex) when (ex is HostProtocolException or IOException or TimeoutException)
            {
                last = ex;
                transport.Dispose();
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }

        throw last ?? new HostProtocolException("CONNECT_TIMEOUT");
    }

    private static void CryptographicClear(byte[] secret)
    {
        CryptographicOperations.ZeroMemory(secret);
    }
}

public sealed class HostCallCanceledException : Exception
{
    public HostCallCanceledException(bool sent) : base("Local wait canceled.") => Sent = sent;

    public bool Sent { get; }
}

public sealed class HostCallUnknownException : Exception
{
    public HostCallUnknownException(string code, bool sent) : base(code)
    {
        Code = code;
        Sent = sent;
    }

    public string Code { get; }
    public bool Sent { get; }
}
