using Sovereign.Desktop.Client;

var failed = 0;
void Check(bool condition, string name)
{
    if (condition) { Console.WriteLine("pass  " + name); return; }
    failed++;
    Console.WriteLine("FAIL  " + name);
}

var root = Path.Combine(Path.GetTempPath(), "sovereign-skills-" + Guid.NewGuid().ToString("N"));
try
{
    var embedder = new KeywordEmbedder();
    var library = new SkillLibrary(Path.Combine(root, "on"), embedder, enabled: true, capacity: 2);
    Check(SkillLibrary.PassesBar(Run("short", "alpha task", 2, 0, true)) && library.Consider(Run("short", "alpha task", 2, 0, true)) == SkillOutcome.Saved,
        "a short successful run clears the save bar");
    Check(library.Consider(Run("errors", "beta task", 2, 2, true)) == SkillOutcome.RejectedQuality, "tool errors miss the save bar");
    Check(library.Consider(Run("long", "gamma task", 20, 0, true)) == SkillOutcome.RejectedQuality, "a long run misses the save bar");
    Check(library.Consider(Run("miss", "beta task", 1, 0, false)) == SkillOutcome.RejectedQuality, "an unmet task misses the save bar");
    Check(library.Cards.Count == 1 && library.Cards[0].Tools.Contains("read") && library.Cards[0].Score > 0.9, "the saved card keeps steps, tools, and score");

    Check(library.Consider(Run("dup", "near-alpha task", 1, 0, true)) == SkillOutcome.RejectedDuplicate && library.Cards.Count == 1,
        "similarity above 0.90 rejects a near-duplicate");
    Check(library.Consider(Run("other", "beta task", 6, 0, true)) == SkillOutcome.Saved && library.Cards.Count == 2, "a distinct success is saved");
    var beta = library.Cards.Single(card => card.WhenToUse.Contains("beta", StringComparison.Ordinal));
    library.Pin(beta.Id, true);
    Check(library.Consider(Run("third", "gamma task", 1, 0, true)) == SkillOutcome.Saved, "a full library saves after pruning");
    Check(library.Cards.Any(card => card.Id == beta.Id && card.Pinned) && library.Cards.Count == 2 && !library.Cards.Any(card => card.WhenToUse.Contains("alpha", StringComparison.Ordinal)),
        "prune drops the lowest unpinned card and keeps the pin");
    Check(library.ReadAudit().Contains("prune ", StringComparison.Ordinal) && library.ReadAudit().Contains(beta.Id, StringComparison.Ordinal) == false || library.ReadAudit().Contains("pin ", StringComparison.Ordinal),
        "prune is written to the local audit log");
    Check(library.ReadAudit().Contains("prune ", StringComparison.Ordinal), "the audit log records the dropped card");

    var retrieval = library.Retrieve("gamma task");
    Check(retrieval.Injection.StartsWith("Past approaches that worked", StringComparison.Ordinal)
        && retrieval.Injection.Contains("gamma", StringComparison.Ordinal)
        && !retrieval.Injection.Contains("beta", StringComparison.Ordinal)
        && retrieval.Caption.StartsWith("Skills used:", StringComparison.Ordinal),
        "retrieval injects only cards at or above the match floor");
    Check(library.Cards.Single(card => card.WhenToUse.Contains("gamma", StringComparison.Ordinal)).UseCount == 1, "a used card increments its use count");

    var crowded = new SkillLibrary(Path.Combine(root, "crowd"), new AngleEmbedder(), enabled: true, capacity: 4);
    foreach (var name in new[] { "m1", "m2", "m3", "m4" })
        Check(crowded.Consider(Run(name, name + " approach", name == "m4" ? 4 : 1, 0, true)) == SkillOutcome.Saved, "saved " + name);
    var top = crowded.Retrieve("query approach");
    Check(top.Cards.Length == 3 && top.Cards.All(card => card.Name != "m4") && top.Injection.Contains("Skill: m1", StringComparison.Ordinal),
        "retrieval injects the top three matching cards");

    var exported = library.ExportJson();
    var restored = new SkillLibrary(Path.Combine(root, "restored"), embedder, enabled: true, capacity: 2);
    restored.ImportJson(exported);
    Check(restored.Cards.Count == library.Cards.Count
        && restored.Cards.All(card => library.Cards.Any(original => original.Id == card.Id && original.Pinned == card.Pinned && original.Steps == card.Steps && original.Score == card.Score && original.Vector.Length == 384)),
        "export and import round trip");

    var wide = new SkillLibrary(Path.Combine(root, "wide"), new WideEmbedder(), enabled: true, capacity: 50);
    Check(wide.Consider(Run("wide", "alpha task", 1, 0, true)) == SkillOutcome.RejectedEmbed && wide.Cards.Count == 0,
        "a 1024-d vector is refused");

    var off = new SkillLibrary(Path.Combine(root, "off"), new BoomEmbedder(), enabled: false, capacity: 50);
    var before = off.ReadAudit();
    Check(off.Consider(Run("off", "alpha task", 1, 0, true)) == SkillOutcome.Disabled && off.Retrieve("alpha task").Injection.Length == 0 && off.Cards.Count == 0 && off.ReadAudit() == before,
        "toggle off leaves the library unchanged");
}
finally { if (Directory.Exists(root)) Directory.Delete(root, true); }

return failed;

static SkillRun Run(string id, string task, int steps, int errors, bool met) => new(
    id, id, task, met ? "completed" : "failed", steps, errors, met, task,
    "1. Read the file\n2. Report the count", ["read"], DateTimeOffset.Parse("2026-10-09T12:00:00Z"));

sealed class KeywordEmbedder : ISkillEmbedder
{
    public float[]? Embed(string text)
    {
        var vector = new float[384];
        if (text.Contains("near-alpha", StringComparison.Ordinal)) { vector[0] = 0.96f; vector[3] = 0.28f; }
        else if (text.Contains("alpha", StringComparison.Ordinal)) vector[0] = 1;
        else if (text.Contains("beta", StringComparison.Ordinal)) vector[1] = 1;
        else if (text.Contains("gamma", StringComparison.Ordinal)) vector[2] = 1;
        else return null;
        return vector;
    }
}

sealed class AngleEmbedder : ISkillEmbedder
{
    public float[]? Embed(string text)
    {
        var vector = new float[384];
        var slot = text.Contains("m1", StringComparison.Ordinal) ? 1
            : text.Contains("m2", StringComparison.Ordinal) ? 2
            : text.Contains("m3", StringComparison.Ordinal) ? 3
            : text.Contains("m4", StringComparison.Ordinal) ? 4
            : 0;
        vector[0] = 0.85f;
        if (slot > 0) vector[slot] = MathF.Sqrt(1f - 0.85f * 0.85f);
        else vector[0] = 1;
        return vector;
    }
}

sealed class WideEmbedder : ISkillEmbedder
{
    public float[]? Embed(string text) => new float[1024];
}

sealed class BoomEmbedder : ISkillEmbedder
{
    public float[]? Embed(string text) => throw new InvalidOperationException("embedder must not run while the library is off");
}
