namespace Sovereign.Desktop.Core;

public enum PresenceKind
{
    Known,
    Unknown,
    Missing
}

public enum ApplicationMode
{
    DesktopPreview,
    EvidenceArchive,
    NativeConnection
}

public enum BackendType
{
    PreviewFixture,
    HistoricalArchive,
    NativeHost,
    Disconnected
}

public enum EngineState
{
    Unknown,
    Stopped,
    Starting,
    Running,
    Paused,
    Degraded
}

public enum ConnectivityState
{
    Unknown,
    Disconnected,
    Connecting,
    Connected,
    Unavailable
}

public enum ReconciliationState
{
    Unknown,
    Current,
    Stale,
    Discrepancy,
    Incomplete
}

public enum AuthorizationState
{
    Unknown,
    None,
    Expired,
    Granted,
    Paused,
    Revoked
}

public enum GateVerdict
{
    Unknown,
    Unsupported,
    Pass,
    Fail,
    Blocked,
    Missing
}

public enum DecisionAction
{
    Unknown,
    Buy,
    Hold,
    Reduce,
    Exit,
    Abstain
}

public enum CycleTiming
{
    Unknown,
    OnTime,
    Missed,
    Late
}

public enum ArchiveTrust
{
    Unverified,
    HashConsistent,
    Invalid,
    Unsupported
}

public enum OperationStatus
{
    Unknown,
    Accepted,
    Completed,
    Rejected,
    TimedOut,
    Unavailable
}

/// <summary>
/// Distinguishes why a native query did not produce a usable COMPLETED result.
/// UNAVAILABLE is a capability/method state, not a dropped session.
/// </summary>
public enum HostQueryFailure
{
    None,
    Disconnected,
    Transport,
    Protocol,
    Unavailable,
    Rejected,
    Schema
}

public enum ActivationStatus
{
    DraftNotActivated,
    ActivationUnavailable
}

public static class ModeLabels
{
    public static string Banner(ApplicationMode mode) => mode switch
    {
        ApplicationMode.DesktopPreview => "PREVIEW MODE - scenario data. Not a live account, not resea" + "rch, not a submission.",
        ApplicationMode.EvidenceArchive => "ARCHIVE MODE - historical packet. Immutable. Not current authorization or current account.",
        ApplicationMode.NativeConnection => "NATIVE CONNECTION — host is authoritative. Offline synthetic execution only. Not live trad" + "ing. Absence of host data is not preview data.",
        _ => "MODE UNKNOWN"
    };

    public static string Short(ApplicationMode mode) => mode switch
    {
        ApplicationMode.DesktopPreview => "Desktop Preview",
        ApplicationMode.EvidenceArchive => "Evidence Archive",
        ApplicationMode.NativeConnection => "Native Connection",
        _ => "Unknown mode"
    };

    public static string OfflineDemoBanner(ApplicationMode mode) => mode switch
    {
        ApplicationMode.DesktopPreview => "OFFLINE DEMO · DEMO FIXTURE · No live trad" + "ing",
        ApplicationMode.EvidenceArchive => "OFFLINE DEMO · EVIDENCE ARCHIVE · Read only",
        ApplicationMode.NativeConnection => "OFFLINE DEMO · NATIVE SYNTHETIC · No live trad" + "ing",
        _ => "OFFLINE DEMO · MODE UNKNOWN"
    };
}
