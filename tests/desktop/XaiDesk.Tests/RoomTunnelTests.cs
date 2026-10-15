using Sovereign.Desktop.Client;
using Xunit;

namespace XaiDesk.Tests;

public sealed class RoomTunnelTests
{
    const string PinnedVersion = "2026.9.1";
    const string PinnedSha256 = "2837888cc0f5d58f15b6dc478376de90b4d3ba5241c7947455d1e0a0df429712";
    const string MissingMessage = "Online rooms need the room tunnel (cloudflared.exe), which is missing from this install. Reinstall or update SI Desk.";
    const string HashMismatchMessage = "Room tunnel hash mismatch. Repair the app.";

    [Fact]
    public async Task Missing_tunnel_executable_names_the_room_tunnel_instead_of_the_path()
    {
        var install = Path.Combine(Path.GetTempPath(), "si-desk-tunnel-" + Guid.NewGuid().ToString("N"));
        var profile = Path.Combine(install, "profile");
        try
        {
            using var tunnel = new RoomTunnel();
            var missing = await Assert.ThrowsAsync<IOException>(() => tunnel.StartAsync(install, profile, new Uri("http://127.0.0.1:9/"), CancellationToken.None));
            Assert.Equal(MissingMessage, missing.Message);
            Assert.DoesNotContain("Could not find file", missing.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(install, missing.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(profile));

            Directory.CreateDirectory(Path.Combine(install, "room-tunnel"));
            var missingFile = await Assert.ThrowsAsync<IOException>(() => tunnel.StartAsync(install, profile, new Uri("http://127.0.0.1:9/"), CancellationToken.None));
            Assert.Equal(MissingMessage, missingFile.Message);
        }
        finally
        {
            if (Directory.Exists(install)) Directory.Delete(install, recursive: true);
        }
    }

    [Fact]
    public async Task Wrong_tunnel_bytes_keep_the_hash_mismatch_message()
    {
        var install = Path.Combine(Path.GetTempPath(), "si-desk-tunnel-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(install, "room-tunnel");
        Directory.CreateDirectory(folder);
        var executable = Path.Combine(folder, "cloudflared.exe");
        await File.WriteAllBytesAsync(executable, [1, 2, 3, 4]);
        var profile = Path.Combine(install, "profile");
        try
        {
            using var tunnel = new RoomTunnel();
            var mismatch = await Assert.ThrowsAsync<IOException>(() => tunnel.StartAsync(install, profile, new Uri("http://127.0.0.1:9/"), CancellationToken.None));
            Assert.Equal(HashMismatchMessage, mismatch.Message);
            Assert.False(Directory.Exists(profile));
        }
        finally
        {
            Directory.Delete(install, recursive: true);
        }
    }

    [Fact]
    public void Release_pin_matches_the_runtime_hash_and_the_publish_checks()
    {
        var root = FindRepoRoot();
        var pin = File.ReadAllLines(Path.Combine(root, "apps", "desktop", "room-tunnel", "cloudflared.pin"));
        Assert.Contains("version=" + PinnedVersion, pin);
        Assert.Contains("asset=cloudflared-windows-amd64.exe", pin);
        Assert.Contains("sha256=" + PinnedSha256, pin);

        var tunnel = File.ReadAllText(Path.Combine(root, "apps", "desktop", "Sovereign.Desktop.Client", "RoomTunnel.cs"));
        Assert.Contains(PinnedSha256, tunnel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(PinnedVersion, tunnel, StringComparison.Ordinal);
        Assert.Contains(MissingMessage, tunnel, StringComparison.Ordinal);
        Assert.Contains(HashMismatchMessage, tunnel, StringComparison.Ordinal);

        var publish = File.ReadAllText(Path.Combine(root, "scripts", "publish-release.ps1"));
        var fetchAt = publish.IndexOf("Get-Cloudflared.ps1", StringComparison.Ordinal);
        var publishAt = publish.IndexOf("dotnet publish", StringComparison.Ordinal);
        Assert.True(fetchAt >= 0 && publishAt > fetchAt);
        Assert.Contains("-VerifyOnly", publish, StringComparison.Ordinal);

        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"));
        Assert.Contains("Get-Cloudflared.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("Get-Cloudflared.Tests.ps1", workflow, StringComparison.Ordinal);

        var targets = File.ReadAllText(Path.Combine(root, "apps", "desktop", "Sovereign.Desktop", "RoomTunnel.targets"));
        Assert.Contains("EnsureRoomTunnelBinary", targets, StringComparison.Ordinal);
        Assert.Contains("'$(Configuration)' == 'Release'", targets, StringComparison.Ordinal);
        Assert.Contains("cloudflared.exe", targets, StringComparison.Ordinal);
        Assert.Contains("<Error ", targets, StringComparison.Ordinal);
        Assert.Contains("ROOMTUNNEL001", targets, StringComparison.Ordinal);

        var notice = File.ReadAllText(Path.Combine(root, "apps", "desktop", "room-tunnel", "NOTICE.txt"));
        Assert.Contains(PinnedVersion, notice, StringComparison.Ordinal);
        Assert.Contains(PinnedSha256, notice, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))
                && Directory.Exists(Path.Combine(dir.FullName, "apps", "desktop")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("No Directory.Build.props above the test output contains apps/desktop.");
    }
}
