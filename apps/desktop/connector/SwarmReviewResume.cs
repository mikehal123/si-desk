namespace Sovereign.Desktop.Connector;

public enum SwarmResumeAction { None, Send, Queue }

public readonly record struct SwarmResumePlan(SwarmResumeAction Action, string? Text);

/// <summary>
/// Decides how a posted Grokbot review starts the manager.
/// Continue and fix send the review text. Done does not.
/// A busy, closed, or unready chat keeps the review queued.
/// </summary>
public static class SwarmReviewResume
{
    public const string NotResumedNotice = "Grokbot review posted but agent not resumed. Click Resume";
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    public static string? SendText(bool resume, string? reviewText)
    {
        if (!resume) return null;
        var text = (reviewText ?? "").Trim();
        return text.Length == 0 ? null : text;
    }

    public static SwarmResumePlan Plan(bool resume, string? reviewText, bool open, bool busy, bool ready, bool handoffHolding)
    {
        var text = SendText(resume, reviewText);
        if (text is null) return new SwarmResumePlan(SwarmResumeAction.None, null);
        if (!open || busy || !ready || handoffHolding)
            return new SwarmResumePlan(SwarmResumeAction.Queue, text);
        return new SwarmResumePlan(SwarmResumeAction.Send, text);
    }

    public static string? Notice(DateTimeOffset postedAt, DateTimeOffset now, bool started)
    {
        if (started || now - postedAt < Deadline) return null;
        return NotResumedNotice;
    }
}
