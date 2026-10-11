using System.IO.Compression;
using System.Text.Json;
using Sovereign.Desktop.Core.Support;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class SupportExportPrivacyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Sovereign support " + Guid.NewGuid().ToString("N"));
    private readonly List<string> _links = [];
    private string Profile => Path.Combine(_root, "profile");
    public SupportExportPrivacyTests() => Directory.CreateDirectory(Profile);
    public void Dispose()
    {
        foreach (var link in _links)
            if (Directory.Exists(link)) Directory.Delete(link); else if (File.Exists(link)) File.Delete(link);
        Directory.Delete(_root, recursive: true);
    }

    private SupportExportResult Export(bool drafts = false) => SupportExporter.Export(Profile, Path.Combine(_root, "support.zip"), "0.3.34", "Desktop", drafts);
    private void Save(string relative, string content)
    {
        var path = Path.Combine(Profile, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Desktop_version_is_not_a_key_but_real_key_markers_are_refused()
    {
        Save("app-version.json", """{"version":"0.3.34-desk-foundation+12345678"}""");
        var result = SupportExporter.Export(Profile, Path.Combine(_root, "version.zip"), "0.3.34-desk-foundation", "Desktop");
        Assert.Contains("app-version.json", result.IncludedRelativePaths);
        Assert.False(result.RefusedSecretLikeContent);
        Save("preferences.json", """{"note":"sk-fake_test_key_123456789"}""");
        var guarded = Export();
        Assert.True(guarded.RefusedSecretLikeContent);
        Assert.DoesNotContain("preferences.json", guarded.IncludedRelativePaths);
        Assert.Throws<IOException>(() => SupportExporter.Export(Profile, Path.Combine(_root, "secret.zip"), "version", "Bearer fake_test_key"));
        Assert.False(File.Exists(Path.Combine(_root, "secret.zip")));
    }

    [Fact]
    public void Metadata_is_default_and_private_drafts_require_explicit_opt_in()
    {
        Save("app-version.json", """{"version":"0.3.34"}""");
        Save("drafts/case.json", """{"question":"Private valuation question"}""");
        Save("desk/private-source.txt", "Private evidence");
        var metadata = Export();
        Assert.Contains("app-version.json", metadata.IncludedRelativePaths);
        Assert.DoesNotContain("drafts/case.json", metadata.IncludedRelativePaths);
        Assert.DoesNotContain(metadata.ExcludedReasons, item => item.Contains("private-source.txt"));
        var withDrafts = Export(drafts: true);
        Assert.Contains("drafts/case.json", withDrafts.IncludedRelativePaths);
        using var archive = ZipFile.OpenRead(withDrafts.ZipPath);
        using var manifest = JsonDocument.Parse(archive.GetEntry("export-manifest.json")!.Open());
        Assert.True(manifest.RootElement.GetProperty("private_drafts_requested").GetBoolean());
        Assert.Null(archive.GetEntry("desk/private-source.txt"));
    }

    [Theory]
    [InlineData("apiKey")]
    [InlineData("access_token")]
    [InlineData("clientSecret")]
    [InlineData("Authorization")]
    [InlineData("password")]
    public void Nested_credential_properties_never_enter_the_archive(string key)
    {
        Save("preferences.json", JsonSerializer.Serialize(new { providers = new[] { new Dictionary<string, string> { [key] = "not-a-recognizable-prefix" } } }));
        Save("app-version.json", """{"version":"0.3.34"}""");
        var result = Export();
        Assert.True(result.RefusedSecretLikeContent);
        Assert.False(result.ContainsPlantedSecretMarker);
        using var archive = ZipFile.OpenRead(result.ZipPath);
        Assert.Null(archive.GetEntry("preferences.json"));
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            Assert.DoesNotContain("not-a-recognizable-prefix", reader.ReadToEnd());
        }
    }

    [Fact]
    public void Oversized_and_invalid_json_are_excluded_without_partial_contents()
    {
        Save("preferences.json", new string('x', SupportExporter.MaximumFileBytes + 1));
        Save("connection-state.json", "not valid JSON");
        Save("last-client-errors.json", """{"message":"Authorization: Bearer hidden-secret"}""");
        var result = Export();
        Assert.Equal(new[] { "export-manifest.json" }, result.IncludedRelativePaths);
        Assert.Contains(result.ExcludedReasons, value => value.Contains("size limit"));
        Assert.Contains(result.ExcludedReasons, value => value.Contains("invalid JSON"));
        using var archive = ZipFile.OpenRead(result.ZipPath);
        Assert.Single(archive.Entries);
    }

    [Fact]
    public void Draft_count_is_bounded_and_no_nested_directory_is_traversed()
    {
        for (var i = 0; i <= SupportExporter.MaximumDraftFiles; i++) Save($"drafts/{i:D3}.json", "{}");
        Save("drafts/nested/private.json", "{}");
        var result = Export(drafts: true);
        Assert.Equal(SupportExporter.MaximumDraftFiles + 1, result.IncludedRelativePaths.Count);
        Assert.Contains(result.ExcludedReasons, value => value.Contains("file count limit"));
        Assert.DoesNotContain("drafts/nested/private.json", result.IncludedRelativePaths);
    }

    [Fact]
    public void Linked_draft_folders_and_profile_roots_are_refused()
    {
        var foreign = Path.Combine(_root, "foreign");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "private.json"), """{"private":"Outside profile"}""");
        var directoryLink = Path.Combine(Profile, "drafts");
        CreateDirectoryLink(directoryLink, foreign);
        var result = Export(drafts: true);
        Assert.Equal(new[] { "export-manifest.json" }, result.IncludedRelativePaths);
        Assert.Contains(result.ExcludedReasons, value => value.Contains("linked"));
        var profileLink = Path.Combine(_root, "profile-link");
        CreateDirectoryLink(profileLink, Profile);
        Assert.Throws<IOException>(() => SupportExporter.Export(profileLink, Path.Combine(_root, "other.zip"), "version", "mode"));
        Assert.Equal("""{"private":"Outside profile"}""", File.ReadAllText(Path.Combine(foreign, "private.json")));
    }

    private void CreateDirectoryLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            // NTFS junctions exercise reparse protection without enabling Developer Mode or elevation.
            var info = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{target}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var process = System.Diagnostics.Process.Start(info)!;
            Assert.True(process.WaitForExit(10_000));
            Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, target);
        _links.Add(link);
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
    }

    [Fact]
    public void Export_cannot_overwrite_a_profile_json_file()
    {
        Save("preferences.json", """{"theme":"dark"}""");
        Assert.Throws<IOException>(() => SupportExporter.Export(Profile, Path.Combine(Profile, "preferences.json"), "version", "mode"));
        Assert.Equal("""{"theme":"dark"}""", File.ReadAllText(Path.Combine(Profile, "preferences.json")));
    }
}
