using Sovereign.Desktop.Client;
using Sovereign.Desktop.Core;
using Xunit;

namespace XaiDesk.Tests;

public sealed class DeskChatConnectionTests
{
    [Fact]
    public void Model_picker_keeps_only_grok_and_xai_models()
    {
        var models = DeskChatConnections.GrokModels(
        [
            new CursorChatModel("grok-4.7[fast=true]", "Grok 4.7 High Fast"),
            new CursorChatModel("grok-4", "Grok 4"),
            new CursorChatModel("xai-grok-legacy", "Legacy"),
            new CursorChatModel("gpt-4o", "GPT-4o"),
            new CursorChatModel("gemini-2", "Gemini"),
            new CursorChatModel("claude-sonnet", "Claude"),
            new CursorChatModel("composer-2", "Composer")
        ]);

        Assert.Equal(
            ["grok-4.7[fast=true]", "grok-4", "xai-grok-legacy"],
            models.Select(model => model.Id).ToArray());
        Assert.Equal("grok-4.7[fast=true]", models[0].Id);
    }

    [Fact]
    public void Picker_labels_are_grouped_by_connection()
    {
        Assert.Equal("Cursor · Grok 4.7", DeskChatConnections.PickerLabel(DeskChatConnections.Cursor, "Grok 4.7"));
        Assert.Equal("Grok Build · Grok 4.7", DeskChatConnections.PickerLabel(DeskChatConnections.GrokBuild, "Grok 4.7"));
        Assert.Equal("xAI API · grok-4", DeskChatConnections.PickerLabel(DeskChatConnections.XaiApi, "grok-4"));
        Assert.Equal(["Cursor", "Grok Build", "xAI API"], DeskChatConnections.Labels);
    }

    [Fact]
    public void Each_chat_remembers_its_connection()
    {
        var cursor = ChatArchive.New(DeskChatConnections.CursorStored) with { Connection = "cursor", Model = "grok-4.7[fast=true]" };
        var grok = ChatArchive.New(DeskChatConnections.GrokBuildStored) with { Connection = "grok-build", Model = "grok-4.7" };
        var api = ChatArchive.New(DeskChatConnections.XaiApiStored) with { Connection = "xai-api", Model = "grok-4" };

        Assert.Equal(DeskChatConnections.Cursor, DeskChatConnections.Combo(cursor.Provider));
        Assert.Equal(DeskChatConnections.GrokBuild, DeskChatConnections.Combo(grok.Provider));
        Assert.Equal(DeskChatConnections.XaiApi, DeskChatConnections.Combo(api.Provider));
        Assert.Equal(DeskChatConnections.CursorStored, DeskChatConnections.Stored(DeskChatConnections.Cursor));
        Assert.Equal(DeskChatConnections.GrokBuildStored, DeskChatConnections.Stored(DeskChatConnections.GrokBuild));
        Assert.Equal(DeskChatConnections.XaiApiStored, DeskChatConnections.Stored(DeskChatConnections.XaiApi));
        Assert.NotEqual(cursor.Provider, grok.Provider);
        Assert.NotEqual(grok.Provider, api.Provider);
    }

    [Fact]
    public void Grok_build_stays_a_separate_connection_from_cursor()
    {
        var signedOut = DeskChatConnections.Combo(DeskChatConnections.GrokBuildStored);
        Assert.Equal(DeskChatConnections.GrokBuild, signedOut);
        Assert.NotEqual(DeskChatConnections.Cursor, signedOut);
        Assert.False(XaiDeskIdentity.IsDeskModel("gpt-4o"));
        Assert.False(XaiDeskIdentity.IsDeskModel("claude-opus"));
        Assert.True(XaiDeskIdentity.IsDeskModel("grok-4.7"));
        Assert.True(XaiDeskIdentity.IsDeskModel("custom", "xAI Grok"));
    }

    [Fact]
    public void Cursor_runtime_uses_the_xai_desk_connector()
    {
        var root = Path.Combine(Path.GetTempPath(), "xai-desk-runtime-" + Guid.NewGuid().ToString("N"));
        var own = Path.Combine(root, "xAIDesk", "Tools", "CursorAgent", CursorChatClient.CliVersion, "dist-package");
        try
        {
            Assert.Equal(own, CursorChatClient.ResolveRuntimeDirectory(root));
            Directory.CreateDirectory(own);
            File.WriteAllText(Path.Combine(own, "node.exe"), "own");
            File.WriteAllText(Path.Combine(own, "index.js"), "own");
            Assert.Equal(own, CursorChatClient.ResolveRuntimeDirectory(root));
        }
        finally { Directory.Delete(root, true); }
    }
}
