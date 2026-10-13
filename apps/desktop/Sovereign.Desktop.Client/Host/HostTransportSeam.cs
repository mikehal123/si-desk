namespace Sovereign.Desktop.Client.Host;

/// <summary>
/// Optional test-only attachment for one <see cref="NativeOperatorClient"/> instance.
/// Production leaves this null. It is not read from CLI arguments or environment variables.
/// </summary>
/// <remarks>
/// G3 uses <see cref="AfterSendBeforeReply"/> to wait until the installed host has persisted
/// <c>offline_demo.start</c>, then cancel the client wait so the real reply is never read.
/// Discovery, session_open, MAC verification, and reply identity checks are unchanged.
/// </remarks>
public sealed class HostTransportSeam
{
    /// <summary>
    /// Invoked after a request frame is written on an already-authenticated session,
    /// before the reply is read or validated. Not invoked for <c>session_open</c>.
    /// </summary>
    public Func<HostTransportSendContext, CancellationToken, Task>? AfterSendBeforeReply { get; init; }
}

public readonly record struct HostTransportSendContext(string Method, string PipeName, bool Sent);
