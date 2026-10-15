using Sovereign.Desktop.Client;
using Xunit;

namespace XaiDesk.Tests;

public sealed class AccountSignInPolicyTests
{
    // Split so this file does not contain the removed development-instance strings.
    private static readonly string[] RemovedDevMarkers =
    [
        "accounts" + ".dev",
        "joint-ram" + "-13",
        "R5U05yl" + "Kr8lRDs6E"
    ];

    [Fact]
    public void Startup_allows_a_missing_account_config_and_ignores_a_leftover_file()
    {
        Assert.False(AccountSignInPolicy.Inspect(null).Enabled);
        Assert.False(AccountSignInPolicy.Inspect("").ConfigPresent);
        Assert.False(AccountSignInPolicy.Inspect("   ").Enabled);

        var dir = Path.Combine(Path.GetTempPath(), "si-desk-no-account-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var missing = AccountSignInPolicy.Inspect(dir);
            Assert.False(missing.Enabled);
            Assert.False(missing.ConfigPresent);

            File.WriteAllText(Path.Combine(dir, AccountSignInPolicy.ConfigFileName),
                """{"issuer":"https://account.example","clientId":"not-used"}""");
            var leftover = AccountSignInPolicy.Inspect(dir);
            Assert.False(leftover.Enabled);
            Assert.True(leftover.ConfigPresent);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact(Timeout = 20000)]
    public async Task Shared_room_starts_without_an_account_config()
    {
        var profile = Path.Combine(Path.GetTempPath(), "si-desk-room-no-account-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        SharedRoomHost? host = null;
        try
        {
            Assert.False(File.Exists(Path.Combine(profile, AccountSignInPolicy.ConfigFileName)));
            Assert.False(AccountSignInPolicy.Inspect(profile).Enabled);
            host = new SharedRoomHost(profile, "Ada");
            await host.StartAsync();
            using var guest = await RoomConnection.JoinAsync(host.NewInvitation(host.Address), "Bea", CancellationToken.None);
            Assert.Equal("Bea", guest.Session.Name);
            Assert.Null(guest.Session.AccountSubject);
        }
        finally
        {
            if (host is not null) await host.DisposeAsync();
            try
            {
                if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public void Shipped_sources_omit_the_removed_dev_account()
    {
        var root = RepoRoot();
        var desktop = Path.Combine(root, "apps", "desktop", "Sovereign.Desktop");
        Assert.False(File.Exists(Path.Combine(desktop, AccountSignInPolicy.ConfigFileName)));

        var csproj = File.ReadAllText(Path.Combine(desktop, "Sovereign.Desktop.csproj"));
        Assert.DoesNotContain(AccountSignInPolicy.ConfigFileName, csproj, StringComparison.Ordinal);
        var publish = File.ReadAllText(Path.Combine(root, "scripts", "publish-release.ps1"));
        Assert.DoesNotContain(AccountSignInPolicy.ConfigFileName, publish, StringComparison.Ordinal);

        var accountPage = File.ReadAllText(Path.Combine(desktop, "AccountPage.cs"));
        Assert.Contains("AccountSignInPolicy.Inspect", accountPage, StringComparison.Ordinal);
        Assert.DoesNotContain("SovereignAccountClient", accountPage, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowAccountDialog", accountPage, StringComparison.Ordinal);
        Assert.DoesNotContain("PasswordVault", accountPage, StringComparison.Ordinal);

        var shell = File.ReadAllText(Path.Combine(desktop, "ShellDesign.cs"));
        Assert.DoesNotContain("ShowAccountDialog", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Your SI Desk account", shell, StringComparison.Ordinal);

        var hits = new List<string>();
        foreach (var file in EnumerateTextFiles(root))
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var marker in RemovedDevMarkers)
            {
                if (text.Contains(marker, StringComparison.Ordinal))
                {
                    hits.Add(Path.GetRelativePath(root, file));
                    break;
                }
            }
        }
        Assert.Empty(hits);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var project = Path.Combine(dir.FullName, "apps", "desktop", "Sovereign.Desktop", "Sovereign.Desktop.csproj");
            var publish = Path.Combine(dir.FullName, "scripts", "publish-release.ps1");
            if (File.Exists(project) && File.Exists(publish)) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repo root was not found from the test output directory.");
    }

    private static IEnumerable<string> EnumerateTextFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (name is ".git" or "bin" or "obj" or "artifacts" or "node_modules" or ".vs") continue;
                pending.Push(child);
            }
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files)
            {
                var ext = Path.GetExtension(file);
                if (BinaryExtensions.Contains(ext)) continue;
                FileInfo info;
                try { info = new FileInfo(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                if (info.Length > 2_000_000) continue;
                yield return file;
            }
        }
    }

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".tif", ".tiff",
        ".dll", ".exe", ".pdb", ".zip", ".nupkg", ".snupkg", ".7z", ".gz", ".tgz",
        ".so", ".dylib", ".a", ".lib", ".woff", ".woff2", ".ttf", ".otf",
        ".mp3", ".mp4", ".wav", ".pdf", ".wasm", ".bin", ".dat", ".db", ".sqlite"
    };
}
