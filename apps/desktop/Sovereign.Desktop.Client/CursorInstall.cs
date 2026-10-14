using System.Diagnostics;

namespace Sovereign.Desktop.Client;

/// <summary>
/// Connection-flyout install for the pinned Cursor agent package.
/// Runs setup\Install-CursorConnection.ps1, or opens the official install page when that file is missing.
/// Does not read credentials and does not execute a remote script.
/// </summary>
public static class CursorInstall
{
    public const string ButtonLabel = "Install Cursor";
    public const string OfficialPage = "https://cursor.com/docs/cli/installation";
    public const string AlreadyInstalledStatus =
        "Cursor connector is already installed. Click Connect / refresh models.";
    public const string InstalledStatus =
        "Cursor connector installed. Sign in with Cursor, then click Connect / refresh models.";
    public const string FailedStatus =
        "Cursor install did not finish. Click Install Cursor to try again, then Connect / refresh models.";
    public const string CanceledStatus =
        "Cursor install was canceled or timed out. Click Install Cursor to try again, then Connect / refresh models.";
    public const string PageOpenedStatus =
        "The documented Cursor installer was not found beside the app, so the official install page was opened. Click Connect / refresh models after the connector is installed.";
    public const string PageFailedStatus =
        "Windows could not open the Cursor install page. Click Connect / refresh models after the connector is installed.";
    public const string ScriptMissingStatus =
        "The documented Cursor installer was not found beside the app.";

    private static readonly string[] SecretEnvironment =
    [
        "CURSOR_API_KEY", "CURSOR_AUTH_TOKEN", "CURSOR_API_ENDPOINT"
    ];

    public static bool Offer(bool cursorSelected, bool runtimeInstalled) =>
        cursorSelected && !runtimeInstalled;

    public static CursorInstallPlan Plan(string baseDirectory, bool runtimeInstalled)
    {
        if (runtimeInstalled) return new(CursorInstallStep.AlreadyInstalled, "", null);
        if (TryScript(baseDirectory, out var script)) return new(CursorInstallStep.RunScript, script, null);
        return new(CursorInstallStep.OpenPage, "", OfficialUri());
    }

    public static bool TryScript(string baseDirectory, out string scriptPath)
    {
        scriptPath = "";
        if (string.IsNullOrWhiteSpace(baseDirectory)) return false;
        string root;
        try { root = Path.GetFullPath(baseDirectory); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        var script = Path.Combine(root, "setup", "Install-CursorConnection.ps1");
        string full;
        try { full = Path.GetFullPath(script); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        if (!IsDocumentedScript(full) || !File.Exists(full)) return false;
        scriptPath = full;
        return true;
    }

    public static bool IsDocumentedScript(string scriptPath)
    {
        var name = Path.GetFileName(scriptPath);
        var parent = Path.GetFileName(Path.GetDirectoryName(scriptPath) ?? "");
        return name.Equals("Install-CursorConnection.ps1", StringComparison.OrdinalIgnoreCase)
            && parent.Equals("setup", StringComparison.OrdinalIgnoreCase);
    }

    public static Uri OfficialUri()
    {
        var page = new Uri(OfficialPage);
        if (!page.IsAbsoluteUri || page.Scheme != Uri.UriSchemeHttps
            || !page.Host.Equals("cursor.com", StringComparison.OrdinalIgnoreCase)
            || !page.AbsolutePath.Equals("/docs/cli/installation", StringComparison.Ordinal))
            throw new InvalidOperationException("The official Cursor install page is not https://cursor.com/docs/cli/installation.");
        return page;
    }

    public static ProcessStartInfo Launch(string scriptPath)
    {
        if (!IsDocumentedScript(scriptPath))
            throw new InvalidOperationException(ScriptMissingStatus);
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-ExecutionPolicy");
        info.ArgumentList.Add("Bypass");
        info.ArgumentList.Add("-File");
        info.ArgumentList.Add(scriptPath);
        foreach (var name in SecretEnvironment) info.Environment.Remove(name);
        return info;
    }

    public static async Task<int> RunInstallerAsync(string baseDirectory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!TryScript(baseDirectory, out var script))
            throw new InvalidOperationException(ScriptMissingStatus);
        using var process = Process.Start(Launch(script)) ?? throw new InvalidOperationException("Cursor install could not start.");
        using var cancel = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (NotSupportedException) { }
        });
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(ct);
            return process.ExitCode;
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
            try { await Task.WhenAll(stdout, stderr); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        }
    }
}

public enum CursorInstallStep { AlreadyInstalled, RunScript, OpenPage }

public readonly record struct CursorInstallPlan(CursorInstallStep Step, string ScriptPath, Uri? Page);
