using System.Diagnostics;
using Sovereign.Desktop.SetupLib;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class InstallerTests
{
    [Fact]
    public void Refuses_git_checkout()
    {
        var repo = Path.Combine(Path.GetTempPath(), "xai-desk-checkout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        try
        {
            var result = InstallerEngine.Run(new SetupRequest(
                SetupAction.Install,
                repo,
                Path.GetTempPath(),
                "SIDesk.TEST",
                "SI Desk TEST",
                "0.1.1",
                null,
                "SI Desk TEST",
                true,
                null,
                null));
            Assert.Equal(4, result.ExitCode);
            Assert.Contains("REFUSED_GIT_CHECKOUT", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(repo)) Directory.Delete(repo, true);
        }
    }

    [Fact]
    public void Missing_install_dir_flag_uses_the_default_programs_directory()
    {
        var error = SetupCli.ParseOrError(["--payload", Path.GetTempPath(), "--test-instance", "n2int"], out var request);
        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal(SetupCli.DefaultInstallDir(), request!.InstallDir);
        Assert.Equal(SetupCli.ProductionProductId + ".n2int", request.ProductId);
    }

    [Fact]
    public void Install_dir_flag_without_value_does_not_fall_back_to_localappdata()
    {
        var error = SetupCli.ParseOrError(["--install-dir", "--payload", Path.GetTempPath()], out var request);
        Assert.Null(request);
        Assert.NotNull(error);
        Assert.Equal(SetupCli.MissingInstallDirExitCode, error!.ExitCode);
        Assert.Contains("without a value", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_dir_flag_as_last_argument_does_not_fall_back_to_localappdata()
    {
        var error = SetupCli.ParseOrError(["--payload", Path.GetTempPath(), "--install-dir"], out var request);
        Assert.Null(request);
        Assert.NotNull(error);
        Assert.Equal(SetupCli.MissingInstallDirExitCode, error!.ExitCode);
    }

    [Fact]
    public void Whitespace_install_dir_is_refused_by_engine()
    {
        var result = InstallerEngine.Run(new SetupRequest(
            SetupAction.Install,
            "  ",
            Path.GetTempPath(),
            "SovereignAI.DesktopPreview.TEST",
            "SI Desk Desktop Preview TEST",
            "0.1.7",
            null,
            "SI Desk Desktop Preview TEST",
            true,
            null,
            null));
        Assert.Equal(SetupCli.MissingInstallDirExitCode, result.ExitCode);
        Assert.Contains("MISSING_REQUIRED_ARGUMENT", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_install_dir_parses()
    {
        var install = Path.Combine(Path.GetTempPath(), "SovereignAI.DesktopPreview.N2INT", "install");
        var error = SetupCli.ParseOrError(
            ["--install-dir", install, "--payload", Path.GetTempPath(), "--test-instance", "n2intprobe"],
            out var request);
        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal(install, request!.InstallDir);
        Assert.Equal(SetupCli.ProductionProductId + ".n2intprobe", request.ProductId);
        Assert.NotEqual(InstallerEngine.ProductionProductId, request.ProductId);
    }

    [Fact]
    public void Fresh_unicode_repair_upgrade_uninstall_interrupt_and_sentinel()
    {
        var root = Path.Combine(Path.GetTempPath(), "SI Desk Desktop 测试 " + Guid.NewGuid().ToString("N")[..8]);
        var payloadA = Path.Combine(root, "payload-a");
        var payloadB = Path.Combine(root, "payload-b");
        var install = Path.Combine(root, "install dir");
        var profile = Path.Combine(root, "profile dir");
        var sentinelDir = Path.Combine(Path.GetTempPath(), "SovereignAI.DesktopPreview.sentinel-outside");
        var sentinel = Path.Combine(sentinelDir, "keep-me.txt");
        Directory.CreateDirectory(payloadA);
        Directory.CreateDirectory(payloadB);
        Directory.CreateDirectory(profile);
        Directory.CreateDirectory(sentinelDir);
        File.WriteAllText(Path.Combine(payloadA, "app.txt"), "A");
        File.WriteAllText(Path.Combine(payloadB, "app.txt"), "B");
        File.WriteAllText(Path.Combine(payloadB, "SovereignAI.DesktopPreview.exe"), "fake-exe");
        File.WriteAllText(Path.Combine(profile, "drafts-keep.txt"), "draft");
        File.WriteAllText(sentinel, "untouched");
        var product = "SovereignAI.DesktopPreview.DESK16-" + Guid.NewGuid().ToString("N")[..8];
        var start = "SI Desk Desktop Preview " + product;
        try
        {
            var interrupted = InstallerEngine.Run(new SetupRequest(SetupAction.Install, install, payloadA, product, start, "0.1.0", profile, start, true, 0, sentinel));
            Assert.Equal(3, interrupted.ExitCode);
            Assert.Contains("INCOMPLETE_INSTALL", interrupted.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(install));
            Assert.Equal("untouched", File.ReadAllText(sentinel));

            var fresh = InstallerEngine.Run(new SetupRequest(SetupAction.Install, install, payloadA, product, start, "0.1.0", profile, start, true, null, sentinel));
            Assert.True(fresh.Ok, fresh.Message);
            Assert.Equal("A", File.ReadAllText(Path.Combine(install, "app.txt")));
            File.WriteAllText(Path.Combine(profile, "preferences.json"), """{"theme":"Dark"}""");

            var repair = InstallerEngine.Run(new SetupRequest(SetupAction.Repair, install, payloadA, product, start, "0.1.0", profile, start, true, null, sentinel));
            Assert.True(repair.Ok, repair.Message);
            Assert.True(File.Exists(Path.Combine(profile, "preferences.json")));
            Assert.Equal("draft", File.ReadAllText(Path.Combine(profile, "drafts-keep.txt")));

            var upgrade = InstallerEngine.Run(new SetupRequest(SetupAction.Upgrade, install, payloadB, product, start, "0.1.1", profile, start, true, null, sentinel));
            Assert.True(upgrade.Ok, upgrade.Message);
            Assert.Equal("B", File.ReadAllText(Path.Combine(install, "app.txt")));
            var receipt = File.ReadAllText(Path.Combine(install, "preview-setup-receipt.json"));
            Assert.Contains("0.1.1", receipt, StringComparison.Ordinal);
            Assert.Contains("0.1.0", receipt, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(profile, "preferences.json")));

            var keep = InstallerEngine.Run(new SetupRequest(SetupAction.Uninstall, install, payloadB, product, start, "0.1.1", profile, start, true, null, sentinel));
            Assert.True(keep.Ok, keep.Message);
            Assert.False(Directory.Exists(install));
            Assert.True(File.Exists(Path.Combine(profile, "preferences.json")));

            Directory.CreateDirectory(install);
            File.WriteAllText(Path.Combine(install, "app.txt"), "reinstall");
            var remove = InstallerEngine.Run(new SetupRequest(SetupAction.Uninstall, install, payloadB, product, start, "0.1.1", profile, start, false, null, sentinel));
            Assert.True(remove.Ok, remove.Message);
            Assert.False(Directory.Exists(profile));
            Assert.Equal("untouched", File.ReadAllText(sentinel));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }

            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + product, false);
            var startDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", start);
            if (Directory.Exists(startDir))
            {
                Directory.Delete(startDir, true);
            }
        }
    }

    [Fact]
    public void Repair_fails_explicitly_when_owned_python_cannot_be_stopped()
    {
        var root = Path.Combine(Path.GetTempPath(), "SI Desk shutdown " + Guid.NewGuid().ToString("N")[..8]);
        var payload = Path.Combine(root, "payload");
        var install = Path.Combine(root, "install");
        var profile = Path.Combine(root, "profile");
        Directory.CreateDirectory(payload);
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(payload, "app.txt"), "A");
        var product = "SovereignAI.DesktopPreview.SHUT-" + Guid.NewGuid().ToString("N")[..8];
        Process? holder = null;
        try
        {
            var installed = InstallerEngine.Run(new SetupRequest(SetupAction.Install, install, payload, product, product, "0.1.7", profile, product, true, null, null));
            Assert.True(installed.Ok, installed.Message);
            var pythonDir = Path.Combine(install, "engine-bundle", "python");
            Directory.CreateDirectory(pythonDir);
            var python = Path.Combine(pythonDir, "python.exe");
            File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), python);
            holder = Process.Start(new ProcessStartInfo
            {
                FileName = python,
                Arguments = "/c ping -t 127.0.0.1",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            Assert.NotNull(holder);
            var repair = InstallerEngine.Run(new SetupRequest(SetupAction.Repair, install, payload, product, product, "0.1.7", profile, product, true, null, null));
            Assert.Equal(InstallerEngine.HostShutdownFailedExitCode, repair.ExitCode);
            Assert.Contains("HOST_SHUTDOWN_FAILED", repair.Message, StringComparison.Ordinal);
            Assert.False(holder!.HasExited);
            Assert.DoesNotContain("46860", repair.Message, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                holder?.Kill(entireProcessTree: true);
                holder?.WaitForExit(5000);
            }
            catch
            {
            }

            holder?.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }

            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + product, false);
            var startDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", product);
            if (Directory.Exists(startDir))
            {
                Directory.Delete(startDir, true);
            }
        }
    }
}
