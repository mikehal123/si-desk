using System.Text;

namespace Sovereign.Desktop.Connector;

public readonly record struct SwarmArchiveResult(bool Moved, string Reason);

public static class SwarmRunArchive
{
    public static SwarmArchiveResult Move(string conversationFolder, string runId)
    {
        if (string.IsNullOrWhiteSpace(conversationFolder) || string.IsNullOrWhiteSpace(runId))
            return new SwarmArchiveResult(false, "missing board");

        var id = runId.Trim();
        var runDir = Path.Combine(conversationFolder, "runs", id);
        var board = Path.Combine(runDir, "board.json");
        var bak = board + ".bak";
        var archivedDir = Path.Combine(runDir, "archived");
        var archivedBoard = Path.Combine(archivedDir, "board.json");
        var archivedBak = archivedBoard + ".bak";

        if (!File.Exists(board)) return new SwarmArchiveResult(false, "missing board");

        var moveBak = File.Exists(bak);
        if (File.Exists(archivedBoard) || (moveBak && File.Exists(archivedBak)))
            return new SwarmArchiveResult(false, "destination exists");

        Directory.CreateDirectory(archivedDir);
        File.Move(board, archivedBoard, false);
        if (moveBak) File.Move(bak, archivedBak, false);

        var current = Path.Combine(conversationFolder, "current.txt");
        if (File.Exists(current) && string.Equals(File.ReadAllText(current).Trim(), id, StringComparison.Ordinal))
            File.WriteAllText(current, "", new UTF8Encoding(false));

        return new SwarmArchiveResult(true, "moved");
    }
}
