using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Sovereign.Desktop.SetupLib;

internal static class Program
{
    private const string ZipHash = "f2d20ea5ee803f40e1bc2f1b43ce064d43fa5a0747165127bbead45c45a09324";
    private const string ExeHash = "a05f706fa7d48e85c09a00cb3cae1afde8f73853f0b632c4ec7767f0b9bef9d0";
    private const string Instance = "qa-cw-guestcandidate";
    private const string Version = "0.3.4-cw-candidate-20260921";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.SequenceEqual(new[] { "--verify-only" }))
        {
            using var zip = OpenVerifiedArchive();
            VerifyEntries(zip);
            return 0;
        }
        if (args.Length != 0) return 2;
        ApplicationConfiguration.Initialize();
        using var form = new Form { Text = "xAI Desk · isolated QA candidate", Width = 620, Height = 270, StartPosition = FormStartPosition.CenterScreen };
        var status = new Label { Left = 24, Top = 24, Width = 555, Height = 110,
            Text = "Install the verified September 21 review candidate into its own QA profile.\n\nYour main app and the original guest QA installation remain unchanged.\nNo sign-in, model download or scan is performed." };
        var install = new Button { Text = "Install QA candidate", Left = 24, Top = 155, Width = 200, Height = 36 };
        form.Controls.AddRange([status, install]);
        var busy = false;
        form.FormClosing += (_, e) => { if (busy) e.Cancel = true; };
        install.Click += async (_, _) =>
        {
            busy = true;
            install.Enabled = false;
            try
            {
                var progress = new Progress<string>(s => status.Text = s);
                var executable = await Task.Run(() => Install(progress));
                status.Text = "Installed and verified. Opening the candidate on Capabilities.\n\nThe installation receipt is saved in the QA instance folder.";
                var profile = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(executable))!, "profile");
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
                foreach (var arg in new[] { "--mode", "native", "--profile", profile }) start.ArgumentList.Add(arg);
                Process.Start(start);
                install.Text = "Installed";
            }
            catch (Exception ex)
            {
                status.Text = "Installation stopped: " + ex.Message;
                install.Text = "Installation stopped";
            }
            finally { busy = false; }
        };
        Application.Run(form);
        return 0;
    }

    private static ZipArchive OpenVerifiedArchive()
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("candidate.zip") ?? throw new IOException("Embedded candidate missing.");
        if (Convert.ToHexStringLower(SHA256.HashData(stream)) != ZipHash)
        {
            stream.Dispose();
            throw new IOException("Candidate archive hash mismatch.");
        }
        stream.Position = 0;
        return new ZipArchive(stream, ZipArchiveMode.Read);
    }

    private static void VerifyEntries(ZipArchive archive)
    {
        using var manifest = JsonDocument.Parse(archive.GetEntry("files.json")!.Open());
        foreach (var row in manifest.RootElement.EnumerateArray())
        {
            var name = row.GetProperty("path").GetString()!.Replace('\\', '/');
            var entry = archive.GetEntry(name) ?? throw new IOException("Missing package file: " + name);
            using var stream = entry.Open();
            if (entry.Length != row.GetProperty("bytes").GetInt64() || Convert.ToHexStringLower(SHA256.HashData(stream)) != row.GetProperty("sha256").GetString())
                throw new IOException("Package file mismatch: " + name);
        }
    }

    private static string Install(IProgress<string> progress)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SovereignAI.DesktopPreview", Instance);
        if (Directory.Exists(root)) throw new IOException("This QA instance already exists; no files were replaced.");
        progress.Report("Verifying the embedded candidate archive…");
        using var archive = OpenVerifiedArchive();
        VerifyEntries(archive);
        var extract = Path.Combine(Path.GetTempPath(), "sai-qa-candidate-" + Guid.NewGuid().ToString("N"));
        progress.Report("Extracting the verified package. This may take a few minutes…");
        archive.ExtractToDirectory(extract);
        var app = Path.Combine(root, "app");
        var profile = Path.Combine(root, "profile");
        progress.Report("Installing into the isolated QA instance…");
        var result = InstallerEngine.Run(new SetupRequest(SetupAction.Install, app, Path.Combine(extract, "payload"),
            "SovereignAI.DesktopPreview." + Instance, "xAI Desk " + Instance, Version, profile,
            "xAI Desk " + Instance, true, null, null));
        if (!result.Ok) throw new IOException(result.Message);
        Directory.CreateDirectory(profile);
        var executable = Path.Combine(app, "SovereignAI.DesktopPreview.exe");
        using var installed = File.OpenRead(executable);
        var installedHash = Convert.ToHexStringLower(SHA256.HashData(installed));
        if (installedHash != ExeHash) throw new IOException("Installed executable hash mismatch.");
        File.WriteAllText(Path.Combine(root, "candidate-install-receipt.json"), JsonSerializer.Serialize(new
        {
            version = Version, instance = Instance, installedAt = DateTimeOffset.UtcNow,
            archiveSha256 = ZipHash, executableSha256 = installedHash, app, profile, log = result.Log
        }, new JsonSerializerOptions { WriteIndented = true }));
        return executable;
    }
}
