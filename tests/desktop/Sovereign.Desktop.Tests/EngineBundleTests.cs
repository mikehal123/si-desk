using Sovereign.Desktop.Client.Adapters;
using Sovereign.Desktop.Client.Host;
using Sovereign.Desktop.Core;
using Sovereign.Desktop.SetupLib;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class EngineBundleTests
{
    [Fact]
    public void Verify_fails_when_engine_directory_is_missing()
    {
        var result = EngineBundleVerifier.Verify(Path.Combine(Path.GetTempPath(), "no-such-engine-" + Guid.NewGuid().ToString("N")));
        Assert.False(result.Ok);
        Assert.Contains("ENGINE_MISSING", result.Message, StringComparison.Ordinal);
        Assert.Contains("Preview fixtures", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_refuses_grok_staging_root()
    {
        var result = EngineBundleVerifier.Verify(Path.Combine(Path.GetTempPath(), "missing-engine-stage-" + Guid.NewGuid().ToString("N")));
        Assert.False(result.Ok);
        Assert.Contains("ENGINE_MISSING", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_reports_missing_listed_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "sov-eng-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "python"));
        Directory.CreateDirectory(Path.Combine(root, "native", "engine"));
        File.WriteAllText(Path.Combine(root, "python", "python.exe"), "py");
        File.WriteAllText(Path.Combine(root, "native", "engine", "host_entry.py"), "entry");
        File.WriteAllText(Path.Combine(root, "bundle-manifest.json"),
            """{"files":[{"path":"python/python.exe","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},{"path":"native/engine/host_entry.py","sha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"},{"path":"missing.bin","sha256":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"}]}""");
        var result = EngineBundleVerifier.Verify(root);
        Assert.False(result.Ok);
        Assert.Contains("ENGINE_CORRUPT", result.Message, StringComparison.Ordinal);
        Assert.Contains(result.Problems, p => p.Contains("missing.bin", StringComparison.Ordinal));
        Directory.Delete(root, true);
    }

    [Fact]
    public void Resolver_does_not_follow_stage_root()
    {
        var install = Path.Combine(Path.GetTempPath(), "sov-res-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "engine-bundle-pin.json"),
            """{"schema":"sovereign-desktop-engine-bundle-pin-v1","archive_sha256":"6bf51e05ebe97500be23c423caef726c722b02c034b83a4746708eeb666c4d74","bundle_version":"2.0.0","contract_packet":"2.1.0","source_revision":"x","engine_relative":"engine-bundle","stage_root":"C:\\Windows\\Temp\\engine-stage\\Engine.2.0.0","profile_id":"grok-offline-integration"}""");
        var resolved = InstalledEngineResolver.TryResolve(install, out var reason);
        Assert.Null(resolved);
        Assert.DoesNotContain("grok-engine-stage", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ENGINE_MISSING", reason, StringComparison.Ordinal);
        Directory.Delete(install, true);
    }

    [Fact]
    public async Task Connect_without_engine_or_endpoint_does_not_use_preview_or_staging()
    {
        var profile = Path.Combine(Path.GetTempPath(), "sov-conn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        await using var client = new NativeOperatorClient(new NativeClientOptions(profile, "Sovereign.Desktop", "0.1.7-shell-refresh", profile));
        await client.ConnectAsync();
        Assert.False(client.IsConnected);
        Assert.Contains("Preview fixtures", client.ConnectionReason, StringComparison.Ordinal);
        Assert.Contains("development engine", client.ConnectionReason, StringComparison.OrdinalIgnoreCase);
        Directory.Delete(profile, true);
    }

    [Fact]
    public void Installer_fails_when_required_engine_file_is_absent()
    {
        var root = Path.Combine(Path.GetTempPath(), "sov-setup-eng-" + Guid.NewGuid().ToString("N"));
        var payload = Path.Combine(root, "payload");
        var install = Path.Combine(root, "install");
        var engine = Path.Combine(payload, "engine-bundle");
        Directory.CreateDirectory(Path.Combine(engine, "python"));
        Directory.CreateDirectory(Path.Combine(engine, "native", "engine"));
        File.WriteAllText(Path.Combine(payload, "SovereignAI.DesktopPreview.exe"), "exe");
        File.WriteAllText(Path.Combine(engine, "python", "python.exe"), "py");
        File.WriteAllText(Path.Combine(engine, "native", "engine", "host_entry.py"), "entry");
        File.WriteAllText(Path.Combine(engine, "bundle-manifest.json"),
            """{"files":[{"path":"python/python.exe","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},{"path":"native/engine/host_entry.py","sha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"},{"path":"absent.dll","sha256":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"}]}""");
        var product = "SovereignAI.DesktopPreview.B4-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            var result = InstallerEngine.Run(new SetupRequest(
                SetupAction.Install, install, payload, product, product, "0.1.7", Path.Combine(root, "profile"), product, true, null, null));
            Assert.Equal(InstallerEngine.EngineVerifyFailedExitCode, result.ExitCode);
            Assert.Contains("ENGINE_CORRUPT", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }

            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + product, false);
        }
    }

    [Fact]
    public void Supervisor_profile_id_is_stable_and_distinct_per_directory()
    {
        var first = Path.Combine(Path.GetTempPath(), "sov-id-" + Guid.NewGuid().ToString("N"));
        var second = Path.Combine(Path.GetTempPath(), "sov-id-" + Guid.NewGuid().ToString("N"));
        var firstId = InstalledHostSupervisor.ProfileIdFor(first);
        var secondId = InstalledHostSupervisor.ProfileIdFor(second);
        Assert.NotEqual("desktop", firstId);
        Assert.NotEqual("grok-offline-integration", firstId);
        Assert.NotEqual(firstId, secondId);
        Assert.Equal(firstId, InstalledHostSupervisor.ProfileIdFor(first));
        Assert.Equal(InstalledHostSupervisor.ExpectedPipeName(firstId), @"\\.\pipe\SovereignAI.Offline." + firstId + ".v2");
        File.WriteAllText(Path.Combine(first, InstalledHostSupervisor.ProfileIdFileName), "desktop");
        Assert.Equal(firstId, InstalledHostSupervisor.ProfileIdFor(first));
        Directory.Delete(first, true);
        Directory.Delete(second, true);
    }

    [Fact]
    public void Verify_allows_independent_qa_installs_and_refuses_qa_staging()
    {
        Assert.False(EngineBundleVerifier.IsForbiddenEngineRoot(
            Path.Combine(Path.GetTempPath(), "qa-staging", "release-0.1.4"),
            out var staging));
        Assert.Equal("", staging);
        Assert.False(EngineBundleVerifier.IsForbiddenEngineRoot(
            Path.Combine(Path.GetTempPath(), "qa-installs", "app", "engine-bundle"),
            out var install));
        Assert.Equal("", install);
    }

    [Fact]
    public void Discovery_binds_pipe_and_profile_id_and_allows_qa_installs()
    {
        var profile = Path.Combine(Path.GetTempPath(), "sov-bind-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        var id = InstalledHostSupervisor.ProfileIdFor(profile);
        File.WriteAllText(
            Path.Combine(profile, "endpoint-v2.json"),
            """{"pipe_name":"\\\\.\\pipe\\SovereignAI.Offline.desktop.v2","host_instance_id":"host-other","profile_id":"desktop","bundle_version":"2.0.2","pid":1}""");
        Assert.Null(HostDiscovery.TryRead(profile, out var mismatch, id));
        Assert.Contains("ENDPOINT_PROFILE_MISMATCH", mismatch, StringComparison.Ordinal);
        File.WriteAllText(
            Path.Combine(profile, "endpoint-v2.json"),
            "{\"pipe_name\":" + System.Text.Json.JsonSerializer.Serialize(InstalledHostSupervisor.ExpectedPipeName(id)) +
            ",\"host_instance_id\":\"host-1\",\"profile_id\":" + System.Text.Json.JsonSerializer.Serialize(id) +
            ",\"bundle_version\":\"2.0.2\",\"pid\":1}");
        var endpoint = HostDiscovery.TryRead(profile, out var reason, id);
        Assert.Equal("ok", reason);
        Assert.NotNull(endpoint);
        Assert.Equal(id, endpoint!.ProfileId);
        Assert.Equal(InstalledHostSupervisor.ExpectedPipeName(id), endpoint.PipeName);
        Assert.True(HostDiscovery.IsAuthorizedProfile(profile, out var qaInstall));
        Assert.Equal("ok", qaInstall);
        Assert.False(HostDiscovery.IsAuthorizedProfile(@"C:\Windows\System32\drivers", out var qaStage));
        Assert.Contains("outside", qaStage, StringComparison.OrdinalIgnoreCase);
        Directory.Delete(profile, true);
    }
}
