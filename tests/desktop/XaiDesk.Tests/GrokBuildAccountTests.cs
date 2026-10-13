using Sovereign.Desktop.Client;
using Xunit;

namespace XaiDesk.Tests;

public sealed class GrokBuildAccountTests
{
    [Fact]
    public void Missing_auth_email_is_not_logged_in()
    {
        var report = GrokBuildAccount.Interpret(
            "grok 1.0.50",
            """{"acct":{"first_name":"Ada","last_name":"Lovelace"}}""",
            "grok-4.7",
            null);
        Assert.Equal("grok 1.0.50", report.Version);
        Assert.Equal("not logged in", report.SignedInAs);
        Assert.Equal("unknown", report.Plan);
        Assert.NotNull(report.Error);
        Assert.Contains("not logged in", report.Error);
        Assert.False(report.Ok);
    }

    [Fact]
    public void Free_usage_models_text_sets_the_error_and_free_plan()
    {
        var report = GrokBuildAccount.Interpret(
            "grok 1.0.50",
            """{"acct":{"email":"ada@example.com"}}""",
            "HTTP 429 free-usage-exhausted",
            null);
        Assert.Contains("HTTP 429 free-usage-exhausted", report.Error);
        Assert.Equal("free", report.Plan);
        Assert.Equal("ada@example.com", report.SignedInAs);
        Assert.False(report.Ok);
    }

    [Fact]
    public void Email_and_models_list_is_ok()
    {
        const string secret = "super-secret-token";
        var report = GrokBuildAccount.Interpret(
            "grok 1.0.50",
            "{\"acct\":{\"email\":\"ada@example.com\",\"first_name\":\"Ada\",\"api_key\":\"" + secret + "\",\"refresh_token\":\"" + secret + "\"}}",
            "grok-4.7\ngrok-3",
            null);
        Assert.True(report.Ok);
        Assert.Null(report.Error);
        Assert.Equal("ada@example.com", report.SignedInAs);
        Assert.Equal("grok 1.0.50", report.Version);
        Assert.Equal("unknown", report.Plan);
        Assert.DoesNotContain(secret, report.SignedInAs);
        Assert.DoesNotContain(secret, report.Version);
    }
}
