using System.Text;
using System.Text.Json;
using Sovereign.Desktop.Client;
using Xunit;

namespace XaiDesk.Tests;

public sealed class CursorModelCatalogTests
{
    [Fact]
    public void Parse_keeps_advertised_models_and_puts_grok_4_7_first()
    {
        using var document = JsonDocument.Parse("""
            {
              "currentModelId": "gpt-5.4",
              "availableModels": [
                { "modelId": "gpt-5.4", "name": "GPT" },
                { "modelId": "claude-sonnet", "name": "Claude" },
                { "modelId": "gemini-2.5", "name": "Gemini" },
                { "modelId": "composer-2", "name": "Composer" },
                { "modelId": "grok-4", "name": "Grok" },
                { "modelId": "grok-4.7[context=256k,reasoning_effort=high,fast=true]", "name": "Zed" },
                { "modelId": "default[]", "name": "Auto" },
                { "modelId": "", "name": "Blank" },
                { "modelId": "   ", "name": "Spaces" },
                { "name": "Missing id" },
                { "modelId": 4, "name": "Not a string" },
                { "modelId": "unnamed-model" }
              ]
            }
            """);

        var listing = CursorModelCatalog.Parse(document.RootElement);

        Assert.Equal(
            [
                "grok-4.7[context=256k,reasoning_effort=high,fast=true]",
                "unnamed-model",
                "default[]",
                "claude-sonnet",
                "composer-2",
                "gemini-2.5",
                "gpt-5.4",
                "grok-4"
            ],
            listing.Models.Select(model => model.Id).ToArray());
        Assert.Equal("Auto", listing.Models.Single(model => model.Id == "default[]").Name);
        Assert.Equal("", listing.Models.Single(model => model.Id == "unnamed-model").Name);
        Assert.DoesNotContain(listing.Models, model => string.IsNullOrWhiteSpace(model.Id));
        Assert.StartsWith("grok-4.7[", listing.Models[0].Id, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_reads_current_model_id()
    {
        using var listed = JsonDocument.Parse("""
            { "currentModelId": "claude-sonnet", "availableModels": [ { "modelId": "claude-sonnet", "name": "Claude" } ] }
            """);
        var listing = CursorModelCatalog.Parse(listed.RootElement);
        Assert.Equal("claude-sonnet", listing.CurrentModelId);
        Assert.Equal("claude-sonnet", Assert.Single(listing.Models).Id);

        using var missing = JsonDocument.Parse("""{ "currentModelId": "grok-4.7[fast=true]" }""");
        var empty = CursorModelCatalog.Parse(missing.RootElement);
        Assert.Empty(empty.Models);
        Assert.Equal("grok-4.7[fast=true]", empty.CurrentModelId);

        using var notArray = JsonDocument.Parse("""{ "currentModelId": 3, "availableModels": { "modelId": "gpt-5.4" } }""");
        var skipped = CursorModelCatalog.Parse(notArray.RootElement);
        Assert.Empty(skipped.Models);
        Assert.Null(skipped.CurrentModelId);
    }

    [Fact]
    public void SelectDefault_prefers_the_saved_pick_then_grok_4_7()
    {
        CursorChatModel[] models =
        [
            new("gpt-5.4", "GPT"),
            new("grok-4.7[b]", "B"),
            new("grok-4.7[a]", "A"),
            new("default[]", "Auto")
        ];

        Assert.Null(CursorModelCatalog.SelectDefault([], "gpt-5.4", "gpt-5.4"));
        Assert.Equal("gpt-5.4", CursorModelCatalog.SelectDefault(models, "gpt-5.4", "grok-4.7[a]"));
        Assert.Equal("grok-4.7[b]", CursorModelCatalog.SelectDefault(models, "missing", "gpt-5.4"));
        Assert.Equal("grok-4.7[b]", CursorModelCatalog.SelectDefault(models, null, "default[]"));
        Assert.Equal("grok-4.7[b]", CursorModelCatalog.SelectDefault(models, "", "claude-sonnet"));

        CursorChatModel[] withoutGrok = [new("gpt-5.4", "GPT"), new("claude-sonnet", "Claude")];
        Assert.Equal("claude-sonnet", CursorModelCatalog.SelectDefault(withoutGrok, null, "claude-sonnet"));
        Assert.Equal("gpt-5.4", CursorModelCatalog.SelectDefault(withoutGrok, "absent", "absent"));
    }

    [Fact]
    public void Resolve_returns_live_when_non_empty_and_cache_otherwise()
    {
        var live = new[] { new CursorChatModel("gpt-5.4", "GPT") };
        var cached = new[] { new CursorChatModel("grok-4.7[fast=true]", "Grok") };

        Assert.Same(live, CursorModelCatalog.Resolve(live, cached));
        Assert.Same(cached, CursorModelCatalog.Resolve(null, cached));
        Assert.Same(cached, CursorModelCatalog.Resolve([], cached));
        Assert.Empty(CursorModelCatalog.Resolve(null, null!));
        Assert.Empty(CursorModelCatalog.Resolve([], null!));
    }

    [Fact]
    public void Save_then_Load_round_trips_id_and_name()
    {
        var directory = NewDirectory();
        try
        {
            CursorChatModel[] models = [new("default[]", "Auto"), new("grok-4.7[fast=true]", "Grok 4.7")];
            CursorModelCatalog.Save(directory, []);
            Assert.Empty(Directory.GetFiles(directory));
            CursorModelCatalog.Save(directory, models);

            var path = Path.Combine(directory, CursorModelCatalog.FileName);
            Assert.Equal([path], Directory.GetFiles(directory));
            using (var document = JsonDocument.Parse(File.ReadAllText(path)))
            {
                var stored = document.RootElement.GetProperty("models");
                Assert.Equal("default[]", stored[0].GetProperty("id").GetString());
                Assert.Equal("Auto", stored[0].GetProperty("name").GetString());
                Assert.Equal("grok-4.7[fast=true]", stored[1].GetProperty("id").GetString());
                Assert.Equal("Grok 4.7", stored[1].GetProperty("name").GetString());
                Assert.Equal(2, stored[0].EnumerateObject().Count());
            }

            Assert.Equal(models, CursorModelCatalog.Load(directory));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Load_of_a_missing_file_and_corrupt_json_returns_empty()
    {
        var directory = NewDirectory();
        try
        {
            Assert.Empty(CursorModelCatalog.Load(directory));
            Assert.Empty(CursorModelCatalog.Load(Path.Combine(directory, "missing-profile")));

            var path = Path.Combine(directory, CursorModelCatalog.FileName);
            File.WriteAllText(path, "{");
            Assert.Empty(CursorModelCatalog.Load(directory));
            File.WriteAllText(path, """{"models":{"id":"gpt-5.4"}}""");
            Assert.Empty(CursorModelCatalog.Load(directory));

            var id = new string('m', 1_048_600);
            var payload = JsonSerializer.Serialize(new { models = new[] { new { id, name = "Big" } } });
            Assert.True(Encoding.UTF8.GetByteCount(payload) > 1_048_576);
            File.WriteAllText(path, payload);
            Assert.Empty(CursorModelCatalog.Load(directory));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cursor-model-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
