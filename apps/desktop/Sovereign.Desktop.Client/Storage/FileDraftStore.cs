using System.Text.Json;
using Sovereign.Desktop.Core;
using Sovereign.Desktop.Core.Drafts;

namespace Sovereign.Desktop.Client.Storage;

public sealed class FileDraftStore : IDraftStore
{
    private readonly string _root;
    private readonly IClock _clock;
    private readonly object _gate = new();

    public FileDraftStore(string profileDirectory, IClock? clock = null)
    {
        _root = Path.Combine(profileDirectory, "drafts");
        Directory.CreateDirectory(_root);
        _clock = clock ?? new SystemClock();
    }

    public Task<MandateDraft> SaveMandateAsync(MandateDraft draft, int? expectedRevision, CancellationToken cancellationToken = default) =>
        Task.FromResult(Save("mandate", draft.Id, expectedRevision, draft, next => draft with
        {
            Revision = next,
            SavedAt = _clock.UtcNow,
            Activation = ActivationStatus.DraftNotActivated,
            SchemaVersion = MandateDraft.CurrentSchema
        }));

    public Task<MandateDraft?> LoadMandateAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Load<MandateDraft>("mandate", id));

    public Task<IReadOnlyList<MandateDraft>> ListMandatesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(List<MandateDraft>("mandate"));

    public Task<SessionDraft> SaveSessionAsync(SessionDraft draft, int? expectedRevision, CancellationToken cancellationToken = default) =>
        Task.FromResult(Save("session", draft.Id, expectedRevision, draft, next => draft with
        {
            Revision = next,
            SavedAt = _clock.UtcNow,
            Activation = ActivationStatus.DraftNotActivated,
            SchemaVersion = SessionDraft.CurrentSchema
        }));

    public Task<IReadOnlyList<SessionDraft>> ListSessionsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(List<SessionDraft>("session"));

    private T Save<T>(string prefix, string id, int? expectedRevision, T incoming, Func<int, T> stamped)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Draft id is required.", nameof(id));
        }

        lock (_gate)
        {
            try
            {
                var path = PathFor(prefix, id);
                var current = File.Exists(path) ? JsonSerializer.Deserialize<RevisionEnvelope>(File.ReadAllText(path), Options) : null;
                var currentRev = current?.Revision ?? 0;
                if (expectedRevision is int exp && exp != currentRev)
                {
                    throw new ConcurrentDraftException($"Expected revision {exp} but disk has {currentRev}.");
                }

                var next = currentRev + 1;
                var payload = stamped(next);
                var json = JsonSerializer.Serialize(payload, Options);
                var tmp = path + ".tmp";
                var bak = path + ".bak";
                File.WriteAllText(tmp, json);
                if (File.Exists(path))
                {
                    File.Replace(tmp, path, bak, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tmp, path);
                }

                return payload;
            }
            catch (ConcurrentDraftException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new DraftIOException("Draft save failed without replacing the last valid file.", ex);
            }
        }
    }

    private T? Load<T>(string prefix, string id)
    {
        var path = PathFor(prefix, id);
        if (!File.Exists(path))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
    }

    private IReadOnlyList<T> List<T>(string prefix)
    {
        var files = Directory.GetFiles(_root, prefix + "-*.json");
        return files.Select(f => JsonSerializer.Deserialize<T>(File.ReadAllText(f), Options)!).Where(x => x is not null).ToArray();
    }

    private string PathFor(string prefix, string id)
    {
        foreach (var c in id)
        {
            if (!char.IsLetterOrDigit(c) && c is not '-' and not '_')
            {
                throw new ArgumentException("Draft id must be alphanumeric, hyphen or underscore.");
            }
        }

        return Path.Combine(_root, $"{prefix}-{id}.json");
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private sealed class RevisionEnvelope
    {
        public int Revision { get; set; }
    }
}
