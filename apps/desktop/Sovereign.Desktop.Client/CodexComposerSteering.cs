namespace Sovereign.Desktop.Client;

public enum CodexComposerSteerResult
{
    Accepted,
    QueuedForNextTurn,
    HeldUnsent
}

/// <summary>One OpenAI conversation's correction. A false or failed steer never becomes a second turn by itself.</summary>
public sealed record CodexComposerSubmission(CodexComposerSteerResult Result, SavedChatInstruction? Instruction, string? Failure, bool IgnoredDuplicate = false);

/// <summary>
/// Enter/Send while an OpenAI run is active. The caller supplies <c>RequestSteeringAsync</c>.
/// This type never starts a turn and never touches another conversation's queue.
/// </summary>
public sealed class CodexComposerSteering
{
    private readonly ChatSteeringQueue _queue;
    private int _accepting;

    public CodexComposerSteering(ChatSteeringQueue queue) => _queue = queue ?? throw new ArgumentNullException(nameof(queue));

    public ChatSteeringQueue Queue => _queue;
    public bool StopRequested { get; private set; }

    public void Stop()
    {
        StopRequested = true;
        _queue.Pause();
    }

    /// <summary>Clears Stop for an explicit later send. Previously paused instructions stay paused.</summary>
    public void Resume() => StopRequested = false;

    public async Task<CodexComposerSubmission> SubmitDuringRunAsync(string text, string model, IReadOnlyList<ChatAttachment> attachments,
        Func<string, CancellationToken, Task<bool>> requestSteering, CancellationToken cancellationToken)
    {
        if (requestSteering is null) throw new ArgumentNullException(nameof(requestSteering));
        if (Interlocked.CompareExchange(ref _accepting, 1, 0) != 0)
            return new CodexComposerSubmission(CodexComposerSteerResult.HeldUnsent, null, null, IgnoredDuplicate: true);
        try
        {
            var instruction = _queue.Accept(text, model, attachments, true);
            if (StopRequested)
            {
                _queue.Pause();
                return new CodexComposerSubmission(CodexComposerSteerResult.HeldUnsent, instruction, null);
            }
            bool accepted;
            try
            {
                accepted = await requestSteering(instruction.Text, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _queue.Pause();
                _queue.MarkMayHaveBeenSent(instruction.Id);
                return new CodexComposerSubmission(CodexComposerSteerResult.HeldUnsent, instruction, null);
            }
            catch (Exception ex)
            {
                _queue.Pause();
                return new CodexComposerSubmission(CodexComposerSteerResult.HeldUnsent, instruction, ex.Message);
            }
            if (StopRequested)
            {
                _queue.Pause();
                if (accepted) _queue.MarkMayHaveBeenSent(instruction.Id);
                return new CodexComposerSubmission(CodexComposerSteerResult.HeldUnsent, instruction, null);
            }
            if (!accepted) return new CodexComposerSubmission(CodexComposerSteerResult.QueuedForNextTurn, instruction, null);
            if (!_queue.KeepOutOfNextTurn(instruction.Id))
            {
                _queue.Pause();
                return new CodexComposerSubmission(CodexComposerSteerResult.HeldUnsent, instruction, "The correction could not be kept in this chat.");
            }
            return new CodexComposerSubmission(CodexComposerSteerResult.Accepted, instruction, null);
        }
        finally { Interlocked.Exchange(ref _accepting, 0); }
    }
}
