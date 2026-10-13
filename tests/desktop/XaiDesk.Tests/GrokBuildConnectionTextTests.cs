using Sovereign.Desktop.Client;
using Xunit;

namespace XaiDesk.Tests;

public sealed class GrokBuildConnectionTextTests
{
    [Fact]
    public void Format_success_includes_version_email_and_plan()
    {
        const string account = "user at example.test";
        var report = new GrokBuildStatusReport("1.2.3", account, "Free", null);
        var text = GrokBuildConnectionText.Format(report);

        Assert.Contains("1.2.3", text, StringComparison.Ordinal);
        Assert.Contains(account, text, StringComparison.Ordinal);
        Assert.Contains("Free", text, StringComparison.Ordinal);
        Assert.Equal("grok 1.2.3 · signed in as " + account + " · plan Free", text);
        var cliLine = GrokBuildConnectionText.Format(new GrokBuildStatusReport("grok 1.0.50 (c58f321264ba) [stable]", account, "unknown", null));
        Assert.Equal("grok 1.0.50 (c58f321264ba) [stable] · signed in as " + account + " · plan unknown", cliLine);
    }

    [Fact]
    public void Format_error_is_the_exact_error_text()
    {
        var report = new GrokBuildStatusReport("", "", "", "429 free-usage-exhausted");

        Assert.Equal("429 free-usage-exhausted", GrokBuildConnectionText.Format(report));
    }

    [Fact]
    public void OfferCursorGrok47_is_true_for_usage_limit_failures()
    {
        Assert.True(GrokBuildConnectionText.OfferCursorGrok47(new GrokBuildUsageException("429 free-usage-exhausted")));
        Assert.True(GrokBuildConnectionText.OfferCursorGrok47(new IOException("HTTP 429 free-usage-exhausted")));
    }

    [Fact]
    public void OfferCursorGrok47_is_false_for_a_plain_io_failure()
    {
        Assert.False(GrokBuildConnectionText.OfferCursorGrok47(new IOException("disk full")));
    }

    [Fact]
    public void ChatFailureText_returns_the_exact_usage_message()
    {
        const string message = "429 free-usage-exhausted";
        var text = GrokBuildConnectionText.ChatFailureText(new GrokBuildUsageException(message));

        Assert.NotNull(text);
        Assert.Equal(message, text);
        Assert.DoesNotContain("Run failed locally", text);
    }
}
