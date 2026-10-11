using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace Sovereign.Desktop.Client;

internal static class CursorToolParser
{
    private static readonly ConditionalWeakTable<Dictionary<string, CursorToolActivity>, CursorToolRetention> Retention = new();
    internal static CursorToolRetention History(Dictionary<string, CursorToolActivity> tools, int limit = 1000)
    {
        var history = Retention.GetValue(tools, values => new(values, limit));
        if (tools.Count == 0 && history.Summary.Total > 0) history.Reset();
        return history;
    }
    internal static CursorToolActivity? Find(Dictionary<string, CursorToolActivity> tools, string id, int limit = 1000) => History(tools, limit).Find(id);
    internal static void Reset(Dictionary<string, CursorToolActivity> tools) { if (Retention.TryGetValue(tools, out var history)) history.Reset(); tools.Clear(); }
    internal static void Finish(Dictionary<string, CursorToolActivity> tools) { if (Retention.TryGetValue(tools, out var history)) history.Finish(); }
    internal static CursorToolActivity Merge(JsonElement update, Dictionary<string, CursorToolActivity> tools, string prefix = "", int limit = 1000)
    {
        var sourceId = update.GetProperty("toolCallId").GetString() ?? throw new IOException("Missing Cursor tool identity.");
        var id = prefix + sourceId;
        if (string.IsNullOrWhiteSpace(sourceId) || sourceId.Length > 512) throw new IOException("Invalid Cursor tool identity.");
        var history = History(tools, limit);
        var prior = history.Find(id) ?? new CursorToolActivity(id, "Tool", "other", "pending", "", []);
        string Field(string name, string fallback) => update.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : fallback;
        var changes = prior.Changes;
        var output = prior.Output;
        if (update.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            var diffs = content.EnumerateArray().Where(c => c.TryGetProperty("type", out var type) && type.GetString() == "diff")
                .Select(c => new CursorFileChange(c.GetProperty("path").GetString()!, c.GetProperty("oldText").GetString(), c.GetProperty("newText").GetString()!)).ToArray();
            if (diffs.Length > 0) changes = diffs;
            var text = string.Join("\n", content.EnumerateArray().Where(c => c.TryGetProperty("type", out var t) && t.GetString() == "content"
                && c.TryGetProperty("content", out var inner) && inner.TryGetProperty("type", out var it) && it.GetString() == "text")
                .Select(c => c.GetProperty("content").GetProperty("text").GetString()));
            if (text.Length > 0) output = text.Length > 32000 ? text[..32000] + "\n[Output display truncated]" : text;
        }
        var next = prior with { Title = Clip(Field("title", prior.Title), 240), Kind = Clip(Field("kind", prior.Kind), 80), Status = Clip(Field("status", prior.Status), 80),
            Input = update.TryGetProperty("rawInput", out var input) ? Clip(input.GetRawText(), 32000) : prior.Input, Changes = changes, Output = output };
        if (next.Kind == "execute" && next.Status == "completed" && next.Output.TrimStart().StartsWith("Not in allowlist:", StringComparison.OrdinalIgnoreCase))
            next = next with { Status = "blocked" };
        history.Store(next);
        return next;
    }
    private static string Clip(string text, int maximum) => text.Length <= maximum ? text : text[..maximum] + "\n[Input display truncated]";
}

/// <summary>Bounded display cache with private, exact correlation for evicted calls. No execution authority.</summary>
internal sealed class CursorToolRetention(Dictionary<string, CursorToolActivity> tools, int limit) : IDisposable
{
    private const long DetailCharacters = 8_000_000;
    private const int MaximumChanges = 512;
    private readonly Dictionary<string, long> _statuses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CursorFileChange> _changes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _spilledPreviews = [];
    private string? _folder;
    private long _total;
    private int _changedFiles;
    private bool _changesTruncated;
    private string? _storageError;
    private bool _countsIncomplete;
    private string? _archiveDirectory;
    private CursorFileChange[]? _snapshot;
    private long Count(string status) => _statuses.GetValueOrDefault(status);
    internal static bool Active(CursorToolActivity tool) => tool.Status is "pending" or "in_progress";
    public CursorToolSummary Summary => new(_total, Count("pending") + Count("in_progress"), Count("completed"), Count("failed"),
        Count("blocked"), Count("unconfirmed"), Math.Max(0, _total - tools.Count), _changedFiles,
        _changesTruncated || _changedFiles > _changes.Count, tools.Count, Count("cancelled"),
        Count("other"), _storageError, _countsIncomplete);
    public CursorFileChange[] Changes => _snapshot ??= _changes.Values.ToArray();
    public CursorChangeArchive? ChangeArchive => _archiveDirectory is null ? null : new(_archiveDirectory, _changedFiles);
    private static string Bucket(string status) => status is "pending" or "in_progress" or "completed" or "failed" or "blocked" or "unconfirmed" or "cancelled" ? status : "other";
    public CursorToolActivity? Find(string id)
    {
        if (tools.TryGetValue(id, out var current) && !_spilledPreviews.Contains(id)) return current;
        return Read<CursorToolActivity>("tool", id) ?? current;
    }
    public void Store(CursorToolActivity tool)
    {
        var prior = Find(tool.Id);
        if (prior is null) _total++;
        else _statuses[Bucket(prior.Status)] = Math.Max(0, Count(Bucket(prior.Status)) - 1);
        _statuses[Bucket(tool.Status)] = Count(Bucket(tool.Status)) + 1;
        if (tool.Status == "completed" && (prior?.Status != "completed" || !prior.Changes.SequenceEqual(tool.Changes)))
            foreach (var change in tool.Changes) Aggregate(change);
        _spilledPreviews.Remove(tool.Id);
        tools.Remove(tool.Id); // Dictionary insertion order is the bounded recent order.
        if (Size(tool) > DetailCharacters)
        {
            Write("tool", tool.Id, tool);
            _spilledPreviews.Add(tool.Id);
            tools[tool.Id] = tool with { Changes = [] };
        }
        else tools[tool.Id] = tool;
        while (tools.Count > limit || tools.Values.Sum(Size) > DetailCharacters)
        {
            var candidates = tools.Values.Where(t => t.Id != tool.Id).ToArray();
            var oldest = candidates.FirstOrDefault(t => !Active(t)) ?? candidates.FirstOrDefault();
            if (oldest is null) break;
            if (!_spilledPreviews.Contains(oldest.Id)) Write("tool", oldest.Id, oldest);
            tools.Remove(oldest.Id); _spilledPreviews.Remove(oldest.Id);
        }
    }
    private void Aggregate(CursorFileChange change)
    {
        var prior = _changes.GetValueOrDefault(change.Path) ?? Read<CursorFileChange>("change", change.Path.ToUpperInvariant());
        if (prior is null) _changedFiles++;
        var combined = prior is null ? change : change with { Before = prior.Before };
        // Every exact aggregate has a durable record before its display cache can evict it.
        Write("change", change.Path.ToUpperInvariant(), combined);
        if (_archiveDirectory is not null) CopyChange(FileFor("change", change.Path.ToUpperInvariant()), _archiveDirectory);
        _changes.Remove(change.Path);
        if (Size(combined) <= DetailCharacters) _changes[change.Path] = combined;
        else { Write("change", change.Path.ToUpperInvariant(), combined); _changesTruncated = true; }
        while (_changes.Count > MaximumChanges || _changes.Values.Sum(Size) > DetailCharacters)
        {
            var oldest = _changes.First(); Write("change", oldest.Key.ToUpperInvariant(), oldest.Value); _changes.Remove(oldest.Key);
        }
        _snapshot = null;
    }
    public CursorChangeArchive? ExportChanges(string directory)
    {
        try
        {
            var full = Path.GetFullPath(directory);
            Directory.CreateDirectory(full);
            if (_folder is not null)
                foreach (var path in Directory.EnumerateFiles(_folder, "change-*.json")) CopyChange(path, full);
            _archiveDirectory = full;
            return ChangeArchive;
        }
        catch (Exception error) when (StorageFailure(error)) { StorageNotice(); return null; }
    }
    private void CopyChange(string source, string directory)
    {
        try
        {
            var target = Path.Combine(directory, Path.GetFileName(source));
            var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.Copy(source, temp, true); File.Move(temp, target, true); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        catch (Exception error) when (StorageFailure(error)) { StorageNotice(); }
    }
    public void Import(CursorToolSummary summary, CursorFileChange[] changes)
    {
        _total = summary.Total; _changedFiles = summary.ChangedFiles; _changesTruncated = summary.ChangeDetailsTruncated;
        _storageError = summary.StorageError; _countsIncomplete = summary.CountsIncomplete;
        _statuses.Clear(); _statuses["in_progress"] = Math.Min(summary.Active, tools.Values.Count(t => t.Status == "in_progress"));
        _statuses["pending"] = summary.Active - Count("in_progress"); _statuses["completed"] = summary.Completed;
        _statuses["failed"] = summary.Failed; _statuses["blocked"] = summary.Blocked; _statuses["unconfirmed"] = summary.Unconfirmed;
        _statuses["cancelled"] = summary.Cancelled; _statuses["other"] = summary.Other;
        _changes.Clear(); foreach (var change in changes.Take(MaximumChanges)) _changes[change.Path] = change;
        _snapshot = null;
    }
    public void ImportArchive(CursorChangeArchive archive, string trustedRoot)
    {
        try
        {
            foreach (var change in CursorActivityRetention.AllChanges(archive, trustedRoot))
            {
                _changes[change.Path] = change;
                Write("change", change.Path.ToUpperInvariant(), change);
                while (_changes.Count > MaximumChanges || _changes.Values.Sum(Size) > DetailCharacters)
                    _changes.Remove(_changes.First().Key);
            }
            _archiveDirectory = archive.Directory; _snapshot = null;
        }
        catch (Exception error) when (StorageFailure(error) || error is ArgumentException) { StorageNotice(); }
    }
    public void Finish()
    {
        var active = Count("pending") + Count("in_progress");
        _statuses["pending"] = _statuses["in_progress"] = 0;
        _statuses["unconfirmed"] = Count("unconfirmed") + active;
        foreach (var (id, tool) in tools.ToArray()) if (Active(tool)) tools[id] = tool with { Status = "unconfirmed" };
        Dispose();
    }
    public void Reset()
    {
        Dispose(); tools.Clear(); _statuses.Clear(); _changes.Clear(); _spilledPreviews.Clear();
        _total = 0; _changedFiles = 0; _changesTruncated = false; _snapshot = null;
        _storageError = null; _countsIncomplete = false; _archiveDirectory = null;
    }
    private static long Size(CursorToolActivity tool) => (long)tool.Id.Length + tool.Title.Length + tool.Kind.Length + tool.Status.Length + tool.Input.Length + tool.Output.Length + tool.Changes.Sum(Size);
    private static long Size(CursorFileChange change) => (long)change.Path.Length + (change.Before?.Length ?? 0) + change.After.Length + (change.UnifiedDiff?.Length ?? 0);
    private string FileFor(string kind, string id)
    {
        _folder ??= Path.Combine(Path.GetTempPath(), "Sovereign-cursor-activity-" + Guid.NewGuid().ToString("N"));
        return Path.Combine(_folder, kind + "-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id))) + ".json");
    }
    private T? Read<T>(string kind, string id) where T : class
    {
        if (_folder is null) return null;
        try
        {
            var path = FileFor(kind, id); if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 64 * 1024 * 1024) throw new IOException("Stored Cursor activity exceeds its load limit.");
            return JsonSerializer.Deserialize<T>(stream);
        }
        catch (Exception error) when (StorageFailure(error)) { StorageNotice(); return null; }
    }
    private void Write<T>(string kind, string id, T value)
    {
        try
        {
            var path = FileFor(kind, id); Directory.CreateDirectory(_folder!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) JsonSerializer.Serialize(stream, value);
                File.Move(temp, path, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        catch (Exception error) when (StorageFailure(error)) { StorageNotice(); }
    }
    private static bool StorageFailure(Exception error) => error is IOException or UnauthorizedAccessException or JsonException;
    private void StorageNotice() { _storageError = "Some activity details could not be stored or restored; the model was not stopped by the display limit."; _countsIncomplete = true; _changesTruncated = true; }
    public void Dispose()
    {
        var folder = _folder; _folder = null;
        if (folder is null) return;
        // Only the exact privately generated temp child is eligible for recursive cleanup.
        if (Path.GetDirectoryName(Path.GetFullPath(folder)) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
            !Path.GetFileName(folder).StartsWith("Sovereign-cursor-activity-", StringComparison.Ordinal)) return;
        try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    ~CursorToolRetention() => Dispose();
}
