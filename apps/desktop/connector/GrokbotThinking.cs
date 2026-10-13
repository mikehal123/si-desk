namespace Sovereign.Desktop.Connector;

public static class GrokbotThinking
{
    public const string Thinking = "Grokbot is thinking…";
    public const string StillWorking = "Still working…";
    public static readonly TimeSpan StillWorkingAfter = TimeSpan.FromMinutes(5);

    public static string Label(GrokbotMailStatus status, string? reply, bool stopped, DateTimeOffset created, DateTimeOffset? pickedUpAt, DateTimeOffset now)
    {
        if (stopped || !string.IsNullOrWhiteSpace(reply) || (status != GrokbotMailStatus.Queued && status != GrokbotMailStatus.PickedUp))
            return "";
        var anchor = pickedUpAt ?? created;
        return now - anchor < StillWorkingAfter ? Thinking : StillWorking;
    }

    public static TimeSpan PollInterval(TimeSpan baseline, bool anyPickedUp)
    {
        if (!anyPickedUp || baseline < TimeSpan.FromSeconds(2)) return baseline;
        return TimeSpan.FromSeconds(2);
    }
}
