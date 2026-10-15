using Sovereign.Desktop.Client;
using Xunit;

namespace XaiDesk.Tests;

public sealed class SwarmModelHireTests
{
    private static CursorChatModel M(string id, string name) => new(id, name);

    [Fact]
    public void Catalog_picks_ranked_ids_and_keeps_verifier_distinct()
    {
        CursorChatModel[] models =
        [
            M("grok-4.7", "Grok 4.7"),
            M("composer-2.5", "Composer 2.5"),
            M("opus-5.5", "Claude Opus 5.5"),
            M("gpt-5.6-sol", "GPT-5.6 Sol"),
            M("fable-5.1", "Claude Fable 5.1"),
            M("gemini-3.8-flash", "Gemini 3.8 Flash"),
            M("muse-spark-1.3", "Muse Spark 1.3"),
            M("codex-5.3", "Codex 5.3")
        ];
        var catalog = SwarmModelHire.Catalog(models, "grok-4.7");
        Assert.Contains("engineer modelId: fable-5.1", catalog);
        Assert.Contains("verifier modelId: opus-5.5", catalog);
        Assert.Contains("researcher modelId: gemini-3.8-flash", catalog);
        Assert.Contains("investigator modelId: gemini-3.8-flash", catalog);
        Assert.Contains("scout modelId: grok-4.7", catalog);
        Assert.Contains("engineer, researcher, scout, investigator, and verifier", catalog);
        Assert.Contains("- fable-5.1 | Claude Fable 5.1", catalog);
        Assert.DoesNotContain("grokbot", catalog, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("wake role", catalog, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Claude Fable 5.1 modelId", catalog);

        var exact = SwarmModelHire.Catalog([M("fable-5.1", "Claude Fable 5.1"), M(SwarmRoleModels.ScoutDefaultModelId, "Grok 4.7 Fast")], "fable-5.1");
        Assert.Contains("scout modelId: " + SwarmRoleModels.ScoutDefaultModelId, exact);
    }

    [Fact]
    public void Missing_rank_falls_back_to_the_parent_unless_that_repeats_the_engineer()
    {
        CursorChatModel[] models = [M("fable-5.1", "Claude Fable 5.1")];
        var catalog = SwarmModelHire.Catalog(models, "fable-5.1");
        Assert.Contains("engineer modelId: fable-5.1", catalog);
        Assert.Contains("verifier modelId: (none)", catalog);
        Assert.Contains("researcher modelId: fable-5.1", catalog);
    }

    [Fact]
    public void Card_line_shows_requested_and_reported_when_they_differ()
    {
        var child = new CursorSubagent("h", "Engineer", "Build", "running",
            ModelId: "opus-5.5", ModelName: "Claude Opus 5.5", ModelProvider: "anthropic", ModelSource: "runtime model update",
            RequestedModelId: "fable-5.1", RequestedModelName: "Claude Fable 5.1", RequestedModelProvider: "anthropic");
        var line = SubagentCardText.CardLine(child);
        Assert.Contains("Claude Opus 5.5", line);
        Assert.Contains("requested Claude Fable 5.1", line);
        var same = child with { RequestedModelName = "Claude Opus 5.5", RequestedModelId = "opus-5.5" };
        Assert.DoesNotContain("requested", SubagentCardText.CardLine(same));
    }
}
