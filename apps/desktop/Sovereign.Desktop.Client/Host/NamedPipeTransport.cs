using System.Buffers.Binary;
using System.IO.Pipes;

namespace Sovereign.Desktop.Client.Host;

public sealed class NamedPipeTransport : IDisposable, IAsyncDisposable
{
    private NamedPipeClientStream? _pipe;

    public bool IsConnected => _pipe?.IsConnected == true;

    public async Task ConnectAsync(string pipeName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var name = NormalizePipeName(pipeName);
        _pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, Sovereign.Desktop.Client.LocalPipeAccess.Client);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        try
        {
            await _pipe.ConnectAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HostProtocolException("CONNECT_TIMEOUT");
        }
    }

    public async Task WriteMessageAsync(HostJson message, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var frame = HostV2.EncodeFrame(message);
        await WriteExactAsync(frame, timeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task<HostJson> ReadMessageAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var header = await ReadExactAsync(4, timeout, cancellationToken).ConfigureAwait(false);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (size > HostV2.MaxMessageBytes)
        {
            throw new HostProtocolException("MESSAGE_TOO_LARGE");
        }

        var body = await ReadExactAsync((int)size, timeout, cancellationToken).ConfigureAwait(false);
        var value = HostV2.ParseJsonObject(body);
        HostV2.ValidateMessage(value);
        return value;
    }

    public async Task WriteRawAsync(byte[] bytes, TimeSpan timeout, CancellationToken cancellationToken) =>
        await WriteExactAsync(bytes, timeout, cancellationToken).ConfigureAwait(false);

    public void Dispose()
    {
        _pipe?.Dispose();
        _pipe = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_pipe is not null)
        {
            await _pipe.DisposeAsync().ConfigureAwait(false);
            _pipe = null;
        }
    }

    public static string NormalizePipeName(string pipeName)
    {
        const string prefix = @"\\.\pipe\";
        return pipeName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? pipeName[prefix.Length..]
            : pipeName;
    }

    private async Task<byte[]> ReadExactAsync(int count, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_pipe is null)
        {
            throw new HostProtocolException("NOT_CONNECTED");
        }

        var buffer = new byte[count];
        var offset = 0;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        try
        {
            while (offset < count)
            {
                var n = await _pipe.ReadAsync(buffer.AsMemory(offset, count - offset), linked.Token).ConfigureAwait(false);
                if (n == 0)
                {
                    throw new HostProtocolException("PIPE_CLOSED");
                }

                offset += n;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HostProtocolException("READ_TIMEOUT");
        }

        return buffer;
    }

    private async Task WriteExactAsync(byte[] bytes, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_pipe is null)
        {
            throw new HostProtocolException("NOT_CONNECTED");
        }

        var offset = 0;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        try
        {
            while (offset < bytes.Length)
            {
                var remaining = bytes.Length - offset;
                await _pipe.WriteAsync(bytes.AsMemory(offset, remaining), linked.Token).ConfigureAwait(false);
                offset += remaining;
            }

            await _pipe.FlushAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HostProtocolException("WRITE_TIMEOUT");
        }
    }
}
