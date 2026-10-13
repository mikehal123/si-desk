using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;

namespace Sovereign.Desktop.Client;

/// <summary>Project-scoped file tools and explicitly permitted, unsandboxed Windows terminal commands.</summary>
public sealed class ProjectToolHost
{
    private readonly CursorProjectOptions _options;
    public string Root { get; }
    private const int FileLimit = 1_000_000;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public ProjectToolHost(string root, CursorProjectOptions options)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(Root)) throw new IOException("Selected project folder does not exist.");
        _options = options;
        Resolve(".");
    }

    public static JsonArray Definitions(CursorProjectMode mode)
    {
        var result = new JsonArray();
        void Add(string name, string description, string properties, string required)
        {
            result.Add(new JsonObject { ["type"] = "function", ["function"] = new JsonObject {
                ["name"] = name, ["description"] = description,
                ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = JsonNode.Parse(properties),
                    ["required"] = JsonNode.Parse(required), ["additionalProperties"] = false } } });
        }
        Add("list_files", "List up to 200 immediate children of a project-relative directory. No links are followed.", """{"path":{"type":"string"}}""", """["path"]""");
        Add("read_file", "Read a UTF-8 text file (maximum 1 MB); returns a slice and SHA-256. Lines start at 1; at most 200 lines per call.", """{"path":{"type":"string"},"start_line":{"type":"integer","minimum":1},"line_count":{"type":"integer","minimum":1,"maximum":200}}""", """["path"]""");
        Add("search_files", "Literal case-insensitive text search in project UTF-8 files. Capped at 2000 entries and 100 matches. Skips links, .git, bin, obj and node_modules.", """{"query":{"type":"string"}}""", """["query"]""");
        if (mode == CursorProjectMode.Plan) return result;
        Add("create_file", "Create a new UTF-8 file in an existing project directory. Fails if it exists. Maximum 1 MB.", """{"path":{"type":"string"},"content":{"type":"string"}}""", """["path","content"]""");
        Add("edit_file", "Replace exactly one occurrence in an existing UTF-8 file. expected_sha256 must match the read_file result. Fails on conflicts; maximum 1 MB.", """{"path":{"type":"string"},"old_text":{"type":"string"},"new_text":{"type":"string"},"expected_sha256":{"type":"string"}}""", """["path","old_text","new_text","expected_sha256"]""");
        Add("terminal", "Run a PowerShell command in the project as the current Windows user. NOT sandboxed: commands can access files outside the project. For authorized builds/tests. Output capped; timeout 120s default, maximum 600s. Stop terminates the process tree.", """{"command":{"type":"string"},"timeout_seconds":{"type":"integer","minimum":1,"maximum":600}}""", """["command"]""");
        return result;
    }

    public async Task<string> ExecuteAsync(string id, string name, JsonElement args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var kind = name switch { "list_files" or "read_file" => "read", "search_files" => "search", "create_file" or "edit_file" => "edit", "terminal" => "execute", _ => "other" };
        var activity = new CursorToolActivity(id, name, kind, "pending", args.GetRawText(), []);
        void Report(string status, string output = "", CursorFileChange[]? changes = null) => _options.ToolActivity?.Invoke(activity with { Status = status, Output = output, Changes = changes ?? [] });
        string Result(string status, object value, CursorFileChange[]? changes = null)
        {
            var json = JsonSerializer.Serialize(new { status, result = value });
            Report(status, json, changes); return json;
        }
        Report("pending");
        try
        {
            if (kind == "other" || (_options.Mode == CursorProjectMode.Plan && kind is not ("read" or "search")))
                return Result("blocked", "Tool is unavailable in this mode. It did not run.");
            if (_options.Permissions != CursorPermissionPolicy.AlwaysAllow)
            {
                var allowed = _options.RequestPermission is not null && await _options.RequestPermission(new(activity,
                    [new("allow", "Allow once", "allow_once"), new("deny", "Deny", "reject_once")]), ct) == "allow";
                ct.ThrowIfCancellationRequested();
                if (!allowed) return Result("blocked", "Permission denied. Tool did not run.");
            }
            ct.ThrowIfCancellationRequested();
            Report("in_progress");
            string Text(string key) => args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new IOException("Missing string argument: " + key);
            int Number(string key, int fallback) => args.TryGetProperty(key, out var v) && v.TryGetInt32(out var n) ? n : fallback;
            switch (name)
            {
                case "list_files":
                    var directory = Resolve(Text("path"));
                    var entries = Directory.EnumerateFileSystemEntries(directory).Take(201).ToArray();
                    return Result("completed", new { paths = entries.Take(200).Select(p => Path.GetRelativePath(Root, p)), truncated = entries.Length > 200 });
                case "read_file":
                    var read = Read(Resolve(Text("path")));
                    var lines = read.Text.Split('\n');
                    var start = Number("start_line", 1); var length = Number("line_count", 200);
                    if (start < 1 || length is < 1 or > 200) throw new IOException("Invalid line range.");
                    var slice = string.Join('\n', lines.Skip(start - 1).Take(length));
                    return Result("completed", new { text = Limit(slice), sha256 = read.Hash, total_lines = lines.Length, start_line = start,
                        truncated = start - 1 + length < lines.Length || slice.Length > 24000 });
                case "search_files":
                    var query = Text("query");
                    if (string.IsNullOrEmpty(query) || query.Length > 1000) throw new IOException("Search query must contain 1–1000 characters.");
                    var matches = new List<object>(); var visited = 0;
                    var pending = new Stack<string>(); pending.Push(Root);
                    while (pending.Count > 0 && visited < 2000 && matches.Count < 100)
                    {
                        foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
                        {
                            ct.ThrowIfCancellationRequested();
                            if (++visited >= 2000 || matches.Count >= 100) break;
                            var attributes = File.GetAttributes(entry);
                            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                            if ((attributes & FileAttributes.Directory) != 0)
                            {
                                if (Path.GetFileName(entry) is not (".git" or "node_modules" or "bin" or "obj")) pending.Push(entry);
                                continue;
                            }
                            try
                            {
                                var content = Read(Resolve(Path.GetRelativePath(Root, entry))).Text.Split('\n');
                                for (var i = 0; i < content.Length && matches.Count < 100; i++)
                                    if (content[i].Contains(query, StringComparison.OrdinalIgnoreCase)) matches.Add(new { path = Path.GetRelativePath(Root, entry), line = i + 1, text = content[i][..Math.Min(240, content[i].Length)] });
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException) { }
                        }
                    }
                    return Result("completed", new { matches, truncated = visited >= 2000 || matches.Count >= 100 });
                case "create_file":
                case "edit_file":
                    var path = Resolve(Text("path"));
                    string? before = null; string after; string? hash = null;
                    if (name == "edit_file")
                    {
                        var original = Read(path); before = original.Text; hash = original.Hash;
                        if (!hash.Equals(Text("expected_sha256"), StringComparison.OrdinalIgnoreCase)) throw new IOException("File changed since it was read. Read it again before editing.");
                        var old = Text("old_text"); var at = before.IndexOf(old, StringComparison.Ordinal);
                        if (old.Length == 0 || at < 0 || before.IndexOf(old, at + old.Length, StringComparison.Ordinal) >= 0) throw new IOException("old_text must match exactly once.");
                        after = before[..at] + Text("new_text") + before[(at + old.Length)..];
                    }
                    else after = Text("content");
                    if (Utf8.GetByteCount(after) > FileLimit || after.Contains('\0')) throw new IOException("Text file exceeds limit or contains NUL.");
                    var temp = Path.Combine(Path.GetDirectoryName(path)!, ".sovereign-edit-" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        await File.WriteAllTextAsync(temp, after, Utf8, ct);
                        ct.ThrowIfCancellationRequested(); Resolve(Text("path"));
                        if (hash is not null && Read(path).Hash != hash) throw new IOException("Concurrent file edit detected; no replacement made.");
                        File.Move(temp, path, overwrite: hash is not null);
                    }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                    return Result("completed", new { path = Path.GetRelativePath(Root, path), sha256 = Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(after))).ToLowerInvariant() }, [new(path, before, after)]);
                case "terminal":
                    var command = Text("command");
                    if (string.IsNullOrWhiteSpace(command) || command.Length > 12000) throw new IOException("Command must contain 1–12000 characters.");
                    var seconds = Number("timeout_seconds", 120);
                    if (seconds is < 1 or > 600) throw new IOException("Command timeout must be 1–600 seconds.");
                    var result = await TerminalAsync(command, seconds, ct);
                    return Result(result.ExitCode == 0 ? "completed" : "failed", result);
            }
            throw new IOException("Unknown project tool.");
        }
        catch (OperationCanceledException) { Report("cancelled", "Cancelled. Completed edits are kept."); throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return Result("failed", ex.Message); }
    }

    private string Resolve(string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':')) throw new IOException("Use a project-relative file path.");
        foreach (var segment in relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..") continue;
            var stem = segment.Split('.')[0].ToUpperInvariant();
            if (segment.EndsWith(' ') || segment.EndsWith('.') || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])))
                throw new IOException("Ambiguous or reserved Windows file path.");
        }
        var full = Path.GetFullPath(Path.Combine(Root, relative));
        if (!full.Equals(Root, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(Path.EndsInDirectorySeparator(Root) ? Root : Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Path is outside the selected project.");
        for (var part = full; part is not null; part = Path.GetDirectoryName(part))
            if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) throw new IOException("File tools do not follow links or junctions.");
        return full;
    }
    private static (string Text, string Hash) Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > FileLimit) throw new IOException("File exceeds the 1 MB text limit.");
        var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
        var text = Utf8.GetString(bytes);
        if (text.Contains('\0')) throw new IOException("Binary files are not supported by this text tool.");
        return (text, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }
    private static string Limit(string text) => text.Length <= 24000 ? text : text[..24000] + "\n[Output truncated]";
    private sealed record TerminalResult(int ExitCode, string Stdout, string Stderr);
    private async Task<TerminalResult> TerminalAsync(string command, int seconds, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new IOException("Project terminal requires Windows.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { WorkingDirectory = Root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        // A parent PowerShell 7 session may omit Windows PowerShell's standard modules.
        start.Environment["PSModulePath"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "Modules")
            + ";" + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsPowerShell", "Modules")
            + ";" + Environment.GetEnvironmentVariable("PSModulePath");
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command }) start.ArgumentList.Add(arg);
        using var job = new TerminalJob();
        using var process = new Process { StartInfo = start };
        ct.ThrowIfCancellationRequested();
        process.Start(); process.StandardInput.Close();
        try
        {
            job.Assign(process);
            async Task<string> Drain(StreamReader reader)
            {
                var result = new StringBuilder(); var chars = new char[4096]; int count;
                while ((count = await reader.ReadAsync(chars, timeout.Token)) > 0) if (result.Length < 24001) result.Append(chars, 0, Math.Min(count, 24001 - result.Length));
                return Limit(result.ToString());
            }
            var stdout = Drain(process.StandardOutput); var stderr = Drain(process.StandardError);
            try { await Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException($"Command timed out after {seconds} seconds; process tree stopped."); }
            return new(process.ExitCode, await stdout, await stderr);
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }

    /// <summary>Owns terminal descendants. This is lifetime management, not a security sandbox.</summary>
    private sealed class TerminalJob : IDisposable
    {
        private readonly SafeFileHandle _handle;
        public TerminalJob()
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            _handle = CreateJobObjectW(IntPtr.Zero, null);
            if (_handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } };
            if (!SetInformationJobObject(_handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
            {
                var error = Marshal.GetLastWin32Error(); _handle.Dispose(); throw new Win32Exception(error);
            }
        }

        public void Assign(Process process)
        {
            if (!AssignProcessToJobObject(_handle, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        public void Dispose() => _handle.Dispose();

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimits
        {
            public BasicLimits Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimits information, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    }
}
