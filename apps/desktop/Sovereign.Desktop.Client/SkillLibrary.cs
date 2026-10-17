using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sovereign.Desktop.Client;

/// <summary>Save and prune rules for subagent skills. No training. Similarity is 384-d cosine.</summary>
public interface ISkillEmbedder
{
    /// <summary>Return a 384-d vector from the live Chroma embedding, or null when that embedding is unavailable.</summary>
    float[]? Embed(string text);
}

public enum SkillOutcome
{
    Disabled,
    Saved,
    RejectedQuality,
    RejectedDuplicate,
    RejectedEmbed,
    LibraryFull
}

public sealed record SkillRun(
    string RunId,
    string Name,
    string Task,
    string Outcome,
    int Steps,
    int ToolErrors,
    bool TaskMet,
    string WhenToUse,
    string StepsThatWorked,
    string[] Tools,
    DateTimeOffset At);

public sealed record SkillCard(
    string Id,
    string Name,
    string WhenToUse,
    string Steps,
    string[] Tools,
    string SourceRunId,
    DateTimeOffset SavedAt,
    double Score,
    int UseCount,
    bool Pinned,
    float[] Vector);

public sealed record SkillRetrieval(SkillCard[] Cards, string Injection, string Caption);

public sealed class SkillLibrary
{
    public const int DefaultCapacity = 50;
    public const int Dimensions = 384;
    public const double NoveltyCeiling = 0.90;
    public const double RetrievalFloor = 0.80;
    public const double SimilarityWeight = 0.9;
    public const double QualityBar = 0.60;
    public const string EmbedModel = "BAAI/bge-small-en-v1.5";

    private readonly string _directory;
    private readonly ISkillEmbedder _embedder;
    private readonly List<SkillCard> _cards = [];
    private readonly object _gate = new();
    // Saves run one model process at a time. Readers never wait on it; _gate is not held while embedding.
    private readonly object _embedGate = new();
    private bool _enabled;
    private int _capacity;

    public SkillLibrary(string directory, ISkillEmbedder embedder, bool enabled = false, int capacity = DefaultCapacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _directory = directory;
        _embedder = embedder;
        _enabled = enabled;
        _capacity = capacity;
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            lock (_gate)
            {
                if (_enabled == value) return;
                _enabled = value;
                SaveSettings();
            }
        }
    }

    public int Capacity => _capacity;
    public IReadOnlyList<SkillCard> Cards
    {
        get { lock (_gate) return _cards.ToArray(); }
    }

    public string AuditPath => Path.Combine(_directory, "audit.log");

    public static SkillLibrary Open(string profile, ISkillEmbedder embedder)
    {
        var directory = Path.Combine(profile, "skill-library");
        var library = new SkillLibrary(directory, embedder);
        library.Load();
        return library;
    }

    public static double Score(int steps, int toolErrors, bool taskMet)
    {
        if (!taskMet || steps < 0 || toolErrors < 0) return 0;
        var value = 1 - 0.04 * Math.Max(0, steps - 1) - 0.20 * toolErrors;
        return Math.Clamp(value, 0, 1);
    }

    public static bool PassesBar(SkillRun run) =>
        run.TaskMet && run.Outcome == "completed" && run.ToolErrors == 0 && run.Steps >= 0 && Score(run.Steps, run.ToolErrors, true) >= QualityBar;

    public static double Combined(SkillCard card) => card.Score * (1 + Math.Max(0, card.UseCount));

    public static string ApplyGuidance(string text, string? guidance) =>
        string.IsNullOrWhiteSpace(guidance) ? text : text + "\n\n" + guidance.Trim();

    public SkillOutcome Consider(SkillRun run)
    {
        lock (_gate)
        {
            if (!_enabled) return SkillOutcome.Disabled;
            if (!PassesBar(run))
            {
                Log("reject-quality", "run=" + Clean(run.RunId) + " steps=" + run.Steps.ToString(CultureInfo.InvariantCulture) + " errors=" + run.ToolErrors.ToString(CultureInfo.InvariantCulture));
                return SkillOutcome.RejectedQuality;
            }
        }
        // The embedder can start a slow model process. Holding _gate here would stall every reader, including the UI.
        float[]? vector;
        lock (_embedGate) vector = Accept(_embedder.Embed(EmbedText(run)));
        lock (_gate)
        {
            if (!_enabled) return SkillOutcome.Disabled;
            if (vector is null)
            {
                Log("reject-embed", "run=" + Clean(run.RunId) + " model=" + EmbedModel);
                return SkillOutcome.RejectedEmbed;
            }
            var nearest = Nearest(vector, null);
            if (nearest.Similarity > NoveltyCeiling)
            {
                Log("reject-duplicate", "run=" + Clean(run.RunId) + " similarity=" + nearest.Similarity.ToString("0.000", CultureInfo.InvariantCulture) + " existing=" + nearest.Id);
                return SkillOutcome.RejectedDuplicate;
            }
            if (_cards.Count >= _capacity && !TryPrune())
            {
                Log("reject-full", "run=" + Clean(run.RunId) + " pinned=" + _cards.Count(card => card.Pinned).ToString(CultureInfo.InvariantCulture));
                return SkillOutcome.LibraryFull;
            }
            var card = new SkillCard(Guid.NewGuid().ToString("N"), Clip(run.Name, 80), Clip(run.WhenToUse, 500),
                Clip(run.StepsThatWorked, 2000), run.Tools.Where(tool => !string.IsNullOrWhiteSpace(tool)).Select(tool => Clip(tool, 80)).Distinct(StringComparer.Ordinal).Take(12).ToArray(),
                Clean(run.RunId), run.At, Score(run.Steps, run.ToolErrors, true), 0, false, vector);
            _cards.Add(card);
            Log("save", "id=" + card.Id + " name=" + Clean(card.Name) + " score=" + card.Score.ToString("0.000", CultureInfo.InvariantCulture) + " run=" + card.SourceRunId);
            Persist();
            return SkillOutcome.Saved;
        }
    }

    public SkillRetrieval Retrieve(string query)
    {
        lock (_gate)
        {
            if (!_enabled || _cards.Count == 0) return new([], "", "");
        }
        var vector = Accept(_embedder.Embed(query));
        if (vector is null) return new([], "", "");
        lock (_gate)
        {
            if (!_enabled || _cards.Count == 0) return new([], "", "");
            var ranked = _cards.Select(card => (Card: card, Rank: SimilarityWeight * Cosine(vector, card.Vector) + (1 - SimilarityWeight) * card.Score))
                .Where(item => item.Rank >= RetrievalFloor)
                .OrderByDescending(item => item.Rank)
                .ThenByDescending(item => item.Card.Score)
                .ThenBy(item => item.Card.Name, StringComparer.Ordinal)
                .Take(3)
                .Select(item => item.Card)
                .ToArray();
            if (ranked.Length == 0) return new([], "", "");
            for (var i = 0; i < _cards.Count; i++)
                if (ranked.Any(card => card.Id == _cards[i].Id))
                    _cards[i] = _cards[i] with { UseCount = _cards[i].UseCount + 1 };
            Log("use", "ids=" + string.Join(",", ranked.Select(card => card.Id)));
            Persist();
            var used = ranked.Select(card => _cards.First(item => item.Id == card.Id)).ToArray();
            return new(used, Injection(used), "Skills used: " + string.Join(", ", used.Select(card => card.Name)));
        }
    }

    public void Pin(string id, bool pinned)
    {
        lock (_gate)
        {
            var index = _cards.FindIndex(card => card.Id == id);
            if (index < 0) return;
            _cards[index] = _cards[index] with { Pinned = pinned };
            Log("pin", "id=" + id + " pinned=" + (pinned ? "true" : "false"));
            Persist();
        }
    }

    public bool Update(string id, string name, string whenToUse, string steps, string[] tools)
    {
        lock (_gate)
        {
            var index = _cards.FindIndex(card => card.Id == id);
            if (index < 0) return false;
            var vector = Accept(_embedder.Embed(whenToUse + "\n" + steps));
            if (vector is null) return false;
            if (Nearest(vector, id).Similarity > NoveltyCeiling) return false;
            _cards[index] = _cards[index] with
            {
                Name = Clip(name, 80),
                WhenToUse = Clip(whenToUse, 500),
                Steps = Clip(steps, 2000),
                Tools = tools.Where(tool => !string.IsNullOrWhiteSpace(tool)).Select(tool => Clip(tool, 80)).Distinct(StringComparer.Ordinal).Take(12).ToArray(),
                Vector = vector
            };
            Log("edit", "id=" + id);
            Persist();
            return true;
        }
    }

    public void Delete(string id)
    {
        lock (_gate)
        {
            var removed = _cards.RemoveAll(card => card.Id == id);
            if (removed == 0) return;
            Log("delete", "id=" + id);
            Persist();
        }
    }

    public string ExportJson()
    {
        lock (_gate) return JsonSerializer.Serialize(Snapshot(), JsonOptions());
    }

    public void ImportJson(string json)
    {
        lock (_gate)
        {
            var body = JsonSerializer.Deserialize<SkillLibraryFile>(json, JsonOptions()) ?? throw new JsonException("Empty skill library.");
            if (body.Version != 1) throw new JsonException("Unrecognized skill library.");
            var cards = new List<SkillCard>();
            foreach (var card in body.Cards ?? [])
            {
                var vector = Accept(card.Vector);
                if (vector is null || string.IsNullOrWhiteSpace(card.Id) || !Guid.TryParseExact(card.Id, "N", out _)) continue;
                cards.RemoveAll(item => item.Id == card.Id);
                cards.Add(card with { Vector = vector, Name = Clip(card.Name ?? "", 80), WhenToUse = Clip(card.WhenToUse ?? "", 500), Steps = Clip(card.Steps ?? "", 2000), Tools = card.Tools ?? [] });
            }
            _cards.Clear();
            _cards.AddRange(cards);
            while (_cards.Count > _capacity && TryPrune()) { }
            Log("import", "count=" + _cards.Count.ToString(CultureInfo.InvariantCulture));
            Persist();
        }
    }

    public string ReadAudit()
    {
        lock (_gate) return File.Exists(AuditPath) ? File.ReadAllText(AuditPath) : "";
    }

    private bool TryPrune()
    {
        var victim = _cards.Where(card => !card.Pinned).OrderBy(Combined).ThenBy(card => card.SavedAt).FirstOrDefault();
        if (victim is null) return false;
        _cards.Remove(victim);
        Log("prune", "id=" + victim.Id + " name=" + Clean(victim.Name) + " combined=" + Combined(victim).ToString("0.000", CultureInfo.InvariantCulture) + " score=" + victim.Score.ToString("0.000", CultureInfo.InvariantCulture) + " uses=" + victim.UseCount.ToString(CultureInfo.InvariantCulture));
        return true;
    }

    private (double Similarity, string Id) Nearest(float[] vector, string? exceptId)
    {
        var best = 0.0;
        var id = "";
        foreach (var card in _cards)
        {
            if (card.Id == exceptId) continue;
            var similarity = Cosine(vector, card.Vector);
            if (similarity > best) { best = similarity; id = card.Id; }
        }
        return (best, id);
    }

    internal static float[]? Accept(float[]? vector)
    {
        if (vector is null || vector.Length != Dimensions) return null;
        if (vector.Any(value => float.IsNaN(value) || float.IsInfinity(value))) return null;
        var sum = vector.Sum(value => value * value);
        if (sum <= 0) return null;
        var scale = 1f / MathF.Sqrt(sum);
        return vector.Select(value => value * scale).ToArray();
    }

    internal static double Cosine(float[] left, float[] right)
    {
        if (left.Length != right.Length || left.Length == 0) return 0;
        double dot = 0;
        for (var i = 0; i < left.Length; i++) dot += left[i] * right[i];
        return dot;
    }

    private static string Injection(IReadOnlyList<SkillCard> cards)
    {
        var text = new StringBuilder();
        text.Append("Past approaches that worked\nWhen you start a helper, paste the matching cards below into that helper's task. They are prior approaches, not new scope.");
        foreach (var card in cards)
        {
            text.Append("\n\nSkill: ").Append(card.Name);
            text.Append("\nWhen: ").Append(card.WhenToUse);
            text.Append("\nSteps: ").Append(card.Steps);
            text.Append("\nTools: ").Append(card.Tools.Length == 0 ? "none" : string.Join(", ", card.Tools));
        }
        return text.ToString();
    }

    private static string EmbedText(SkillRun run) => run.WhenToUse + "\n" + run.StepsThatWorked;

    private void Load()
    {
        try
        {
            var settingsPath = Path.Combine(_directory, "settings.json");
            if (File.Exists(settingsPath))
            {
                var settings = JsonSerializer.Deserialize<SkillLibrarySettingsFile>(File.ReadAllText(settingsPath), JsonOptions());
                _enabled = settings?.Enabled == true;
                if (settings?.Capacity is >= 1) _capacity = settings.Capacity;
            }
            var libraryPath = Path.Combine(_directory, "library.json");
            if (!File.Exists(libraryPath)) return;
            var body = JsonSerializer.Deserialize<SkillLibraryFile>(File.ReadAllText(libraryPath), JsonOptions());
            foreach (var card in body?.Cards ?? [])
            {
                var vector = Accept(card.Vector);
                if (vector is null || !Guid.TryParseExact(card.Id, "N", out _)) continue;
                _cards.Add(card with { Vector = vector });
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _cards.Clear();
            _enabled = false;
        }
    }

    private void Persist()
    {
        Directory.CreateDirectory(_directory);
        WriteAtomic(Path.Combine(_directory, "library.json"), JsonSerializer.Serialize(Snapshot(), JsonOptions()));
        SaveSettings();
    }

    private void SaveSettings()
    {
        Directory.CreateDirectory(_directory);
        WriteAtomic(Path.Combine(_directory, "settings.json"), JsonSerializer.Serialize(new SkillLibrarySettingsFile(_enabled, _capacity), JsonOptions()));
    }

    private SkillLibraryFile Snapshot() => new(1, _capacity, _cards.ToArray());

    private void Log(string kind, string detail)
    {
        Directory.CreateDirectory(_directory);
        File.AppendAllText(AuditPath, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " + kind + " " + detail + "\n");
    }

    private static void WriteAtomic(string path, string json)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, json);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static JsonSerializerOptions JsonOptions() => new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private static string Clip(string text, int max)
    {
        var value = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        return value.Length <= max ? value : value[..max];
    }

    private static string Clean(string text) => text.Replace('\r', ' ').Replace('\n', ' ').Trim();

}

public sealed record SkillLibrarySettingsFile(bool Enabled, int Capacity);
public sealed record SkillLibraryFile(int Version, int Capacity, SkillCard[]? Cards);
