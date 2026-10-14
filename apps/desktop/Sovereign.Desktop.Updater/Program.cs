using System.Diagnostics;
using System.Text.Json;
using Sovereign.Desktop.SetupLib;
using Sovereign.Desktop.Updates;
using Microsoft.Win32;

if (args.Length != 1) return 2;
var work = Path.GetFullPath(args[0]);
string? install = null, backup = null, profile = null, baseDir = null;
string? profileFingerprint = null;
var moved = false;
var log = new List<string>();
FileStream? installLock = null;
Process? candidateApp = null;
UpdateCandidateShutdown? candidateShutdown = null;
string? registryPath = null;
var registryValues = new Dictionary<string, (object Value, RegistryValueKind Kind)>();
var shortcutBackups = new Dictionary<string, byte[]>();
try
{
    var request = JsonSerializer.Deserialize<UpdateRequest>(await File.ReadAllTextAsync(Path.Combine(work, "request.json"))) ?? throw new IOException("Missing request.");
    install = Path.GetFullPath(request.InstallDirectory).TrimEnd(Path.DirectorySeparatorChar);
    baseDir = UpdatePaths.ValidateInstall(install, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    var updates = Path.Combine(baseDir, "updates");
    if (!string.Equals(Directory.GetParent(work)?.FullName, updates, StringComparison.OrdinalIgnoreCase) ||
        !Guid.TryParseExact(Path.GetFileName(work), "N", out _)) throw new IOException("Invalid update workspace.");
    if (new DirectoryInfo(work).Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Update directory is a link.");
    // An inherited app/ working directory is itself a Windows rename lock. Use the validated,
    // sibling update workspace before either swap; the updater executable also lives here.
    Environment.CurrentDirectory = work;
    installLock = new FileStream(Path.Combine(updates, "install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    if (FileVersionInfo.GetVersionInfo(Path.Combine(install, "SovereignAI.DesktopPreview.exe")).FileVersion != request.CurrentVersion + ".0")
        throw new IOException("Installed version changed. Check for updates again.");
    profile = Path.GetFullPath(request.ProfileDirectory);
    var recordedProfile = (await File.ReadAllTextAsync(Path.Combine(baseDir, "profile-path.txt"))).Trim();
    if (!string.Equals(profile, Path.GetFullPath(recordedProfile), StringComparison.OrdinalIgnoreCase)) throw new IOException("Profile identity mismatch.");
    var release = UpdatePackage.Verify(await File.ReadAllTextAsync(Path.Combine(work, "update.json")), UpdateTrust.PublicKey, DateTimeOffset.UtcNow);
    if (UpdatePackage.ParseVersion(release.Version) <= UpdatePackage.ParseVersion(request.CurrentVersion)) throw new IOException("Update is not newer.");
    await UpdatePackage.VerifyArchiveAsync(Path.Combine(work, "update.zip"), release);
    UpdatePackage.Extract(Path.Combine(work, "update.zip"), Path.Combine(work, "package"), release.Version);
    DeskUpdateSafety.VerifyCompatibility(Path.Combine(work, "package", "payload"), release.Version, request.DeskSnapshot?.SchemaVersion ?? 2);
    try
    {
        using var old = Process.GetProcessById(request.ProcessId);
        if (old.StartTime.ToUniversalTime().Ticks != request.ProcessStartTicks || !string.Equals(old.MainModule?.FileName, Path.Combine(install, "SovereignAI.DesktopPreview.exe"), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Running app identity changed.");
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await old.WaitForExitAsync(wait.Token);
    }
    catch (ArgumentException) { /* Original app already exited. */ }
    var shutdown = InstalledHostShutdown.Coordinate(install, profile, log);
    if (shutdown is not null) throw new IOException(shutdown.Message);
    if (request.DeskSnapshot is { } snapshot)
    {
        DeskUpdateSafety.VerifySnapshot(profile, release.Version, snapshot);
        log.Add($"Verified full lookup backup: schema {snapshot.SchemaVersion}, write generation {snapshot.WriteGeneration}, manifest {snapshot.ManifestSha256}.");
    }
    else if (File.Exists(Path.Combine(profile, "desk", "desk.sqlite")))
        throw new IOException("Lookup profile has no verified pre-update backup. Reopen the app and prepare this update again.");
    profileFingerprint = DeskUpdateSafety.FingerprintClosedProfile(profile);
    await File.WriteAllTextAsync(Path.Combine(work, "desk-before-swap.json"), JsonSerializer.Serialize(new
        { profile_fingerprint = profileFingerprint, backup = request.DeskSnapshot }));
    var instance = Path.GetFileName(baseDir);
    registryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SovereignAI.DesktopPreview." + instance;
    using (var key = Registry.CurrentUser.OpenSubKey(registryPath))
        if (key is not null) foreach (var name in key.GetValueNames()) if (key.GetValue(name) is { } value) registryValues[name] = (value, key.GetValueKind(name));
    var productName = instance == "main" ? "SI Desk" : "SI Desk " + instance;
    var menu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", productName);
    foreach (var path in new[] { Path.Combine(menu, productName + ".lnk"), Path.Combine(menu, productName + ".cmd"), instance == "main" ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "SI Desk.lnk") : "" })
        if (File.Exists(path)) shortcutBackups[path] = await File.ReadAllBytesAsync(path);
    backup = Path.Combine(baseDir, "previous-" + Guid.NewGuid().ToString("N"));
    await UpdateRecovery.MoveAsync(install, backup, log); moved = true;
    var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    // An updater launched from PowerShell 7 must not give Windows PowerShell its module search path.
    start.Environment["PSModulePath"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "Modules");
    foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(work, "package", "SovereignAI.ps1"), "-Action", "Install", "-Instance", Path.GetFileName(baseDir) }) start.ArgumentList.Add(argument);
    using var setup = Process.Start(start) ?? throw new IOException("Could not start installer.");
    var stdout = setup.StandardOutput.ReadToEndAsync(); var stderr = setup.StandardError.ReadToEndAsync();
    await setup.WaitForExitAsync(); log.Add(await stdout); log.Add(await stderr);
    if (setup.ExitCode != 0) throw new IOException("Installer failed with exit code " + setup.ExitCode);
    var installed = FileVersionInfo.GetVersionInfo(Path.Combine(install, "SovereignAI.DesktopPreview.exe"));
    if (installed.FileVersion != release.Version + ".0") throw new IOException("Installed version mismatch.");
    var launch = new ProcessStartInfo(Path.Combine(install, "SovereignAI.DesktopPreview.exe")) { UseShellExecute = false, WorkingDirectory = install };
    launch.ArgumentList.Add("--mode"); launch.ArgumentList.Add("native"); launch.ArgumentList.Add("--profile"); launch.ArgumentList.Add(profile);
    var readyPath = Path.Combine(work, "ready.txt");
    if (File.Exists(readyPath)) File.Delete(readyPath);
    launch.Environment["SOVEREIGN_UPDATE_READY"] = readyPath;
    var launchStarted = DateTime.UtcNow;
    candidateApp = Process.Start(launch) ?? throw new IOException("Updated app did not start.");
    candidateShutdown = new UpdateCandidateShutdown(candidateApp, install, launchStarted);
    for (var i = 0; i < 90 && !candidateApp.HasExited && !File.Exists(readyPath); i++)
    {
        candidateShutdown.Observe();
        await Task.Delay(1000);
    }
    if (candidateApp.HasExited || !File.Exists(readyPath) || (await File.ReadAllTextAsync(readyPath)).Trim().Split('-')[0] != release.Version)
        throw new IOException("Updated app did not confirm startup." + (candidateApp.HasExited ? " Exit code: " + candidateApp.ExitCode + "." : ""));
    await Task.Delay(2000);
    candidateShutdown.Observe();
    if (candidateApp.HasExited) throw new IOException("Updated app exited immediately after startup confirmation. Exit code: " + candidateApp.ExitCode + ".");
    await File.WriteAllTextAsync(Path.Combine(baseDir, "update-result.txt"), "Updated successfully to " + release.Version + ". Previous app retained at " + backup);
    await File.WriteAllLinesAsync(Path.Combine(work, "install.log"), log);
    return 0;
}
catch (Exception error)
{
    log.Add(error.ToString());
    var message = "Update did not complete: " + error.Message;
    var restartSafe = !moved;
    try
    {
        if (moved && install is not null && backup is not null)
        {
            restartSafe = false;
            // A crashed UI may leave desk workers behind. Retain the launched process identity through
            // this catch and stop its tracked tree before asking the host to close and renaming app/.
            if (candidateShutdown is not null) await candidateShutdown.StopAsync(log);
            var shutdown = InstalledHostShutdown.Coordinate(install, profile, log);
            if (shutdown is not null) throw new IOException("Cannot restore while installed processes remain: " + shutdown.Message);
            if (profile is null || profileFingerprint is null) throw new IOException("Cannot prove profile compatibility for rollback.");
            await UpdateRecovery.RestoreAsync(install, backup, Path.Combine(work, "failed-install"), profile, profileFingerprint, log);
            if (registryPath is not null)
            {
                using var key = Registry.CurrentUser.CreateSubKey(registryPath);
                foreach (var (name, entry) in registryValues) key.SetValue(name, entry.Value, entry.Kind);
            }
            foreach (var (path, bytes) in shortcutBackups) await File.WriteAllBytesAsync(path, bytes);
            message += " Previous app restored; profile preserved.";
            restartSafe = true;
        }
        if (baseDir is not null) await File.WriteAllTextAsync(Path.Combine(baseDir, "update-result.txt"), message);
        if (restartSafe && install is not null && profile is not null && File.Exists(Path.Combine(install, "SovereignAI.DesktopPreview.exe")))
        {
            var launch = new ProcessStartInfo(Path.Combine(install, "SovereignAI.DesktopPreview.exe")) { UseShellExecute = false };
            launch.ArgumentList.Add("--mode"); launch.ArgumentList.Add("native"); launch.ArgumentList.Add("--profile"); launch.ArgumentList.Add(profile);
            Process.Start(launch);
        }
    }
    catch (Exception recoveryError)
    {
        message += " Recovery stopped: " + recoveryError.Message + " Previous app retained at " + backup;
        log.Add("Recovery: " + recoveryError);
        if (baseDir is not null) await File.WriteAllTextAsync(Path.Combine(baseDir, "update-result.txt"), message);
    }
    if (Directory.Exists(work)) await File.WriteAllLinesAsync(Path.Combine(work, "install.log"), log);
    return 1;
}
finally { candidateApp?.Dispose(); installLock?.Dispose(); }
