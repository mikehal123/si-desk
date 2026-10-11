using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sovereign.Desktop.Connector;

public static class SwarmStatus
{
    public const string Todo = "todo";
    public const string Doing = "doing";
    public const string Review = "review";
    public const string Done = "done";
    public const string Failed = "failed";
}

public static class SwarmRole
{
    public const string Engineer = "engineer";
    public const string Investigator = "investigator";
    public const string Researcher = "researcher";
    public const string Verifier = "verifier";
    public const string GrokbotResearch = "grokbot-research";
    public const string GrokbotOps = "grokbot-ops";

    public static bool IsGrokbot(string? role) =>
        string.Equals(role, GrokbotResearch, StringComparison.Ordinal)
        || string.Equals(role, GrokbotOps, StringComparison.Ordinal);
}

public sealed class SwarmTask
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("role")] public string Role { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = SwarmStatus.Todo;
    [JsonPropertyName("assignee")] public string Assignee { get; set; } = "";
    [JsonPropertyName("result")] public string Result { get; set; } = "";
    [JsonPropertyName("proof")] public string Proof { get; set; } = "";
    [JsonPropertyName("verifier_note")] public string VerifierNote { get; set; } = "";
    [JsonPropertyName("details")] public string Details { get; set; } = "";
    [JsonPropertyName("retries")] public int Retries { get; set; }
    [JsonPropertyName("interrupt_retries")] public int InterruptRetries { get; set; }
    [JsonPropertyName("files")] public List<string> Files { get; set; } = [];
    [JsonPropertyName("after")] public List<string> After { get; set; } = [];
}

public sealed class SwarmBoard
{
    public const int MaxRetries = 2;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true
    };

    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("goal")] public string Goal { get; set; } = "";
    [JsonPropertyName("tasks")] public List<SwarmTask> Tasks { get; set; } = [];

    [JsonIgnore] public string Root { get; set; } = DefaultRoot();
    [JsonIgnore] public string BoardPath => Path.Combine(Root, Id, "board.json");

    public static string DefaultRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "xai-desk", "swarm");

    public static string PanelState(string status) => status switch
    {
        SwarmStatus.Todo => "queued",
        SwarmStatus.Doing or SwarmStatus.Review => "running",
        SwarmStatus.Done => "completed",
        SwarmStatus.Failed => "failed",
        _ => status
    };

    public static SwarmBoard Create(string goal, IEnumerable<SwarmTask> tasks, string? root = null, string? id = null)
    {
        if (string.IsNullOrWhiteSpace(goal)) throw new ArgumentException("Swarm goal is required.", nameof(goal));
        var board = new SwarmBoard
        {
            Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id.Trim(),
            Goal = goal.Trim(),
            Root = string.IsNullOrWhiteSpace(root) ? DefaultRoot() : root,
            Tasks = tasks.Select(Copy).ToList()
        };
        if (board.Tasks.Count == 0) throw new ArgumentException("A swarm needs at least one task.", nameof(tasks));
        if (board.Tasks.Any(task => string.IsNullOrWhiteSpace(task.Id) || string.IsNullOrWhiteSpace(task.Title)))
            throw new ArgumentException("Every task needs an id and a title.", nameof(tasks));
        foreach (var task in board.Tasks)
            if (string.IsNullOrWhiteSpace(task.Status)) task.Status = SwarmStatus.Todo;
        if (File.Exists(board.BoardPath))
            throw new IOException("A swarm board already exists at that path. Start a new run instead of overwriting it.");
        board.Save();
        return board;
    }

    public static SwarmBoard Read(string path)
    {
        var board = JsonSerializer.Deserialize<SwarmBoard>(File.ReadAllText(path), Json) ?? throw new IOException("Swarm board is empty.");
        var directory = Path.GetDirectoryName(path);
        board.Root = string.IsNullOrEmpty(directory) ? "" : Path.GetDirectoryName(directory) ?? "";
        var directoryName = string.IsNullOrEmpty(directory) ? "" : Path.GetFileName(directory);
        board.Id = string.IsNullOrWhiteSpace(board.Id) ? directoryName : board.Id;
        board.Tasks ??= [];
        foreach (var task in board.Tasks)
        {
            if (task is null) continue;
            task.Files ??= [];
            task.After ??= [];
        }
        return board;
    }

    public static SwarmBoard Load(string id, string? root = null)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Swarm id is required.", nameof(id));
        var folder = string.IsNullOrWhiteSpace(root) ? DefaultRoot() : root;
        var path = Path.Combine(folder, id.Trim(), "board.json");
        if (!File.Exists(path)) throw new KeyNotFoundException("That swarm board is not on disk.");
        var board = Read(path);
        board.Root = folder;
        board.Id = string.IsNullOrWhiteSpace(board.Id) ? id.Trim() : board.Id;
        board.Tasks ??= [];
        return board;
    }

    public static IReadOnlyList<string> ListIds(string? root = null)
    {
        var folder = string.IsNullOrWhiteSpace(root) ? DefaultRoot() : root;
        if (!Directory.Exists(folder)) return [];
        return Directory.GetDirectories(folder)
            .Select(path => Path.GetFileName(path))
            .Where(name => File.Exists(Path.Combine(folder, name, "board.json")))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    public static string ReadText(string? swarmId, string? root = null)
    {
        var folder = string.IsNullOrWhiteSpace(root) ? DefaultRoot() : root;
        if (string.IsNullOrWhiteSpace(swarmId))
        {
            var lines = ListIds(folder).Select(id =>
            {
                var board = Load(id, folder);
                return id + "\t" + board.Goal.Replace('\n', ' ') + "\t" + string.Join(",", board.Tasks.Select(task => task.Id + ":" + task.Status));
            });
            var text = string.Join("\n", lines);
            return text.Length == 0 ? "No swarm boards." : text;
        }
        return File.ReadAllText(Load(swarmId.Trim(), folder).BoardPath);
    }

    public static SwarmBoard RequireTask(string taskId, string? root, out SwarmTask task)
    {
        if (string.IsNullOrWhiteSpace(taskId)) throw new ArgumentException("Task id is required.", nameof(taskId));
        foreach (var id in ListIds(root))
        {
            var board = Load(id, root);
            var found = board.Tasks.FirstOrDefault(item => item.Id == taskId);
            if (found is null) continue;
            task = found;
            return board;
        }
        throw new KeyNotFoundException("That swarm task is not on a board.");
    }

    public SwarmTask TaskById(string taskId) =>
        Tasks.FirstOrDefault(task => task.Id == taskId) ?? throw new KeyNotFoundException("That task is not on this board.");

    public SwarmTask Start(string taskId, string assignee)
    {
        var task = TaskById(taskId);
        if (task.Status != SwarmStatus.Todo) throw new InvalidOperationException("Only a todo task can move to doing.");
        if (!SwarmSchedule.CanStart(this, taskId))
            throw new InvalidOperationException("That task is not ready. At most 4 tasks can be doing, and a task waits while its files are in use or its earlier tasks are not done.");
        task.Status = SwarmStatus.Doing;
        task.Assignee = assignee?.Trim() ?? "";
        Save();
        return task;
    }

    public SwarmTask Submit(string taskId, string result, string proof)
    {
        var task = TaskById(taskId);
        if (task.Status is not (SwarmStatus.Doing or SwarmStatus.Todo))
            throw new InvalidOperationException("Only a todo or doing task can move to review.");
        if (string.IsNullOrWhiteSpace(proof)) throw new ArgumentException("Nothing is done without proof.");
        task.Result = result ?? "";
        task.Proof = proof.Trim();
        task.Status = SwarmStatus.Review;
        Save();
        return task;
    }

    public SwarmTask Verify(string taskId, bool pass, string note)
    {
        var task = TaskById(taskId);
        if (task.Status != SwarmStatus.Review) throw new InvalidOperationException("Only the verifier can finish a task, and only from review.");
        task.VerifierNote = note ?? "";
        if (pass)
        {
            if (string.IsNullOrWhiteSpace(task.Proof)) throw new InvalidOperationException("Nothing is done without proof.");
            task.Status = SwarmStatus.Done;
        }
        else if (task.Retries >= MaxRetries)
        {
            task.Status = SwarmStatus.Failed;
        }
        else
        {
            task.Retries++;
            task.Status = SwarmStatus.Todo;
        }
        Save();
        return task;
    }

    /// <summary>
    /// One decision. The first interrupt returns the task to todo with a narrower brief.
    /// The next interrupt or failure marks it failed and leaves the other tasks alone.
    /// </summary>
    public SwarmTask Interrupt(string taskId, string? reason)
    {
        var task = TaskById(taskId);
        if (task.Status != SwarmStatus.Doing) return task;
        var why = ClipReason(reason);
        if (task.InterruptRetries >= 1)
        {
            task.Status = SwarmStatus.Failed;
            task.Result = why;
            Save();
            return task;
        }
        task.InterruptRetries = 1;
        task.Status = SwarmStatus.Todo;
        task.Assignee = "";
        task.Result = "Interrupted once. " + why;
        var title = string.IsNullOrWhiteSpace(task.Title) ? task.Id : task.Title.Trim();
        var line = "Narrowed after an interrupt: do only \"" + title + "\". Drop side work. Reason: " + why;
        task.Details = string.IsNullOrWhiteSpace(task.Details) ? line : task.Details.Trim() + "\n" + line;
        Save();
        return task;
    }

    /// <summary>
    /// Button path. The first use interrupts a doing task into one narrower retry.
    /// Using it again on that task fails only that task.
    /// </summary>
    public SwarmTask Respawn(string taskId, string? reason)
    {
        var task = TaskById(taskId);
        if (task.Status is SwarmStatus.Done or SwarmStatus.Failed) return task;
        if (task.InterruptRetries >= 1 && task.Status != SwarmStatus.Doing)
        {
            task.Status = SwarmStatus.Failed;
            task.Result = ClipReason(reason);
            Save();
            return task;
        }
        return Interrupt(taskId, reason);
    }

    private static string ClipReason(string? reason)
    {
        var why = string.IsNullOrWhiteSpace(reason) ? "The helper was interrupted." : reason.Trim().Replace("\r", " ").Replace("\n", " ");
        return why.Length <= 240 ? why : why[..240];
    }

    public void Save()
    {
        var path = BoardPath;
        if (File.Exists(path))
            SwarmSchedule.EnsureSameRun(Read(path), this);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, path, true);
    }

    private static SwarmTask Copy(SwarmTask source) => new()
    {
        Id = source.Id.Trim(),
        Title = source.Title.Trim(),
        Role = source.Role.Trim(),
        Status = string.IsNullOrWhiteSpace(source.Status) ? SwarmStatus.Todo : source.Status.Trim(),
        Assignee = source.Assignee ?? "",
        Result = source.Result ?? "",
        Proof = source.Proof ?? "",
        VerifierNote = source.VerifierNote ?? "",
        Details = source.Details ?? "",
        Retries = source.Retries,
        InterruptRetries = source.InterruptRetries,
        Files = source.Files is null ? [] : [.. source.Files],
        After = source.After is null ? [] : [.. source.After]
    };
}
