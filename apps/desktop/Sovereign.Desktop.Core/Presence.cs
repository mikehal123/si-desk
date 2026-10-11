namespace Sovereign.Desktop.Core;

public readonly record struct Presence<T> where T : notnull
{
    public PresenceKind Kind { get; init; }
    public T? Value { get; init; }
    public string? Reason { get; init; }

    public bool IsKnown => Kind == PresenceKind.Known && Value is not null;
    public bool IsUnknown => Kind == PresenceKind.Unknown;
    public bool IsMissing => Kind == PresenceKind.Missing;

    public static Presence<T> Known(T value) =>
        new() { Kind = PresenceKind.Known, Value = value };

    public static Presence<T> Unknown(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new Presence<T> { Kind = PresenceKind.Unknown, Reason = reason };
    }

    public static Presence<T> Missing(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new Presence<T> { Kind = PresenceKind.Missing, Reason = reason };
    }

    public string Describe(Func<T, string>? format = null)
    {
        return Kind switch
        {
            PresenceKind.Known when Value is not null => format is null ? Value.ToString() ?? "known" : format(Value),
            PresenceKind.Unknown => $"unknown ({Reason})",
            PresenceKind.Missing => $"missing ({Reason})",
            _ => "unknown (unspecified)"
        };
    }
}
