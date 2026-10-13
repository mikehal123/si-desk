using Sovereign.Desktop.Client;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class CodexComposerSteeringTests
{
    private static ChatAttachment Attachment(string name) => new(Guid.NewGuid().ToString("N"), name, 10, "hash-" + name, "text", "text/plain", "captured " + name, "Readable");

    [Fact]
    public async Task Enter_during_an_openai_run_accepts_one_correction()
    {
        var session = new CodexComposerSteering(new ChatSteeringQueue());
        var calls = new List<string>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = session.SubmitDuringRunAsync("Use the corrected heading", "gpt-5.4", [], async (text, _) =>
        {
            calls.Add(text);
            entered.TrySetResult();
            await release.Task;
            return true;
        }, CancellationToken.None);
        await entered.Task;
        var duplicate = await session.SubmitDuringRunAsync("duplicate enter", "gpt-5.4", [], (text, _) =>
        {
            calls.Add(text);
            return Task.FromResult(true);
        }, CancellationToken.None);
        release.TrySetResult();
        var accepted = await first;

        Assert.True(duplicate.IgnoredDuplicate);
        Assert.Equal(CodexComposerSteerResult.Accepted, accepted.Result);
        Assert.Equal("Use the corrected heading", accepted.Instruction!.Text);
        Assert.Equal(["Use the corrected heading"], calls);
        Assert.False(session.Queue.HasAutomaticPending);
        Assert.False(session.Queue.IsAutomatic(accepted.Instruction.Id));
        Assert.Equal(accepted.Instruction.Id, Assert.Single(session.Queue.Snapshot).Id);
        Assert.Empty(session.Queue.BeginBatch());
    }

    [Fact]
    public async Task False_steer_queues_the_instruction_once()
    {
        var session = new CodexComposerSteering(new ChatSteeringQueue());
        var calls = 0;
        var result = await session.SubmitDuringRunAsync("Send this on the next turn", "gpt-5.4", [], (_, _) =>
        {
            calls++;
            return Task.FromResult(false);
        }, CancellationToken.None);

        Assert.Equal(CodexComposerSteerResult.QueuedForNextTurn, result.Result);
        Assert.Equal(1, calls);
        Assert.True(session.Queue.HasAutomaticPending);
        var batch = session.Queue.BeginBatch();
        var queued = Assert.Single(batch);
        Assert.Equal(result.Instruction!.Id, queued.Id);
        Assert.Equal("Send this on the next turn", queued.Text);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Stop_leaves_the_instruction_unsent()
    {
        var stopped = new CodexComposerSteering(new ChatSteeringQueue());
        var calls = 0;
        stopped.Stop();
        var held = await stopped.SubmitDuringRunAsync("Do not run this", "gpt-5.4", [], (_, _) =>
        {
            calls++;
            return Task.FromResult(true);
        }, CancellationToken.None);
        Assert.Equal(CodexComposerSteerResult.HeldUnsent, held.Result);
        Assert.Equal(0, calls);
        Assert.Null(held.Failure);
        Assert.False(stopped.Queue.HasAutomaticPending);
        Assert.Equal("Do not run this", Assert.Single(stopped.Queue.Snapshot).Text);
        Assert.Empty(stopped.Queue.BeginBatch());

        var queued = new CodexComposerSteering(new ChatSteeringQueue());
        await queued.SubmitDuringRunAsync("Wait for an explicit send", "gpt-5.4", [], (_, _) => Task.FromResult(false), CancellationToken.None);
        queued.Stop();
        Assert.False(queued.Queue.HasAutomaticPending);
        Assert.Equal("Wait for an explicit send", Assert.Single(queued.Queue.Snapshot).Text);
        Assert.Empty(queued.Queue.BeginBatch());
    }

    [Fact]
    public async Task Attachment_order_and_model_are_kept()
    {
        var first = Attachment("first.txt");
        var second = Attachment("second.txt");
        var files = new[] { first, second };
        var session = new CodexComposerSteering(new ChatSteeringQueue());
        var result = await session.SubmitDuringRunAsync("Use the files in order", "gpt-5.4-mini", files, (_, _) => Task.FromResult(false), CancellationToken.None);
        files[0] = Attachment("later.txt");
        result.Instruction!.Attachments[0] = files[0];

        var queued = Assert.Single(session.Queue.Snapshot);
        Assert.Equal("gpt-5.4-mini", queued.Model);
        Assert.Equal("Use the files in order", queued.Text);
        Assert.Equal([first, second], queued.Attachments);
        var batch = session.Queue.BeginBatch();
        Assert.Equal([first.Id, second.Id], Assert.Single(batch).Attachments.Select(file => file.Id));
        Assert.Equal("gpt-5.4-mini", batch[0].Model);
    }

    [Fact]
    public async Task Second_chat_queue_is_not_consumed()
    {
        var first = new CodexComposerSteering(new ChatSteeringQueue());
        var second = new CodexComposerSteering(new ChatSteeringQueue());
        await first.SubmitDuringRunAsync("only the first chat", "gpt-5.4", [Attachment("a.txt")], (_, _) => Task.FromResult(false), CancellationToken.None);
        await second.SubmitDuringRunAsync("only the second chat", "gpt-5.1", [Attachment("b.txt")], (_, _) => Task.FromResult(false), CancellationToken.None);

        var firstBatch = first.Queue.BeginBatch();
        Assert.Equal("only the first chat", Assert.Single(firstBatch).Text);
        Assert.Equal("gpt-5.4", firstBatch[0].Model);
        var secondPending = Assert.Single(second.Queue.Snapshot);
        Assert.Equal("only the second chat", secondPending.Text);
        Assert.Equal("gpt-5.1", secondPending.Model);
        Assert.True(second.Queue.HasAutomaticPending);
        first.Queue.CompleteBatch(firstBatch, true);
        Assert.Equal(secondPending.Id, Assert.Single(second.Queue.BeginBatch()).Id);
        Assert.Empty(first.Queue.Snapshot);
    }

    [Fact]
    public async Task Failed_steer_keeps_the_instruction_for_explicit_send()
    {
        var session = new CodexComposerSteering(new ChatSteeringQueue());
        var result = await session.SubmitDuringRunAsync("keep this correction", "gpt-5.4", [Attachment("notes.txt")],
            (_, _) => Task.FromException<bool>(new IOException("steer unavailable")), CancellationToken.None);
        Assert.Equal(CodexComposerSteerResult.HeldUnsent, result.Result);
        Assert.Equal("steer unavailable", result.Failure);
        Assert.False(session.Queue.HasAutomaticPending);
        var pending = Assert.Single(session.Queue.Snapshot);
        Assert.Equal("keep this correction", pending.Text);
        Assert.Equal("notes.txt", Assert.Single(pending.Attachments).Name);
        Assert.Empty(session.Queue.BeginBatch());
    }
}
