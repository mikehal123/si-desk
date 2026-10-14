namespace Sovereign.Desktop.Connector;

/// <summary>Header counts shared by the tab and the badge.</summary>
public static class HeaderBadges
{
    public readonly record struct SubagentRow(bool SwarmWorker, bool Closed);

    public static int Subagents(IEnumerable<SubagentRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var count = 0;
        foreach (var row in rows)
            if (!row.SwarmWorker && !row.Closed) count++;
        return count;
    }

    public static int Changes(IEnumerable<int> snapshotLengths)
    {
        ArgumentNullException.ThrowIfNull(snapshotLengths);
        var count = 0;
        foreach (var length in snapshotLengths)
            if (length > 0) count += length;
        return count;
    }
}
