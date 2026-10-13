using System.Text.Json;
using Sovereign.Desktop.Client;
using Xunit;

namespace XaiDesk.Tests;

public sealed class SwarmRoleModelsTests
{
    private static CursorChatModel M(string id, string name) => new(id, name);

    private static SwarmRoleModels.Loaded Defaults() =>
        SwarmRoleModels.Load(Path.Combine(Path.GetTempPath(), "swarm-role-models-missing-" + Guid.NewGuid().ToString("N")));

    [Fact]
    public void Highest_advertised_rank_wins_and_a_missing_higher_rank_is_skipped()
    {
        CursorChatModel[] models =
        [
            M("gpt-5.6-sol", "GPT-5.6 Sol"),
            M("claude-opus-5-5[context=300k]", "Claude Opus 5.5")
        ];
        var picked = SwarmRoleModels.Pick(models, Defaults().Roles["engineer"], "chat-model");
        Assert.Equal("claude-opus-5-5[context=300k]", picked);
    }

    [Fact]
    public void Pick_returns_the_chat_model_when_no_rank_matches()
    {
        var picked = SwarmRoleModels.Pick(
            [M("grok-4.7", "Grok 4.7")],
            ["Fable", "Opus 5.5", "GPT-5.6 Sol"],
            "grok-4.7");
        Assert.Equal("grok-4.7", picked);

        var shortToken = SwarmRoleModels.Pick([M("sol-1", "Sol")], ["Sol"], "chat-model");
        Assert.Equal("chat-model", shortToken);
    }

    [Fact]
    public void Advertised_override_wins_over_the_ranking()
    {
        CursorChatModel[] models = [M("fable-5", "Fable"), M("opus-5.5", "Opus 5.5")];
        var picked = SwarmRoleModels.Pick(models, ["Fable", "Opus 5.5"], "chat-model", "opus-5.5");
        Assert.Equal("opus-5.5", picked);
    }

    [Fact]
    public void Override_absent_from_the_advertised_list_falls_through_to_the_ranking()
    {
        CursorChatModel[] models = [M("fable-5", "Fable"), M("opus-5.5", "Opus 5.5")];
        var picked = SwarmRoleModels.Pick(models, ["Fable", "Opus 5.5"], "chat-model", "missing-id");
        Assert.Equal("fable-5", picked);
    }

    [Fact]
    public void Opus_5_5_matches_a_compact_model_id()
    {
        var picked = SwarmRoleModels.Pick(
            [M("claude-opus-5-5[context=300k]", "Anthropic")],
            ["Opus 5.5"],
            "chat-model");
        Assert.Equal("claude-opus-5-5[context=300k]", picked);
    }

    [Fact]
    public void Catalog_verifier_differs_from_engineer_or_is_none()
    {
        var defaults = Defaults();
        var both = SwarmRoleModels.Catalog(
            [
                M("claude-fable-5[context=1m]", "Claude Fable 5"),
                M("claude-opus-5-5[context=300k]", "Claude Opus 5.5")
            ],
            "parent-model",
            defaults);
        Assert.Contains("engineer modelId: claude-fable-5[context=1m]", both);
        Assert.Contains("verifier modelId: claude-opus-5-5[context=300k]", both);
        Assert.Contains("- claude-fable-5[context=1m] | Claude Fable 5", both);
        Assert.DoesNotContain("verifier modelId: claude-fable-5[context=1m]", both);

        var onlyId = "claude-fable-5[context=1m]";
        var only = SwarmRoleModels.Catalog([M(onlyId, "Claude Fable 5")], onlyId, defaults);
        Assert.Contains("engineer modelId: " + onlyId, only);
        Assert.Contains("coder modelId: " + onlyId, only);
        Assert.Contains("verifier modelId: (none)", only);
        Assert.Contains("reviewer modelId: (none)", only);
    }

    [Fact]
    public void Catalog_says_grokbot_roles_take_no_model_id()
    {
        var roles = Defaults().Roles;
        var catalog = SwarmRoleModels.Catalog([M("fable-5", "Fable")], "parent-model", Defaults());
        Assert.Contains("grokbot-research and grokbot-ops take no modelId", catalog);
        Assert.Contains("The verifier modelId must differ from the engineer modelId", catalog);
        Assert.Contains("fall back to the parent modelId", catalog);
        Assert.Null(SwarmRoleModels.ForRole([], roles, "grokbot-research", "parent-model", "fable-5"));
        Assert.Null(SwarmRoleModels.ForRole([], roles, "grokbot-ops", "parent-model", null));
        Assert.Null(SwarmRoleModels.ForRole([], roles, "Grokbot-Research", "parent-model", null));
    }

    [Fact]
    public void Catalog_prints_an_override_only_when_it_is_advertised()
    {
        var directory = NewDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, SwarmRoleModels.FileName), """
            {
              "roles": { "engineer": ["Fable", "Opus 5.5"] },
              "overrides": {
                "chat/used": "opus-5.5",
                "chat/skipped": "not-a-model"
              }
            }
            """);
            var catalog = SwarmRoleModels.Catalog(
                [M("fable-5", "Fable"), M("opus-5.5", "Opus 5.5")],
                "parent-model",
                SwarmRoleModels.Load(directory));
            Assert.Contains("engineer modelId: fable-5", catalog);
            Assert.Contains("override chat/used modelId: opus-5.5", catalog);
            Assert.DoesNotContain("not-a-model", catalog);
            Assert.DoesNotContain("chat/skipped", catalog);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Verifier_advertised_override_may_equal_the_engineer()
    {
        CursorChatModel[] models = [M("fable-5", "Fable"), M("opus-5.5", "Opus 5.5")];
        var roles = Defaults().Roles;
        Assert.Equal("fable-5", SwarmRoleModels.ForRole(models, roles, "engineer", "chat-model", null));
        Assert.Equal("opus-5.5", SwarmRoleModels.ForRole(models, roles, "verifier", "chat-model", null));
        Assert.Equal("fable-5", SwarmRoleModels.ForRole(models, roles, "verifier", "chat-model", "fable-5"));
        Assert.Equal("fable-5", SwarmRoleModels.ForRole(models, roles, "reviewer", "chat-model", "fable-5"));
    }

    [Fact]
    public void Missing_role_uses_the_named_fallback()
    {
        CursorChatModel[] models =
        [
            M("opus-5.5", "Opus 5.5"),
            M("gemini-3.8", "Gemini 3.8"),
            M("composer-2.5", "Composer 2.5")
        ];
        var roles = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["engineer"] = ["Opus 5.5"],
            ["verifier"] = ["Composer 2.5", "Opus 5.5"],
            ["investigator"] = ["Gemini 3.8"]
        };
        Assert.Equal("opus-5.5", SwarmRoleModels.ForRole(models, roles, "Engineer", "chat-model", null));
        Assert.Equal("opus-5.5", SwarmRoleModels.ForRole(models, roles, "coder", "chat-model", null));
        Assert.Equal("composer-2.5", SwarmRoleModels.ForRole(models, roles, "reviewer", "chat-model", null));
        Assert.Equal("gemini-3.8", SwarmRoleModels.ForRole(models, roles, "planner", "chat-model", null));
    }

    [Fact]
    public void EnsureDefaults_writes_once_and_leaves_a_hand_edit()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, SwarmRoleModels.FileName);
            Assert.False(File.Exists(path));
            var missing = SwarmRoleModels.Load(directory);
            Assert.False(File.Exists(path));
            Assert.Equal("Fable", missing.Roles["engineer"][0]);
            Assert.Empty(missing.Overrides);

            SwarmRoleModels.EnsureDefaults(directory);
            Assert.Equal(["Fable", "Opus 5.5", "GPT-5.6 Sol", "Codex 5.3"], SwarmRoleModels.Load(directory).Roles["engineer"]);
            Assert.Equal(["Fable", "Opus 5.5", "GPT-5.6 Sol", "Codex 5.3"], SwarmRoleModels.Load(directory).Roles["coder"]);
            Assert.Equal(["GPT-5.6 Sol", "Opus 5.5", "Fable"], SwarmRoleModels.Load(directory).Roles["planner"]);
            Assert.Equal(["Opus 5.5", "GPT-5.6 Sol", "Fable"], SwarmRoleModels.Load(directory).Roles["reviewer"]);
            Assert.Equal(["Opus 5.5", "GPT-5.6 Sol", "Fable"], SwarmRoleModels.Load(directory).Roles["verifier"]);
            Assert.Equal(["Gemini 3.8", "Composer 2.5", "Muse Spark"], SwarmRoleModels.Load(directory).Roles["researcher"]);
            Assert.Equal(["Gemini 3.8", "Composer 2.5", "Muse Spark"], SwarmRoleModels.Load(directory).Roles["investigator"]);
            Assert.Equal(["Composer 2.5", "GPT-5.6 Sol", "Gemini 3.8"], SwarmRoleModels.Load(directory).Roles["tester"]);
            Assert.Empty(SwarmRoleModels.Load(directory).Overrides);
            var first = File.ReadAllText(path);
            SwarmRoleModels.EnsureDefaults(directory);
            Assert.Equal(first, File.ReadAllText(path));

            File.WriteAllText(path, """
            {
              "roles": { "engineer": ["Hand Edited", "Still Here"] },
              "overrides": { "keep/me": "kept" }
            }
            """);
            var edited = File.ReadAllText(path);
            SwarmRoleModels.EnsureDefaults(directory);
            Assert.Equal(edited, File.ReadAllText(path));
            var loaded = SwarmRoleModels.Load(directory);
            Assert.Equal(["Hand Edited", "Still Here"], loaded.Roles["engineer"]);
            Assert.Equal("kept", loaded.Overrides["keep/me"]);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void SaveOverride_round_trips_and_does_not_drop_roles()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, SwarmRoleModels.FileName);
            SwarmRoleModels.SaveOverride(directory, "", "task", "model");
            SwarmRoleModels.SaveOverride(directory, "chat", " ", "model");
            SwarmRoleModels.SaveOverride(" ", "chat", "task", "model");
            Assert.False(File.Exists(path));

            SwarmRoleModels.SaveOverride(directory, "chat", "task", "exact-model-id");
            var created = SwarmRoleModels.Load(directory);
            Assert.Equal(["Fable", "Opus 5.5", "GPT-5.6 Sol", "Codex 5.3"], created.Roles["engineer"]);
            Assert.Equal(["Composer 2.5", "GPT-5.6 Sol", "Gemini 3.8"], created.Roles["tester"]);
            Assert.Equal("exact-model-id", created.Overrides["chat/task"]);

            File.WriteAllText(path, """
            {
              "roles": {
                "engineer": ["Kept Engineer"],
                "researcher": ["Kept Researcher"]
              },
              "overrides": { "chat/task": "exact-model-id" }
            }
            """);
            SwarmRoleModels.SaveOverride(directory, "chat", "other", "second-model");
            var loaded = SwarmRoleModels.Load(directory);
            Assert.Equal("Kept Engineer", Assert.Single(loaded.Roles["engineer"]));
            Assert.Equal("Kept Researcher", Assert.Single(loaded.Roles["researcher"]));
            Assert.False(loaded.Roles.ContainsKey("coder"));
            Assert.Equal("exact-model-id", loaded.Overrides["chat/task"]);
            Assert.Equal("second-model", loaded.Overrides["chat/other"]);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Load_uses_defaults_when_json_is_missing_corrupt_or_oversized()
    {
        Assert.Equal("Fable", SwarmRoleModels.Load(null).Roles["engineer"][0]);
        Assert.Empty(SwarmRoleModels.Load(" ").Overrides);

        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, SwarmRoleModels.FileName);
            File.WriteAllText(path, "{ not json");
            var corrupt = SwarmRoleModels.Load(directory);
            Assert.Equal("Fable", corrupt.Roles["engineer"][0]);
            Assert.Empty(corrupt.Overrides);

            File.WriteAllText(path, new string('{', 1_048_577));
            var oversized = SwarmRoleModels.Load(directory);
            Assert.Equal("Fable", oversized.Roles["engineer"][0]);
            Assert.Equal("Gemini 3.8", oversized.Roles["researcher"][0]);
            Assert.Empty(oversized.Overrides);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Single_catalog_uses_the_chat_model_for_every_role()
    {
        CursorChatModel[] models =
        [
            M("claude-fable-5-1", "Claude Fable 5.1"),
            M("composer-2.5", "Composer 2.5"),
            M("grok-4.7", "Grok 4.7")
        ];
        var catalog = SwarmRoleModels.SingleCatalog(models, "composer-2.5");
        foreach (var role in new[] { "engineer", "coder", "planner", "reviewer", "verifier", "researcher", "investigator", "tester" })
            Assert.Contains(role + " modelId: composer-2.5", catalog);
        Assert.Contains("Single model mode", catalog);
        Assert.Contains("every role uses only this modelId", catalog);
        Assert.Contains("the verifier uses this same modelId", catalog);
        Assert.Contains("grokbot-research and grokbot-ops take no modelId", catalog);
        Assert.Contains("do not substitute another model or provider", catalog);
        Assert.Contains("- composer-2.5 | Composer 2.5", catalog);
        Assert.DoesNotContain("grok-4.7", catalog);
        Assert.DoesNotContain("claude-fable-5-1", catalog);
        Assert.DoesNotContain("override ", catalog);
        Assert.DoesNotContain("must differ", catalog);
        Assert.DoesNotContain("fall back", catalog);
    }

    [Fact]
    public void Single_catalog_rejects_a_missing_model_instead_of_another_provider()
    {
        var missing = Assert.Throws<IOException>(() => SwarmRoleModels.SingleCatalog([], null));
        Assert.Contains("No swarm model is selected", missing.Message);
        Assert.Contains("will not fall back to another provider", missing.Message);

        var blank = Assert.Throws<IOException>(() => SwarmRoleModels.SingleCatalog([M("grok-4.7", "Grok 4.7")], "  "));
        Assert.Contains("will not fall back", blank.Message);

        var other = Assert.Throws<IOException>(() => SwarmRoleModels.SingleCatalog([M("grok-4.7", "Grok 4.7")], "composer-2.5"));
        Assert.Contains("composer-2.5", other.Message);
        Assert.Contains("is not available", other.Message);
        Assert.Contains("will not fall back to another provider", other.Message);
        Assert.DoesNotContain("grok-4.7", other.Message);
    }

    [Fact]
    public void Hire_catalog_uses_the_selected_model_when_mixed_models_are_off()
    {
        CursorChatModel[] models =
        [
            M("composer-2.5", "Composer 2.5"),
            M("grok-4.7", "Grok 4.7")
        ];
        foreach (bool? mixed in new bool?[] { null, false })
        {
            var catalog = CursorChatClient.SwarmHireCatalog(mixed, "unused-directory", models, "composer-2.5");
            Assert.Contains("engineer modelId: composer-2.5", catalog);
            Assert.Contains("verifier modelId: composer-2.5", catalog);
            Assert.Contains("do not substitute another model or provider", catalog);
            Assert.DoesNotContain("grok-4.7", catalog);
            Assert.DoesNotContain("must differ", catalog);
        }
    }

    [Fact]
    public void Hire_catalog_keeps_role_rankings_only_when_mixed_models_are_on()
    {
        CursorChatModel[] models =
        [
            M("grok-4.7", "Grok 4.7"),
            M("fable-5.1", "Claude Fable 5.1"),
            M("opus-5.5", "Claude Opus 5.5")
        ];
        var legacy = CursorChatClient.SwarmHireCatalog(true, " ", models, "grok-4.7");
        Assert.Contains("engineer modelId: fable-5.1", legacy);
        Assert.Contains("verifier modelId: opus-5.5", legacy);
        Assert.Contains("must differ", legacy);
        Assert.DoesNotContain("Single model mode", legacy);

        var directory = NewDirectory();
        try
        {
            SwarmRoleModels.EnsureDefaults(directory);
            var ranked = CursorChatClient.SwarmHireCatalog(true, directory, models, "grok-4.7");
            Assert.Contains("engineer modelId:", ranked);
            Assert.DoesNotContain("Single model mode", ranked);
            Assert.Contains("must differ", ranked);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Hire_catalog_throws_when_single_mode_has_no_advertised_model()
    {
        var error = Assert.Throws<IOException>(() => CursorChatClient.SwarmHireCatalog(false, null, [], null));
        Assert.Contains("will not fall back to another provider", error.Message);
        Assert.Throws<IOException>(() => CursorChatClient.SwarmHireCatalog(null, "dir", [M("a", "A")], "missing"));
    }

    [Fact]
    public void Saved_chat_remembers_mixed_and_defaults_to_single()
    {
        var created = ChatArchive.New(DeskChatConnections.CursorStored);
        Assert.Null(created.SwarmHire);
        Assert.False(SwarmRoleModels.IsMixed(created.SwarmHire));
        Assert.False(SwarmRoleModels.IsMixed("single"));
        Assert.False(SwarmRoleModels.IsMixed(""));

        var mixed = created with { SwarmHire = SwarmRoleModels.MixedMode };
        var loaded = JsonSerializer.Deserialize<SavedChat>(JsonSerializer.Serialize(mixed));
        Assert.NotNull(loaded);
        Assert.Equal(SwarmRoleModels.MixedMode, loaded.SwarmHire);
        Assert.True(SwarmRoleModels.IsMixed(loaded.SwarmHire));
        Assert.DoesNotContain("SwarmHire", JsonSerializer.Serialize(created));
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "swarm-role-models-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
