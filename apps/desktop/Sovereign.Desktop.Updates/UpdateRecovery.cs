using System.Diagnostics;

namespace Sovereign.Desktop.Updates;

/// <summary>Restores app directories without deleting either binary or writing profile data.</summary>
public static class UpdateRecovery
{
    public static async Task RestoreAsync(string install, string previous, string failed,
        string profile, string profileFingerprint, List<string> log, TimeSpan? renameTimeout = null)
    {
        // Check compatibility before moving any directory. A changed database requires explicit recovery.
        DeskUpdateSafety.RequireUnchangedProfile(profile, profileFingerprint);
        DeskUpdateSafety.CheckNoLinks(install);
        DeskUpdateSafety.CheckNoLinks(previous);
        DeskUpdateSafety.CheckNoLinks(failed);
        if (!Directory.Exists(previous)) throw new IOException("Previous app directory is missing: " + previous);
        if (Directory.Exists(failed) || File.Exists(failed)) throw new IOException("Failed-app preservation path already exists: " + failed);
        if (Directory.Exists(install)) await MoveAsync(install, failed, log, renameTimeout);
        await MoveAsync(previous, install, log, renameTimeout);
        log.Add("Previous app directory restored; failed candidate preserved at " + failed);
    }

    // Testing individual DLL handles cannot prove a Windows directory can be renamed. Retry the actual
    // operation: crash reporting, antivirus and a terminating worker can still hold a directory open.
    public static async Task MoveAsync(string source, string destination, List<string> log, TimeSpan? timeout = null)
    {
        var budget = timeout ?? TimeSpan.FromSeconds(30);
        var watch = Stopwatch.StartNew();
        var retries = 0;
        while (true)
        {
            try
            {
                Directory.Move(source, destination);
                if (retries > 0) log.Add($"Directory rename succeeded after {retries} retries: {source} -> {destination}");
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (!Directory.Exists(source) || Directory.Exists(destination) || File.Exists(destination) || watch.Elapsed >= budget)
                    throw new IOException($"Could not rename {source} to {destination} after {watch.Elapsed.TotalSeconds:F1}s. Source files retained. {error.Message}", error);
                if (retries++ == 0) log.Add("Waiting for directory rename handles: " + source + "; " + error.Message);
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(250, Math.Max(1, (budget - watch.Elapsed).TotalMilliseconds))));
            }
        }
    }
}
