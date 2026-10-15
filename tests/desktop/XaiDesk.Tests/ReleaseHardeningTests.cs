using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Sovereign.Desktop.Core;
using Xunit;

namespace XaiDesk.Tests;

public sealed class ReleaseHardeningTests
{
    private const string RemoteNote =
        "Tailscale mode is remote access: any device on your tailnet can reach this port. It is protected only by the access token, so keep the token secret.";

    [Fact]
    public void Tailscale_note_warns_that_the_port_is_remote_access()
    {
        Assert.Equal(RemoteNote, TailscaleRemoteAccessNote.Text);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void Tailscale_note_shows_when_the_mode_is_selected_or_available(bool selected, bool available, bool show)
    {
        Assert.Equal(show, TailscaleRemoteAccessNote.ShouldShow(selected, available));
    }

    [Fact]
    public void Phone_and_connector_settings_show_the_shared_tailscale_note()
    {
        var root = FindRepoRoot();
        var phone = File.ReadAllText(Path.Combine(root, "apps", "desktop", "Sovereign.Desktop", "PhoneSettings.cs"));
        var connector = File.ReadAllText(Path.Combine(root, "apps", "desktop", "Sovereign.Desktop", "GrokBotConnector.cs"));
        var settings = File.ReadAllText(Path.Combine(root, "apps", "desktop", "Sovereign.Desktop", "SettingsPage.cs"));

        Assert.Contains("TailscaleRemoteAccessNote.Text", phone, StringComparison.Ordinal);
        Assert.Contains("ApplyTailscaleRemoteNote(_phoneRemoteNote, _phoneBind)", phone, StringComparison.Ordinal);
        Assert.Contains("_phoneRemoteNote,", phone, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:8789", phone, StringComparison.Ordinal);
        Assert.Contains("PhoneBindMode.Tailscale", phone, StringComparison.Ordinal);

        Assert.Contains("TailscaleRemoteAccessNote.Text", connector, StringComparison.Ordinal);
        Assert.Contains("ApplyTailscaleRemoteNote(_connectorRemoteNote, _connectorBind)", connector, StringComparison.Ordinal);
        Assert.Contains("_connectorRemoteNote,", connector, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:8790", connector, StringComparison.Ordinal);
        Assert.Contains("ConnectorBindMode.Tailscale", connector, StringComparison.Ordinal);

        Assert.Contains("note.Text = TailscaleRemoteAccessNote.Text", settings, StringComparison.Ordinal);
        Assert.Contains("TailscaleRemoteAccessNote.ShouldShow(selected, offered)", settings, StringComparison.Ordinal);
        Assert.Contains("\"Tailscale only\"", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_publish_disables_symbols_and_strips_pdb_before_zip()
    {
        var root = FindRepoRoot();
        var script = File.ReadAllText(Path.Combine(root, "scripts", "publish-release.ps1"));
        var props = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"));
        var remover = File.ReadAllText(Path.Combine(root, "scripts", "Remove-ReleaseSymbols.ps1"));

        Assert.Contains("-p:SovereignReleasePublish=true", script, StringComparison.Ordinal);
        Assert.Contains("-p:DebugType=none", script, StringComparison.Ordinal);
        Assert.Contains("-p:DebugSymbols=false", script, StringComparison.Ordinal);
        Assert.Contains("-p:CopyOutputSymbolsToPublishDirectory=false", script, StringComparison.Ordinal);

        var publishes = Regex.Matches(script, @"& dotnet publish\b");
        Assert.True(publishes.Count == 3, "expected 3 publish commands, found " + publishes.Count);
        foreach (Match publish in publishes)
        {
            var lineEnd = script.IndexOf('\n', publish.Index);
            var line = script[publish.Index..lineEnd];
            Assert.Contains("@releasePublish", line, StringComparison.Ordinal);
        }

        var scan = script.IndexOf("& $scanPath", StringComparison.Ordinal);
        var strip = script.IndexOf("Remove-ReleaseSymbols.ps1", StringComparison.Ordinal);
        var zip = script.IndexOf("[System.IO.Compression.ZipFile]::CreateFromDirectory($publishDir, $zipPath)", StringComparison.Ordinal);
        Assert.True(scan > 0 && strip > scan && zip > strip, "symbols are removed after the public scan and before the zip");
        Assert.Contains("SIDesk-{0}-win-x64.zip", script, StringComparison.Ordinal);
        Assert.Contains("$_.Extension -eq '.pdb'", remover, StringComparison.Ordinal);
        Assert.Contains("Release output still contains", remover, StringComparison.Ordinal);

        Assert.Contains("'$(Configuration)' == 'Release' and '$(SovereignReleasePublish)' == 'true'", props, StringComparison.Ordinal);
        Assert.Contains("<DebugType>none</DebugType>", props, StringComparison.Ordinal);
        Assert.Contains("<DebugSymbols>false</DebugSymbols>", props, StringComparison.Ordinal);
        Assert.Contains("<CopyOutputSymbolsToPublishDirectory>false</CopyOutputSymbolsToPublishDirectory>", props, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(props, "<DebugType>"));

        var publishStep = workflow.IndexOf("./scripts/publish-release.ps1", StringComparison.Ordinal);
        var hash = workflow.IndexOf("Get-FileHash", StringComparison.Ordinal);
        var body = workflow.IndexOf("release-body.md", StringComparison.Ordinal);
        Assert.True(publishStep > 0 && hash > publishStep && body > hash);
        Assert.Contains("-Algorithm SHA256", workflow, StringComparison.Ordinal);
        Assert.Contains("SHA-256 of SIDesk-", workflow, StringComparison.Ordinal);
        Assert.Contains("body_path: release-body.md", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Remove_release_symbols_drops_pdb_files_and_keeps_binaries()
    {
        var shell = FindPowerShell();
        if (shell is null)
            throw new InvalidOperationException("PowerShell (pwsh or powershell) was not on PATH.");

        var root = Directory.CreateTempSubdirectory("si-desk-symbols");
        var outside = Directory.CreateTempSubdirectory("si-desk-symbols-outside");
        try
        {
            var nested = Directory.CreateDirectory(Path.Combine(root.FullName, "runtimes", "win-x64"));
            File.WriteAllText(Path.Combine(root.FullName, "SIDesk.exe"), "exe");
            File.WriteAllText(Path.Combine(root.FullName, "SIDesk.pdb"), "app-pdb");
            File.WriteAllText(Path.Combine(root.FullName, "Sovereign.Desktop.Phone.pdb"), "phone-pdb");
            var upper = Path.Combine(root.FullName, "SIDesk.PDB");
            if (!string.Equals(Path.GetFullPath(upper), Path.GetFullPath(Path.Combine(root.FullName, "SIDesk.pdb")), StringComparison.Ordinal))
                File.WriteAllText(upper, "upper-pdb");
            File.WriteAllText(Path.Combine(root.FullName, "notes.pdb.txt"), "not-a-pdb");
            File.WriteAllText(Path.Combine(nested.FullName, "foo.dll"), "dll");
            File.WriteAllText(Path.Combine(nested.FullName, "foo.pdb"), "nested-pdb");
            var outsidePdb = Path.Combine(outside.FullName, "keep.pdb");
            File.WriteAllText(outsidePdb, "keep");

            var script = Path.Combine(FindRepoRoot(), "scripts", "Remove-ReleaseSymbols.ps1");
            var removed = Run(shell, script, root.FullName);
            Assert.True(removed.Code == 0, removed.Error + removed.Output);
            Assert.False(File.Exists(Path.Combine(root.FullName, "SIDesk.pdb")));
            Assert.False(File.Exists(Path.Combine(root.FullName, "Sovereign.Desktop.Phone.pdb")));
            Assert.False(File.Exists(Path.Combine(root.FullName, "SIDesk.PDB")));
            var leftover = Directory.EnumerateFiles(root.FullName, "*", SearchOption.AllDirectories)
                .Where(path => path.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Assert.Empty(leftover);
            Assert.Equal("exe", File.ReadAllText(Path.Combine(root.FullName, "SIDesk.exe")));
            Assert.Equal("dll", File.ReadAllText(Path.Combine(nested.FullName, "foo.dll")));
            Assert.Equal("not-a-pdb", File.ReadAllText(Path.Combine(root.FullName, "notes.pdb.txt")));
            Assert.Equal("keep", File.ReadAllText(outsidePdb));

            var zipPath = Path.Combine(outside.FullName, "SIDesk-test-win-x64.zip");
            ZipFile.CreateFromDirectory(root.FullName, zipPath);
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                Assert.Contains(zip.Entries, entry => entry.FullName.EndsWith("SIDesk.exe", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(zip.Entries, entry => entry.FullName.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase));
            }

            var again = Run(shell, script, root.FullName);
            Assert.Equal(0, again.Code);

            var missing = Run(shell, script, Path.Combine(root.FullName, "missing"));
            Assert.NotEqual(0, missing.Code);
            Assert.Contains("Publish directory was not found", missing.Error + missing.Output, StringComparison.Ordinal);

            var notDir = Run(shell, script, Path.Combine(root.FullName, "SIDesk.exe"));
            Assert.NotEqual(0, notDir.Code);

            var blank = Run(shell, script, " ");
            Assert.NotEqual(0, blank.Code);
            Assert.Contains("Publish directory is required", blank.Error + blank.Output, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
            outside.Delete(recursive: true);
        }
    }

    private readonly record struct ShellResult(int Code, string Output, string Error);

    private static ShellResult Run(string shell, string script, string directory)
    {
        var start = new ProcessStartInfo(shell)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("-Directory");
        start.ArgumentList.Add(directory);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(20000), "PowerShell did not finish.");
        return new ShellResult(process.ExitCode, stdout.Result, stderr.Result);
    }

    private static string? FindPowerShell()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in new[] { "pwsh", "pwsh.exe", "powershell", "powershell.exe" })
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
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

        throw new DirectoryNotFoundException("No Directory.Build.props above the test output contains apps\\desktop.");
    }
}
