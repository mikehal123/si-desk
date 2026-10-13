using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;

namespace Sovereign.Desktop.SetupLib;

public static class SetupEngine
{
    const string ManifestName = "install-manifest.txt";
    const string RecordName = "install-record.txt";
    const string SetupFileName = "xAIDesk.Setup.exe";

    public static SetupResult Run(SetupRequest request)
    {
        var log = new List<string>();
        try
        {
            if (string.IsNullOrWhiteSpace(request.InstallDir))
                return Fail(log, "Install directory is required.");
            if (!SafeId(request.ProductId))
                return Fail(log, "Product id is not a per-user id.");
            var installDir = Path.GetFullPath(request.InstallDir);
            return request.Action == SetupAction.Uninstall
                ? Uninstall(request, installDir, log)
                : Install(request, installDir, log);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Fail(log, ex.Message);
        }
    }

    static SetupResult Install(SetupRequest request, string installDir, List<string> log)
    {
        if (string.IsNullOrWhiteSpace(request.PayloadDir) || !Directory.Exists(request.PayloadDir))
            return Fail(log, "Payload directory was not found.");
        var payloadDir = Path.GetFullPath(request.PayloadDir);
        if (SameDir(installDir, payloadDir))
            return Fail(log, "Install directory and payload directory are the same.");

        Directory.CreateDirectory(installDir);
        var copied = new List<string>();
        var limit = request.FailAfterFileCount;
        foreach (var source in EnumerateFiles(payloadDir))
        {
            if (limit is not null && copied.Count >= limit.Value)
                return Fail(log, "Stopped after the requested file count.");
            var relative = Path.GetRelativePath(payloadDir, source);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                continue;
            var destination = Path.Combine(installDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
            copied.Add(relative);
            log.Add("copied " + relative);
        }

        var setupSource = FindSetupExe();
        if (setupSource is not null)
        {
            var setupDest = Path.Combine(installDir, SetupFileName);
            if (!SameFile(setupSource, setupDest))
                File.Copy(setupSource, setupDest, overwrite: true);
            if (!copied.Contains(SetupFileName, StringComparer.OrdinalIgnoreCase))
                copied.Add(SetupFileName);
        }

        File.WriteAllLines(Path.Combine(installDir, ManifestName), copied);
        var startLink = WriteShortcut(ProgramsDir(), request.StartMenuName, installDir);
        string? desktopLink = null;
        if (request.DesktopShortcut)
            desktopLink = WriteShortcut(DesktopDir(), request.StartMenuName, installDir);
        WriteRecord(installDir, request.ProductId, startLink, desktopLink);
        WriteUninstallKey(request, installDir, copied);
        log.Add("installed " + installDir);
        WriteSentinel(request.SentinelPath, "installed");
        return new SetupResult(0, "Installed " + request.ProductName + " " + request.Version + ".", log);
    }

    static SetupResult Uninstall(SetupRequest request, string installDir, List<string> log)
    {
        var record = ReadRecord(installDir);
        DeleteIfExists(record.StartMenu);
        DeleteIfExists(record.Desktop);
        if (record.StartMenu is null)
            DeleteIfExists(Path.Combine(ProgramsDir(), SafeFileName(request.StartMenuName) + ".lnk"));
        if (request.DesktopShortcut)
            DeleteIfExists(Path.Combine(DesktopDir(), SafeFileName(request.StartMenuName) + ".lnk"));

        var manifestPath = Path.Combine(installDir, ManifestName);
        if (File.Exists(manifestPath))
        {
            foreach (var relative in File.ReadAllLines(manifestPath))
            {
                if (string.IsNullOrWhiteSpace(relative) || relative.Contains("..", StringComparison.Ordinal))
                    continue;
                DeleteIfExists(Path.Combine(installDir, relative));
            }
        }

        DeleteIfExists(manifestPath);
        DeleteIfExists(Path.Combine(installDir, RecordName));
        DeleteIfExists(Path.Combine(installDir, SetupFileName));
        var removeData = !request.KeepData;
        try
        {
            DeleteEmptyDirectories(installDir);
            if (removeData && Directory.Exists(installDir))
                Directory.Delete(installDir, recursive: true);
            else if (Directory.Exists(installDir) && !Directory.EnumerateFileSystemEntries(installDir).Any())
                Directory.Delete(installDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!IsRunningSetupInside(installDir))
                throw;
            ScheduleSelfDelete(installDir, removeData);
            log.Add("setup executable will be removed after uninstall exits");
        }

        if (!request.KeepData && !string.IsNullOrWhiteSpace(request.ProfileDir))
        {
            var profile = Path.GetFullPath(request.ProfileDir);
            if (Directory.Exists(profile))
                Directory.Delete(profile, recursive: true);
        }

        DeleteUninstallKey(request.ProductId);
        log.Add("uninstalled " + installDir);
        WriteSentinel(request.SentinelPath, "uninstalled");
        return new SetupResult(0, "Uninstalled " + request.ProductName + ".", log);
    }

    static void WriteUninstallKey(SetupRequest request, string installDir, List<string> copied)
    {
        var setupExe = Path.Combine(installDir, SetupFileName);
        var target = File.Exists(setupExe) ? setupExe : FindSetupExe() ?? setupExe;
        long bytes = 0;
        foreach (var relative in copied)
        {
            var path = Path.Combine(installDir, relative);
            if (File.Exists(path))
                bytes += new FileInfo(path).Length;
        }

        using var key = Registry.CurrentUser.CreateSubKey(UninstallPath(request.ProductId));
        key.SetValue("DisplayName", request.ProductName);
        key.SetValue("DisplayVersion", request.Version);
        key.SetValue("Publisher", "xAI Desk");
        key.SetValue("InstallLocation", installDir);
        key.SetValue("DisplayIcon", Path.Combine(installDir, "xAIDesk.exe"));
        key.SetValue("UninstallString", "\"" + target + "\" --uninstall --install-dir \"" + installDir + "\" --product-id \"" + request.ProductId + "\"");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, bytes / 1024), RegistryValueKind.DWord);
    }

    static void DeleteUninstallKey(string productId)
    {
        Registry.CurrentUser.DeleteSubKeyTree(UninstallPath(productId), throwOnMissingSubKey: false);
    }

    static string UninstallPath(string productId) =>
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + productId;

    static string WriteShortcut(string folder, string name, string installDir)
    {
        Directory.CreateDirectory(folder);
        var link = Path.Combine(folder, SafeFileName(name) + ".lnk");
        var target = Path.Combine(installDir, "xAIDesk.exe");
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Shortcut shell is not available.");
        var shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("Shortcut shell is not available.");
        var shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, [link])
            ?? throw new InvalidOperationException("Shortcut was not created.");
        var shortcutType = shortcut.GetType();
        shortcutType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, [target]);
        shortcutType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, [installDir]);
        shortcutType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
        return link;
    }

    static void WriteRecord(string installDir, string productId, string startLink, string? desktopLink)
    {
        var lines = new List<string>
        {
            "product-id=" + productId,
            "start-menu=" + startLink,
            "desktop=" + (desktopLink ?? "")
        };
        File.WriteAllLines(Path.Combine(installDir, RecordName), lines);
    }

    static (string? StartMenu, string? Desktop) ReadRecord(string installDir)
    {
        var path = Path.Combine(installDir, RecordName);
        if (!File.Exists(path))
            return (null, null);
        string? start = null;
        string? desktop = null;
        foreach (var line in File.ReadAllLines(path))
        {
            if (line.StartsWith("start-menu=", StringComparison.Ordinal))
                start = EmptyToNull(line["start-menu=".Length..]);
            else if (line.StartsWith("desktop=", StringComparison.Ordinal))
                desktop = EmptyToNull(line["desktop=".Length..]);
        }
        return (start, desktop);
    }

    static IEnumerable<string> EnumerateFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(dir); }
            catch (IOException) { continue; }
            foreach (var child in children)
            {
                if (IsReparse(child))
                    continue;
                pending.Push(child);
            }
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir); }
            catch (IOException) { continue; }
            foreach (var file in files)
            {
                if (IsReparse(file))
                    continue;
                yield return file;
            }
        }
    }

    static bool IsReparse(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (IOException)
        {
            return true;
        }
    }

    static string? FindSetupExe()
    {
        var process = Environment.ProcessPath;
        if (IsSetupExe(process))
            return process;
        var beside = Path.Combine(AppContext.BaseDirectory, SetupFileName);
        return IsSetupExe(beside) ? beside : null;
    }

    static bool IsSetupExe(string? path) =>
        !string.IsNullOrEmpty(path)
        && File.Exists(path)
        && path.EndsWith(SetupFileName, StringComparison.OrdinalIgnoreCase);

    static string ProgramsDir() =>
        Environment.GetFolderPath(Environment.SpecialFolder.Programs);

    static string DesktopDir() =>
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    static string SafeFileName(string name)
    {
        var cleaned = name.Trim();
        if (cleaned.Length == 0)
            cleaned = "xAI Desk";
        foreach (var bad in Path.GetInvalidFileNameChars())
            cleaned = cleaned.Replace(bad, ' ');
        cleaned = cleaned.Trim();
        if (cleaned.Length > 80)
            cleaned = cleaned[..80].Trim();
        return cleaned.Length == 0 ? "xAI Desk" : cleaned;
    }

    static bool SafeId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 80)
            return false;
        foreach (var c in id)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
                continue;
            return false;
        }
        return true;
    }

    static bool SameDir(string left, string right) =>
        string.Equals(left.TrimEnd('\\'), right.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    static bool SameFile(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    static void DeleteIfExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;
        if (IsRunningProcess(path))
            return;
        File.Delete(path);
    }

    static bool IsRunningProcess(string path)
    {
        var process = Environment.ProcessPath;
        if (string.IsNullOrEmpty(process))
            return false;
        try
        {
            return string.Equals(Path.GetFullPath(process), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    static bool IsRunningSetupInside(string installDir)
    {
        var process = Environment.ProcessPath;
        if (!IsSetupExe(process))
            return false;
        var file = Path.GetFullPath(process!);
        var dir = Path.GetFullPath(installDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return file.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    static void ScheduleSelfDelete(string installDir, bool removeDirectory)
    {
        var process = Environment.ProcessPath;
        if (!IsSetupExe(process))
            return;
        var file = Path.GetFullPath(process!);
        var dir = Path.GetFullPath(installDir);
        if (file.IndexOfAny(['"', '&', '%']) >= 0 || dir.IndexOfAny(['"', '&', '%']) >= 0)
            return;
        var removeDir = removeDirectory
            ? " & rmdir /s /q \"" + dir + "\""
            : " & rmdir \"" + dir + "\"";
        var script = "for /l %i in (1,1,12) do @if exist \"" + file + "\" (ping -n 2 127.0.0.1 >nul & del /f /q \"" + file + "\")" + removeDir;
        using var _ = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = "/c " + script,
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    static void DeleteEmptyDirectories(string root)
    {
        if (!Directory.Exists(root))
            return;
        foreach (var child in Directory.EnumerateDirectories(root).ToList())
        {
            DeleteEmptyDirectories(child);
            if (!Directory.EnumerateFileSystemEntries(child).Any())
                Directory.Delete(child);
        }
    }

    static void WriteSentinel(string? path, string text)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        var full = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);
        File.WriteAllText(full, text);
    }

    static string? EmptyToNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    static SetupResult Fail(List<string> log, string message)
    {
        log.Add(message);
        return new SetupResult(1, message, log);
    }
}
