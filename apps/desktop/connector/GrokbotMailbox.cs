using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sovereign.Desktop.Connector;

public enum GrokbotMailStatus { Queued, PickedUp, Answered }

public sealed record GrokbotMail(
    string Id,
    string Text,
    bool AskToAct,
    GrokbotMailStatus Status,
    DateTimeOffset Created,
    string? Reply,
    DateTimeOffset? AnsweredAt,
    string? WakeNote = null);

public enum GrokbotLinkState { Off, Waiting, Connected }

public sealed record GrokbotLink(GrokbotLinkState State, DateTimeOffset? LastPoll);

public sealed class GrokbotPreferences
{
    public bool TabEnabled { get; set; } = true;
    public bool AllowControl { get; set; }
    public bool Screenshots { get; set; }
    public bool Stopped { get; set; }
    public string WakeUrl { get; set; } = "";
}

public sealed record GrokbotAuditLine(DateTimeOffset At, string Tool, string Args, string Result);

/// <summary>Mailbox for the Grokbot tab. The connector cannot call Grokbot; Grokbot polls this store.</summary>
public sealed class GrokbotMailbox
{
    public const int MaxMessages = 200;
    public const int MaxAudit = 100;
    public static readonly TimeSpan ConnectedWindow = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string? _directory;
    private readonly object _gate = new();
    private readonly List<GrokbotMail> _messages = [];
    private readonly List<GrokbotAuditLine> _audit = [];
    private GrokbotPreferences _preferences = new();
    private DateTimeOffset? _lastPoll;
    private int _controlling;

    public GrokbotMailbox(string? directory)
    {
        _directory = string.IsNullOrWhiteSpace(directory) ? null : directory;
        Load();
    }

    public static GrokbotMailbox Open(string directory)
    {
        Directory.CreateDirectory(directory);
        return new GrokbotMailbox(directory);
    }

    public IReadOnlyList<GrokbotMail> Messages { get { lock (_gate) return _messages.ToArray(); } }
    public IReadOnlyList<GrokbotAuditLine> Audit { get { lock (_gate) return _audit.ToArray(); } }
    public bool Controlling { get { lock (_gate) return _controlling > 0; } }

    public GrokbotPreferences PreferencesCopy()
    {
        lock (_gate) return Copy(_preferences);
    }

    public void SavePreferences(GrokbotPreferences next)
    {
        ArgumentNullException.ThrowIfNull(next);
        lock (_gate)
        {
            _preferences = Copy(next);
            SaveState();
        }
    }

    public GrokbotMail Queue(string text, bool askToAct)
    {
        var clean = CleanText(text);
        lock (_gate)
        {
            var mail = new GrokbotMail(Guid.NewGuid().ToString("N"), clean, askToAct, GrokbotMailStatus.Queued, DateTimeOffset.UtcNow, null, null);
            _messages.Add(mail);
            while (_messages.Count > MaxMessages) _messages.RemoveAt(0);
            SaveMessages();
            return mail;
        }
    }

    public void NotePoll(DateTimeOffset? at = null)
    {
        lock (_gate)
        {
            _lastPoll = at ?? DateTimeOffset.UtcNow;
            SaveState();
        }
    }

    public GrokbotLink Link(bool listening, DateTimeOffset? now = null)
    {
        lock (_gate)
        {
            if (!listening) return new GrokbotLink(GrokbotLinkState.Off, _lastPoll);
            var clock = now ?? DateTimeOffset.UtcNow;
            var connected = _lastPoll is DateTimeOffset poll && clock >= poll && clock - poll <= ConnectedWindow;
            return new GrokbotLink(connected ? GrokbotLinkState.Connected : GrokbotLinkState.Waiting, _lastPoll);
        }
    }

    public IReadOnlyList<GrokbotMail> ListSince(string? since)
    {
        DateTimeOffset? cutoff = null;
        if (!string.IsNullOrWhiteSpace(since))
        {
            if (!DateTimeOffset.TryParse(since, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                throw new ArgumentException("since must be an ISO-8601 time.");
            cutoff = parsed;
        }
        lock (_gate)
        {
            var picked = new List<GrokbotMail>();
            for (var i = 0; i < _messages.Count; i++)
            {
                var mail = _messages[i];
                if (mail.Status == GrokbotMailStatus.Answered) continue;
                if (cutoff is DateTimeOffset from && mail.Created <= from) continue;
                if (mail.Status == GrokbotMailStatus.Queued)
                    mail = mail with { Status = GrokbotMailStatus.PickedUp };
                _messages[i] = mail;
                picked.Add(mail);
            }
            SaveMessages();
            return picked;
        }
    }

    public GrokbotMail Reply(string messageId, string text)
    {
        if (string.IsNullOrWhiteSpace(messageId)) throw new ArgumentException("message_id is required.");
        var clean = CleanText(text);
        lock (_gate)
        {
            var index = _messages.FindIndex(item => item.Id == messageId);
            if (index < 0) throw new KeyNotFoundException("That inbox message is not in the Grokbot tab.");
            var current = _messages[index];
            if (current.Status == GrokbotMailStatus.Answered)
                throw new InvalidOperationException("That message is already answered.");
            var updated = current with { Status = GrokbotMailStatus.Answered, Reply = clean, AnsweredAt = DateTimeOffset.UtcNow };
            _messages[index] = updated;
            SaveMessages();
            return updated;
        }
    }

    public void NoteWake(string id, string note)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("message id is required.");
        lock (_gate)
        {
            var index = _messages.FindIndex(item => item.Id == id);
            if (index < 0) return;
            _messages[index] = _messages[index] with { WakeNote = note };
            SaveMessages();
        }
    }

    public bool ActionsAllowed()
    {
        lock (_gate)
        {
            if (_preferences.Stopped) return false;
            if (_preferences.AllowControl) return true;
            return _messages.Count > 0 && _messages[^1].AskToAct;
        }
    }

    public void EnsureAction(string tool)
    {
        _ = tool;
        if (!ActionsAllowed())
            throw new InvalidOperationException("Grokbot control is off. Turn on Ask Grokbot to act for the latest message, or Allow Grokbot control. Stop Grokbot blocks every action.");
    }

    public void EnsureScreenshot()
    {
        EnsureAction("app_screenshot");
        lock (_gate)
        {
            if (!_preferences.Screenshots)
                throw new InvalidOperationException("Screenshots are off.");
        }
    }

    public void BeginControl() { lock (_gate) _controlling++; }

    public void EndControl() { lock (_gate) if (_controlling > 0) _controlling--; }

    public void Record(string tool, string args, string result)
    {
        lock (_gate)
        {
            _audit.Add(new GrokbotAuditLine(DateTimeOffset.UtcNow, Clip(tool, 40), Clip(args, 120), Clip(result, 160)));
            while (_audit.Count > MaxAudit) _audit.RemoveAt(0);
            SaveAudit();
        }
    }

    public static string CleanText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Message is empty.");
        var trimmed = text.Trim().Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (trimmed.Length > 8000) throw new ArgumentException("Message is too long.");
        foreach (var ch in trimmed)
            if (char.IsControl(ch) && ch is not '\n' and not '\t')
                throw new ArgumentException("Message has unsupported characters.");
        return trimmed;
    }

    private void Load()
    {
        if (_directory is null) return;
        LoadState();
        LoadLines(Path.Combine(_directory, "thread.jsonl"), line =>
        {
            var saved = JsonSerializer.Deserialize<MailFile>(line, Json);
            if (saved is null || string.IsNullOrWhiteSpace(saved.Id)) return;
            _messages.Add(new GrokbotMail(saved.Id, saved.Text ?? "", saved.AskToAct, saved.Status, saved.Created, saved.Reply, saved.AnsweredAt, saved.WakeNote));
        });
        while (_messages.Count > MaxMessages) _messages.RemoveAt(0);
        LoadLines(Path.Combine(_directory, "audit.jsonl"), line =>
        {
            var entry = JsonSerializer.Deserialize<GrokbotAuditLine>(line, Json);
            if (entry is not null) _audit.Add(entry);
        });
        while (_audit.Count > MaxAudit) _audit.RemoveAt(0);
    }

    private void LoadState()
    {
        var path = Path.Combine(_directory!, "state.json");
        if (!File.Exists(path)) return;
        try
        {
            var saved = JsonSerializer.Deserialize<StateFile>(File.ReadAllText(path), Json);
            if (saved is null) return;
            if (saved.TabEnabled is bool tab) _preferences.TabEnabled = tab;
            if (saved.AllowControl is bool allow) _preferences.AllowControl = allow;
            if (saved.Screenshots is bool shots) _preferences.Screenshots = shots;
            if (saved.Stopped is bool stopped) _preferences.Stopped = stopped;
            if (saved.WakeUrl is string wake) _preferences.WakeUrl = wake;
            _lastPoll = saved.LastPoll;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
    }

    private static void LoadLines(string path, Action<string> take)
    {
        if (!File.Exists(path)) return;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { take(line); }
                catch (JsonException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void SaveMessages() => SaveLines("thread.jsonl", _messages.Select(item => JsonSerializer.Serialize(item, Json)));
    private void SaveAudit() => SaveLines("audit.jsonl", _audit.Select(item => JsonSerializer.Serialize(item, Json)));

    private void SaveState()
    {
        if (_directory is null) return;
        var state = new StateFile
        {
            TabEnabled = _preferences.TabEnabled,
            AllowControl = _preferences.AllowControl,
            Screenshots = _preferences.Screenshots,
            Stopped = _preferences.Stopped,
            WakeUrl = _preferences.WakeUrl ?? "",
            LastPoll = _lastPoll
        };
        WriteAtomic(Path.Combine(_directory, "state.json"), JsonSerializer.Serialize(state, Json));
    }

    private void SaveLines(string name, IEnumerable<string> lines)
    {
        if (_directory is null) return;
        var rows = lines.ToList();
        WriteAtomic(Path.Combine(_directory, name), rows.Count == 0 ? "" : string.Join('\n', rows) + "\n");
    }

    private static void WriteAtomic(string path, string text)
    {
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, true);
    }

    private static GrokbotPreferences Copy(GrokbotPreferences source) => new()
    {
        TabEnabled = source.TabEnabled,
        AllowControl = source.AllowControl,
        Screenshots = source.Screenshots,
        Stopped = source.Stopped,
        WakeUrl = source.WakeUrl ?? ""
    };

    private static string Clip(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        var text = value.Trim().Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        return text.Length <= max ? text : text[..max];
    }

    private sealed class StateFile
    {
        public bool? TabEnabled { get; set; }
        public bool? AllowControl { get; set; }
        public bool? Screenshots { get; set; }
        public bool? Stopped { get; set; }
        public string? WakeUrl { get; set; }
        public DateTimeOffset? LastPoll { get; set; }
    }

    private sealed class MailFile
    {
        public string? Id { get; set; }
        public string? Text { get; set; }
        public bool AskToAct { get; set; }
        public GrokbotMailStatus Status { get; set; }
        public DateTimeOffset Created { get; set; }
        public string? Reply { get; set; }
        public DateTimeOffset? AnsweredAt { get; set; }
        public string? WakeNote { get; set; }
    }
}
