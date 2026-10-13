namespace Sovereign.Desktop.Phone;

public sealed record PhoneChatSummary(string Id, string Title, string Provider, string? Model, bool Working, DateTimeOffset Updated, string? ProjectId);

public sealed record PhoneChatTurn(string Prompt, string Reply, string Outcome, string Model);

public sealed record PhoneSubagentStatus(string Name, string State, string Work);

public sealed record PhoneChatDetail(string Id, string Title, string Provider, string? Model, string Status, bool Working, string? ProjectId, string LiveReply, string ActivityPhase, PhoneChatTurn[] Turns, PhoneSubagentStatus[] Subagents);

public sealed record PhoneSendResult(bool Accepted, string? Error);

public sealed record PhoneNotification(long Cursor, string Kind, string ChatId, string Text);

/// <summary>The live desktop window. Phone access does not open a second model client.</summary>
public interface IPhoneChatBridge
{
    Task<IReadOnlyList<PhoneChatSummary>> ListAsync(CancellationToken ct);
    Task<PhoneChatDetail?> GetAsync(string id, CancellationToken ct);
    Task<PhoneSendResult> SendAsync(string id, string text, CancellationToken ct);
    Task<bool> StopAsync(string id, CancellationToken ct);
}
