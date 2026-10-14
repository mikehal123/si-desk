using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Sovereign.Desktop.Updates;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class UpdateTests
{
    [Fact] public void Displayed_version_matches_binary_version_used_by_updater()
    {
        var assembly = typeof(Sovereign.Desktop.Presentation.ShellViewModel).Assembly;
        var binary = System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly.Location);
        var displayed = Sovereign.Desktop.Presentation.ShellViewModel.AppVersion;
        Assert.Equal(displayed.Split('-')[0] + ".0", binary.FileVersion);
        Assert.Equal(binary.ProductVersion!.Split('+')[0], displayed);
        _ = UpdatePackage.ParseVersion(displayed.Split('-')[0]);
    }
    private static UpdateRelease Release => new("0.3.19", "https://github.com/mikehal123/si-desk/releases/download/v0.3.19/SIDesk-Windows-0.3.19.zip", new string('a',64), 100, "Test", DateTimeOffset.UtcNow.AddDays(1));
    private static string Sign(UpdateRelease release, RSA rsa)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(release);
        return JsonSerializer.Serialize(new SignedUpdate(Convert.ToBase64String(data), Convert.ToBase64String(rsa.SignData(data,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1))));
    }
    [Fact] public void Valid_signature_and_newer_version_are_accepted()
    {
        using var rsa=RSA.Create(2048); var release=Release;
        Assert.Equal(release,UpdatePackage.Verify(Sign(release,rsa),Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()),DateTimeOffset.UtcNow));
        Assert.True(UpdatePackage.ParseVersion("0.3.19") > UpdatePackage.ParseVersion("0.3.9"));
    }
    [Fact] public void Tampering_and_wrong_key_are_rejected()
    {
        using var rsa=RSA.Create(2048); using var other=RSA.Create(2048);
        var envelope=Sign(Release,rsa); var key=Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
        Assert.Throws<InvalidDataException>(()=>UpdatePackage.Verify(envelope,Convert.ToBase64String(other.ExportSubjectPublicKeyInfo()),DateTimeOffset.UtcNow));
        var signed=JsonSerializer.Deserialize<SignedUpdate>(envelope)!;
        var tampered=signed with {Payload=Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(Release with {Version="9.0.0"}))};
        Assert.Throws<InvalidDataException>(()=>UpdatePackage.Verify(JsonSerializer.Serialize(tampered),key,DateTimeOffset.UtcNow));
    }
    [Fact] public void Expired_or_foreign_feed_and_invalid_sizes_are_rejected()
    {
        using var rsa=RSA.Create(2048); var key=Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
        foreach(var release in new[]{Release with {ExpiresUtc=DateTimeOffset.UtcNow.AddDays(-1)},Release with {Url="https://example.com/update.zip"},Release with {Bytes=0},Release with {Version="0.3.19-beta"}})
            Assert.Throws<InvalidDataException>(()=>UpdatePackage.Verify(Sign(release,rsa),key,DateTimeOffset.UtcNow));
    }
    [Fact] public async Task Corrupt_download_is_rejected()
    {
        var path=Path.GetTempFileName();
        try { await File.WriteAllTextAsync(path,"broken"); await Assert.ThrowsAsync<InvalidDataException>(()=>UpdatePackage.VerifyArchiveAsync(path,Release)); }
        finally { File.Delete(path); }
    }
    [Theory]
    [InlineData("SIDesk-Windows-0.3.19/../escape.txt")]
    [InlineData("SIDesk-Windows-0.3.19/payload/x:stream")]
    [InlineData("wrong-root/file")]
    public void Unsafe_zip_is_rejected_before_extracting(string name)
    {
        var temp=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try {
            var zip=Path.Combine(temp,"test.zip");using(var archive=ZipFile.Open(zip,ZipArchiveMode.Create)) {archive.CreateEntry(name);archive.CreateEntry("SIDesk-Windows-0.3.19/SovereignAI.ps1");}
            Assert.Throws<InvalidDataException>(()=>UpdatePackage.Extract(zip,Path.Combine(temp,"output"),"0.3.19"));
            Assert.False(Directory.Exists(Path.Combine(temp,"output")));
        } finally {Directory.Delete(temp,true);}
    }
    [Fact] public void Duplicate_case_insensitive_paths_are_rejected()
    {
        var temp=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try {
            var zip=Path.Combine(temp,"test.zip");using(var archive=ZipFile.Open(zip,ZipArchiveMode.Create)) {archive.CreateEntry("SIDesk-Windows-0.3.19/SovereignAI.ps1");archive.CreateEntry("SIDesk-Windows-0.3.19/sovereignai.ps1");}
            Assert.Throws<InvalidDataException>(()=>UpdatePackage.Extract(zip,Path.Combine(temp,"output"),"0.3.19"));
        } finally {Directory.Delete(temp,true);}
    }
    [Fact] public void Source_checkout_cannot_be_updated()
    {
        Assert.Throws<IOException>(()=>UpdatePaths.ValidateInstall(@"C:\Windows\System32",Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
    }
}
