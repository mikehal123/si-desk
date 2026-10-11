using Sovereign.Desktop.Client;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class ChatSteeringQueueTests
{
    private static ChatAttachment Attachment(string name) => new(Guid.NewGuid().ToString("N"), name, 10, "hash", "text", "text/plain", "captured contents", "Readable");

    [Fact]
    public void Capture_keeps_text_model_and_attachments_independent_of_later_drafts()
    {
        var queue = new ChatSteeringQueue();
        var original = Attachment("first.txt");
        var files = new[] { original };
        var accepted = queue.Accept("Use the first file", "grok", files, true);
        files[0] = Attachment("later.txt");
        accepted.Attachments[0] = files[0];
        var snapshot = queue.Snapshot;
        Assert.Equal("Use the first file", snapshot[0].Text);
        Assert.Equal("grok", snapshot[0].Model);
        Assert.Equal(original, Assert.Single(snapshot[0].Attachments));
        snapshot[0].Attachments[0] = files[0];
        Assert.Equal(original, Assert.Single(queue.Snapshot[0].Attachments));
    }

    [Fact]
    public void Corrections_wait_for_the_current_batch_and_are_combined_in_submission_order()
    {
        var queue = new ChatSteeringQueue();
        var original = queue.Accept("original request", "grok", [], false);
        var active = queue.BeginBatch();
        var first = queue.Accept("keep the original heading", "grok", [], true);
        var second = queue.Accept("also preserve the footer", "grok", [], true);
        Assert.Throws<InvalidOperationException>(() => queue.BeginBatch());
        queue.CompleteBatch(active, delivered: true);
        var batch = queue.BeginBatch();
        Assert.Equal(new[] { first.Id, second.Id }, batch.Select(i => i.Id));
        Assert.DoesNotContain(queue.Snapshot, i => i.Id == original.Id);
        var later = queue.Accept("a third correction", "grok", [], true);
        queue.CompleteBatch(batch, delivered: true);
        Assert.Equal(later.Id, Assert.Single(queue.BeginBatch()).Id);
    }

    [Fact]
    public void Captured_models_are_not_combined_or_changed()
    {
        var queue = new ChatSteeringQueue();
        queue.Accept("first", "model-a", [], true);
        queue.Accept("second", "model-b", [], true);
        queue.Accept("third", "model-a", [], true);
        var first = queue.BeginBatch();
        Assert.Equal("first", Assert.Single(first).Text);
        queue.CompleteBatch(first, true);
        var second = queue.BeginBatch();
        Assert.Equal("model-b", Assert.Single(second).Model);
        queue.CompleteBatch(second, true);
        Assert.Equal("third", Assert.Single(queue.BeginBatch()).Text);
    }

    [Fact]
    public void Stop_prevents_continuation_and_preserves_waiting_instructions_for_explicit_send()
    {
        var queue = new ChatSteeringQueue();
        queue.Accept("original", "grok", [], false);
        var original = queue.BeginBatch();
        var correction = queue.Accept("correction", "grok", [], true);
        queue.Pause();
        queue.CompleteBatch(original, delivered: true);
        Assert.False(queue.HasAutomaticPending);
        Assert.Empty(queue.BeginBatch());
        Assert.Equal(correction.Id, Assert.Single(queue.Snapshot).Id);
        queue.ResumeAll();
        Assert.Equal(correction.Id, Assert.Single(queue.BeginBatch()).Id);
    }

    [Fact]
    public void Failure_retains_uncertain_delivery_and_newly_queued_messages_without_retry()
    {
        var queue = new ChatSteeringQueue();
        queue.Accept("may have changed files", "grok", [], true);
        var dispatched = queue.MarkDispatched(queue.BeginBatch());
        queue.Accept("still waiting", "grok", [], true);
        queue.CompleteBatch(dispatched, delivered: false);
        Assert.True(queue.Snapshot[0].MayHaveBeenSent);
        Assert.False(queue.Snapshot[1].MayHaveBeenSent);
        Assert.False(queue.HasAutomaticPending);
        Assert.Empty(queue.BeginBatch());
    }

    [Fact]
    public void Restart_does_not_send_and_new_send_does_not_resume_old_pending_work()
    {
        var original = new ChatSteeringQueue();
        var old = original.Accept("saved instruction", "grok", [], true);
        var restored = new ChatSteeringQueue(original.Snapshot);
        Assert.False(restored.HasAutomaticPending);
        Assert.Empty(restored.BeginBatch());
        var current = restored.Accept("new explicit message", "grok", [], false);
        var batch = restored.BeginBatch();
        Assert.Equal(current.Id, Assert.Single(batch).Id);
        restored.CompleteBatch(batch, true);
        Assert.Equal(old.Id, Assert.Single(restored.Snapshot).Id);
        restored.ResumeAll();
        Assert.Equal(old.Id, Assert.Single(restored.BeginBatch()).Id);
    }

    [Fact]
    public void Remove_cannot_mutate_the_active_batch_but_can_remove_a_waiting_instruction()
    {
        var queue = new ChatSteeringQueue();
        var active = queue.Accept("active", "grok", [], false);
        queue.BeginBatch();
        var waiting = queue.Accept("waiting", "grok", [], true);
        Assert.False(queue.Remove(active.Id));
        Assert.True(queue.Remove(waiting.Id));
        Assert.False(queue.Remove(waiting.Id));
        Assert.Equal(active.Id, Assert.Single(queue.Snapshot).Id);
    }

    [Fact]
    public void Accepted_correction_stays_saved_and_does_not_start_another_turn()
    {
        var queue = new ChatSteeringQueue();
        var accepted = queue.Accept("correction", "gpt-5.4", [Attachment("a.txt"), Attachment("b.txt")], true);
        var later = queue.Accept("next explicit send", "gpt-5.4", [], true);
        Assert.True(queue.KeepOutOfNextTurn(accepted.Id));
        Assert.False(queue.IsAutomatic(accepted.Id));
        Assert.True(queue.IsAutomatic(later.Id));
        var batch = queue.BeginBatch();
        Assert.Equal(later.Id, Assert.Single(batch).Id);
        queue.CompleteBatch(batch, true);
        Assert.Equal(accepted.Id, Assert.Single(queue.Snapshot).Id);
        Assert.True(queue.DeliverToActiveTurn(accepted.Id));
        Assert.Empty(queue.Snapshot);
        Assert.False(queue.DeliverToActiveTurn(accepted.Id));
    }
}
