using Sovereign.Desktop.Client;
using Xunit;

namespace XaiDesk.Tests;

public sealed class CursorInstallTests
{
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void Install_button_shows_only_when_cursor_is_selected_and_the_runtime_is_missing(bool cursor, bool installed, bool offer)
    {
        Assert.Equal(offer, CursorInstall.Offer(cursor, installed));
    }

    [Fact]
    public void Not_found_connector_names_install_cursor_and_hides_the_path()
    {
        var text = DeskConnectionErrors.ForConnect(new FileNotFoundException(@"C:\secret\node.exe"), DeskConnectKind.Cursor);
        Assert.Equal(DeskConnectionErrors.CursorNotInstalled, text);
        Assert.Contains(CursorInstall.ButtonLabel, text);
        Assert.Contains("Connect / refresh models", text);
        Assert.DoesNotContain("secret", text);
        Assert.DoesNotContain("node.exe", text);
    }

    [Fact]
    public void Missing_script_opens_the_official_install_page()
    {
        var root = Path.Combine(Path.GetTempPath(), "si-desk-cursor-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var plan = CursorInstall.Plan(root, runtimeInstalled: false);
            Assert.Equal(CursorInstallStep.OpenPage, plan.Step);
            Assert.Equal("", plan.ScriptPath);
            Assert.NotNull(plan.Page);
            Assert.Equal("https", plan.Page!.Scheme);
            Assert.Equal("cursor.com", plan.Page.Host);
            Assert.Equal("/docs/cli/installation", plan.Page.AbsolutePath);
            Assert.Equal(CursorInstall.OfficialPage, plan.Page.AbsoluteUri);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Present_script_is_the_documented_installer_under_setup()
    {
        var root = Path.Combine(Path.GetTempPath(), "si-desk-cursor-install-" + Guid.NewGuid().ToString("N"));
        var setup = Path.Combine(root, "setup");
        Directory.CreateDirectory(setup);
        var script = Path.Combine(setup, "Install-CursorConnection.ps1");
        File.WriteAllText(script, "exit 0\n");
        try
        {
            var plan = CursorInstall.Plan(root, runtimeInstalled: false);
            Assert.Equal(CursorInstallStep.RunScript, plan.Step);
            Assert.Equal(Path.GetFullPath(script), plan.ScriptPath);
            Assert.Null(plan.Page);
            Assert.True(CursorInstall.TryScript(root, out var found));
            Assert.Equal(plan.ScriptPath, found);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Installed_runtime_does_not_plan_a_script_or_a_page()
    {
        var plan = CursorInstall.Plan(Path.GetTempPath(), runtimeInstalled: true);
        Assert.Equal(CursorInstallStep.AlreadyInstalled, plan.Step);
        Assert.Equal("", plan.ScriptPath);
        Assert.Null(plan.Page);
    }

    [Fact]
    public void Refuses_an_installer_that_is_not_the_documented_script()
    {
        var root = Path.Combine(Path.GetTempPath(), "si-desk-cursor-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var other = Path.Combine(root, "Install-CursorConnection.ps1");
        File.WriteAllText(other, "exit 0\n");
        try
        {
            Assert.False(CursorInstall.TryScript(root, out _));
            var error = Assert.Throws<InvalidOperationException>(() => CursorInstall.Launch(other));
            Assert.Equal(CursorInstall.ScriptMissingStatus, error.Message);
            Assert.DoesNotContain(root, error.Message);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Launch_uses_fixed_powershell_arguments_and_drops_cursor_secrets()
    {
        var root = Path.Combine(Path.GetTempPath(), "si-desk-cursor-install-" + Guid.NewGuid().ToString("N"));
        var setup = Path.Combine(root, "setup");
        Directory.CreateDirectory(setup);
        var script = Path.Combine(setup, "Install-CursorConnection.ps1");
        File.WriteAllText(script, "exit 0\n");
        var previous = Environment.GetEnvironmentVariable("CURSOR_API_KEY");
        Environment.SetEnvironmentVariable("CURSOR_API_KEY", "not-a-real-key");
        try
        {
            var info = CursorInstall.Launch(script);
            Assert.Equal("powershell.exe", info.FileName);
            Assert.False(info.UseShellExecute);
            Assert.True(info.CreateNoWindow);
            Assert.Equal(new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script }, info.ArgumentList);
            Assert.False(info.Environment.ContainsKey("CURSOR_API_KEY"));
            Assert.False(info.Environment.ContainsKey("CURSOR_AUTH_TOKEN"));
            Assert.False(info.Environment.ContainsKey("CURSOR_API_ENDPOINT"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CURSOR_API_KEY", previous);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Missing_script_does_not_start_and_hides_the_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "si-desk-cursor-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CursorInstall.RunInstallerAsync(root, CancellationToken.None));
            Assert.Equal(CursorInstall.ScriptMissingStatus, error.Message);
            Assert.DoesNotContain(root, error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Documented_script_runs_and_returns_its_exit_code()
    {
        var root = Path.Combine(Path.GetTempPath(), "si-desk-cursor-install-" + Guid.NewGuid().ToString("N"));
        var setup = Path.Combine(root, "setup");
        Directory.CreateDirectory(setup);
        File.WriteAllText(Path.Combine(setup, "Install-CursorConnection.ps1"), "if ($args.Count -ne 0) { exit 3 }\nif ($env:CURSOR_API_KEY) { exit 4 }\nexit 9\n");
        var previous = Environment.GetEnvironmentVariable("CURSOR_API_KEY");
        Environment.SetEnvironmentVariable("CURSOR_API_KEY", "not-a-real-key");
        try
        {
            var code = await CursorInstall.RunInstallerAsync(root, CancellationToken.None);
            Assert.Equal(9, code);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CURSOR_API_KEY", previous);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Status_text_names_the_next_click_and_carries_no_secret()
    {
        foreach (var text in new[]
        {
            CursorInstall.AlreadyInstalledStatus, CursorInstall.InstalledStatus, CursorInstall.FailedStatus,
            CursorInstall.CanceledStatus, CursorInstall.PageOpenedStatus, CursorInstall.PageFailedStatus
        })
        {
            Assert.Contains("Connect / refresh models", text);
            Assert.DoesNotContain("CURSOR_", text);
            Assert.DoesNotContain("\\", text);
        }
    }

    [Fact]
    public void Connection_flyout_source_keeps_install_beside_connect()
    {
        var text = File.ReadAllText(Path.Combine(FindSourceRoot(), "apps", "desktop", "Sovereign.Desktop", "ChatRoom.cs"));
        var flyout = text.IndexOf("_connectionFlyout", StringComparison.Ordinal);
        var row = text.IndexOf("_chatConnect, _chatLogin, _chatInstall", StringComparison.Ordinal);
        Assert.True(flyout >= 0 && row > flyout);
        Assert.Contains("CursorInstall.ButtonLabel", text);
        Assert.Contains("CursorInstall.Plan", text);
        Assert.Contains("Connect / refresh models", text);
        var scroll = text.IndexOf("HorizontalScrollMode = ScrollMode.Disabled", StringComparison.Ordinal);
        Assert.True(scroll > flyout);
        Assert.Contains("MaxWidth = ConnectionFlyoutWidth", text);
        Assert.Contains("VerticalScrollBarVisibility = ScrollBarVisibility.Auto", text);
        var swarm = File.ReadAllText(Path.Combine(FindSourceRoot(), "apps", "desktop", "Sovereign.Desktop", "ChatSubagents.cs"));
        Assert.Contains("_swarmHireRow = new() { Orientation = Orientation.Vertical", swarm);
        Assert.Contains("_subagentOptionRow = new() { Orientation = Orientation.Vertical", swarm);
        Assert.Contains("FlyoutLabel(\"Grokbot reviews handoffs\")", swarm);
    }

    private static string FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))
                && Directory.Exists(Path.Combine(dir.FullName, "apps", "desktop")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("No Directory.Build.props above the test output contains apps\\desktop.");
    }
}
