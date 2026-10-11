using Sovereign.Desktop.SetupLib;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class WebView2PrerequisiteTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("invalid", false)]
    [InlineData("145.0.3800.82", true)]
    public void Detection_requires_a_nonzero_version(string? version, bool expected) =>
        Assert.Equal(expected, WebView2Prerequisite.IsInstalledVersion(version));

    [Fact]
    public void Present_runtime_does_not_download_or_execute()
    {
        var platform = new Fixture { Version = "145.0.3800.82" };
        Assert.Null(WebView2Prerequisite.Ensure(platform, []));
        Assert.Equal(new[] { "detect" }, platform.Calls);
    }

    [Fact]
    public void Missing_runtime_explains_download_then_verifies_before_execution_and_rechecks()
    {
        var platform = new Fixture();
        var messages = new List<string>();
        Assert.Null(WebView2Prerequisite.Ensure(platform, [], messages.Add));
        Assert.Equal(new[] { "detect", "download", "signature", "install", "detect" }, platform.Calls);
        Assert.Contains(messages, value => value.Contains("Internet access is required", StringComparison.Ordinal));
        Assert.False(File.Exists(platform.DownloadPath));
    }

    [Fact]
    public void Invalid_signature_is_never_run()
    {
        var platform = new Fixture { ValidSignature = false };
        var result = WebView2Prerequisite.Ensure(platform, []);
        Assert.Equal(WebView2Prerequisite.FailedExitCode, result?.ExitCode);
        Assert.DoesNotContain("install", platform.Calls);
        Assert.False(File.Exists(platform.DownloadPath));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public void Failed_or_unconfirmed_install_is_actionable(int exit, bool setVersion)
    {
        var platform = new Fixture { Exit = exit, SetVersion = setVersion };
        var result = WebView2Prerequisite.Ensure(platform, []);
        Assert.Equal(WebView2Prerequisite.FailedExitCode, result?.ExitCode);
        Assert.Contains("retry setup", result!.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://go.microsoft.com/fwlink/p/?LinkId=2124703", true)]
    [InlineData("https://msedge.sf.dl.delivery.mp.microsoft.com/file.exe", true)]
    [InlineData("https://download.microsoft.com/file.exe", true)]
    [InlineData("http://download.microsoft.com/file.exe", false)]
    [InlineData("https://microsoft.com.attacker.test/file.exe", false)]
    [InlineData("https://attacker.test/file.exe", false)]
    [InlineData("https://user@microsoft.com/file.exe", false)]
    [InlineData("https://microsoft.com:444/file.exe", false)]
    public void Download_redirects_stay_on_Microsoft_https(string uri, bool expected) =>
        Assert.Equal(expected, WindowsWebView2Prerequisite.IsMicrosoftDownloadUri(new Uri(uri)));

    private sealed class Fixture : IWebView2PrerequisitePlatform
    {
        public List<string> Calls { get; } = [];
        public string? Version { get; set; }
        public bool ValidSignature { get; init; } = true;
        public bool SetVersion { get; init; } = true;
        public int Exit { get; init; }
        public string? DownloadPath { get; private set; }
        public string? InstalledVersion() { Calls.Add("detect"); return Version; }
        public void DownloadBootstrapper(string destination) { Calls.Add("download"); DownloadPath = destination; File.WriteAllText(destination, "fixture"); }
        public bool HasValidMicrosoftSignature(string path) { Calls.Add("signature"); return ValidSignature; }
        public int InstallBootstrapper(string path) { Calls.Add("install"); if (SetVersion) Version = "145.0.3800.82"; return Exit; }
    }
}
