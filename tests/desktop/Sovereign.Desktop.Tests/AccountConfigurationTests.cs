using Sovereign.Desktop.Client;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class AccountConfigurationTests
{
    [Fact]
    public void Empty_placeholders_leave_sign_in_unconfigured()
    {
        var path = Path.Combine(Path.GetTempPath(), "xai-desk-account-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{ "issuer": "", "clientId": "" }""");
        try
        {
            var missing = AccountConfiguration.Read(path + ".missing");
            Assert.False(missing.IsConfigured);
            var config = AccountConfiguration.Read(path);
            Assert.False(config.IsConfigured);
            Assert.Equal("", config.Issuer);
            Assert.Equal("", config.ClientId);
            var ex = Assert.Throws<IOException>(() => new SovereignAccountClient(config));
            Assert.Contains("not configured", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
