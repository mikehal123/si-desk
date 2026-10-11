using Sovereign.Desktop.Client;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class ChatActivityPresentationTests
{
    [Theory]
    [InlineData("read", "Reads", "Read files · 9 steps")]
    [InlineData("execute", "Commands", "Ran 9 commands")]
    [InlineData("edit", "Edits", "Edited files · 9 steps")]
    [InlineData("fetch", "Sources", "Read sources · 9 steps")]
    [InlineData("search", "Searches", "Searched · 9 steps")]
    [InlineData("unknown", "Other tools", "Other tools · 9 steps")]
    public void GroupsCountStepsWithoutInventingDistinctFileCounts(string kind, string category, string label)
    {
        Assert.Equal(category, ChatActivityPresentation.Category(kind));
        Assert.Equal(label, ChatActivityPresentation.GroupLabel(category, 9));
    }

    [Fact]
    public void PreviewIsBoundedProviderTextAndDoesNotSplitEmoji()
    {
        Assert.Equal("café 中文 😀", ChatActivityPresentation.ReasoningPreview("  café\n\n中文\t😀  "));
        Assert.Equal("abc…", ChatActivityPresentation.ReasoningPreview("abc😀def", 4));
        Assert.Equal("abc😀…", ChatActivityPresentation.ReasoningPreview("abc😀def", 5));
        Assert.Equal("", ChatActivityPresentation.ReasoningPreview("\r\n "));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChatActivityPresentation.ReasoningPreview("x", 0));
    }

    [Fact]
    public void CompletedAndRestoredWorkHaveTruthfulChips()
    {
        Assert.Equal("Worked 2m 6s · 101 steps", ChatActivityPresentation.Completion(TimeSpan.FromSeconds(126), 101, "completed", false));
        Assert.Equal("Saved work · 1 step · interrupted", ChatActivityPresentation.Completion(TimeSpan.FromSeconds(2), 1, "interrupted", true));
        Assert.Equal("Worked 0s · 0 steps · stopped", ChatActivityPresentation.Completion(TimeSpan.Zero, 0, "stopped", false));
    }
}
