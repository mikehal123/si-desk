using System.Text.Json;
using Sovereign.Desktop.Client;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class CodexCatalogTests
{
    private static JsonElement Page(string json) => JsonSerializer.Deserialize<JsonElement>(json);
    [Fact]
    public async Task Includes_later_pages_without_duplicates_or_hidden_models()
    {
        var cursors = new List<string?>();
        var models = await CodexChatClient.ReadModelPagesAsync((cursor, _) =>
        {
            cursors.Add(cursor);
            return Task.FromResult(cursor is null
                ? Page("""{"data":[{"model":"first"},{"model":"hidden","hidden":true}],"nextCursor":"page-2"}""")
                : Page("""{"data":[{"model":"first"},{"model":"later"}],"nextCursor":null}"""));
        }, default);
        Assert.Equal(new[] { "first", "later" }, models);
        Assert.Equal(new string?[] { null, "page-2" }, cursors);
    }
    [Fact]
    public async Task Repeated_cursor_fails_instead_of_looping_or_claiming_complete_catalog()
    {
        await Assert.ThrowsAsync<IOException>(() => CodexChatClient.ReadModelPagesAsync((_, _) =>
            Task.FromResult(Page("""{"data":[],"nextCursor":"same"}""")), default));
    }
}
