using System.Text;
using Microsoft.Win32;

namespace Sovereign.Desktop.SetupLib
{
    public sealed record OpenWithValue(string KeyPath, string ValueName, string Data);

    public interface IOpenWithStore
    {
        void Set(string keyPath, string valueName, string data);

        void DeleteKeyTree(string keyPath);

        void DeleteValue(string keyPath, string valueName);
    }

    public sealed class HkcuOpenWithStore : IOpenWithStore
    {
        // Registry.CurrentUser. CreateSubKey + SetValue(valueName, data, RegistryValueKind.String).
        // Empty valueName is the key's default value.
        public void Set(string keyPath, string valueName, string data)
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath) ?? throw new InvalidOperationException("Could not create HKCU\\" + keyPath + ".");
            key.SetValue(valueName, data, RegistryValueKind.String);
        }

        // DeleteKeyTree uses throwOnMissingSubKey: false.
        public void DeleteKeyTree(string keyPath)
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }

        // DeleteValue opens writable and uses throwOnMissingValue: false; missing key is a no-op.
        public void DeleteValue(string keyPath, string valueName)
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
            if (key is null)
            {
                return;
            }

            key.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }

    public static class OpenWithRegistration
    {
        public const string MarkdownProgId = "SIDesk.md";
        public const string TextProgId = "SIDesk.txt";
        public const string JsonProgId = "SIDesk.json";
        public const string LauncherFileName = "SIDesk.OpenWith.cmd";
        private const string ExecutableFileName = "SIDesk.exe";
        private static readonly char[] UnsafeCommandCharacters = ['"', '%', '&', '^', '|', '<', '>', '\r', '\n'];

        // True only for the production product id and that id + ".main".
        public static bool AppliesTo(string productId)
        {
            return string.Equals(productId, SetupCli.ProductionProductId, StringComparison.Ordinal)
                || string.Equals(productId, SetupCli.ProductionProductId + ".main", StringComparison.Ordinal);
        }

        // Path.GetFullPath(installDir)/SIDesk.exe
        public static string ExecutablePath(string installDir)
        {
            return Path.Combine(Path.GetFullPath(installDir), ExecutableFileName);
        }

        // Parent of full installDir + LauncherFileName. Throw InvalidOperationException if the install dir has no parent.
        public static string LauncherPath(string installDir)
        {
            var full = Path.GetFullPath(installDir);
            var parent = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(parent))
            {
                throw new InvalidOperationException("The install directory has no parent.");
            }

            return Path.Combine(parent, LauncherFileName);
        }

        // Writes the launcher file and registry entries. Returns the launcher full path.
        public static string Register(string installDir, string? profileDir, IOpenWithStore store)
        {
            ArgumentNullException.ThrowIfNull(store);
            var install = RequiredPath(installDir, nameof(installDir));
            var profile = OptionalProfile(profileDir);
            var exe = ExecutablePath(install);
            var launcher = LauncherPath(install);
            var launcherDirectory = Path.GetDirectoryName(launcher);
            if (!string.IsNullOrEmpty(launcherDirectory))
            {
                Directory.CreateDirectory(launcherDirectory);
            }

            File.WriteAllText(launcher, LauncherScript(exe, profile), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            WriteExtension(store, MarkdownProgId, ".md", "SI Desk Markdown", exe, launcher);
            WriteExtension(store, TextProgId, ".txt", "SI Desk Text", exe, launcher);
            WriteExtension(store, JsonProgId, ".json", "SI Desk JSON", exe, launcher);
            return launcher;
        }

        // Removes only this app's progids, OpenWithProgids values, and the launcher file.
        public static void Unregister(string installDir, IOpenWithStore store)
        {
            ArgumentNullException.ThrowIfNull(store);
            var launcher = LauncherPath(installDir);
            store.DeleteKeyTree(@"Software\Classes\" + MarkdownProgId);
            store.DeleteKeyTree(@"Software\Classes\" + TextProgId);
            store.DeleteKeyTree(@"Software\Classes\" + JsonProgId);
            store.DeleteValue(@"Software\Classes\.md\OpenWithProgids", MarkdownProgId);
            store.DeleteValue(@"Software\Classes\.txt\OpenWithProgids", TextProgId);
            store.DeleteValue(@"Software\Classes\.json\OpenWithProgids", JsonProgId);
            if (File.Exists(launcher))
            {
                File.Delete(launcher);
            }
        }

        private static void WriteExtension(IOpenWithStore store, string progId, string extension, string friendlyName, string exe, string launcher)
        {
            var progIdKey = @"Software\Classes\" + progId;
            store.Set(progIdKey, "", friendlyName);
            store.Set(progIdKey + @"\DefaultIcon", "", exe + ",0");
            store.Set(progIdKey + @"\shell\open\command", "", "\"" + launcher + "\" \"%1\"");
            store.Set(@"Software\Classes\" + extension + @"\OpenWithProgids", progId, "");
        }

        private static string LauncherScript(string exe, string? profile)
        {
            var arguments = profile is null
                ? "\"" + exe + "\" --mode native \"%~1\""
                : "\"" + exe + "\" --mode native --profile \"" + profile + "\" \"%~1\"";
            return "@echo off" + Environment.NewLine + "start \"\" " + arguments + Environment.NewLine;
        }

        private static string RequiredPath(string path, string paramName)
        {
            ArgumentNullException.ThrowIfNull(path);
            RejectUnsafe(path, paramName);
            var trimmed = path.Trim();
            if (trimmed.Length == 0)
            {
                throw new ArgumentException("The path is required.", paramName);
            }

            RejectTrailingBackslash(trimmed, paramName);
            return trimmed;
        }

        private static string? OptionalProfile(string? profileDir)
        {
            if (profileDir is null)
            {
                return null;
            }

            RejectUnsafe(profileDir, nameof(profileDir));
            if (string.IsNullOrWhiteSpace(profileDir))
            {
                return null;
            }

            var trimmed = profileDir.Trim();
            RejectTrailingBackslash(trimmed, nameof(profileDir));
            return Path.GetFullPath(trimmed);
        }

        private static void RejectUnsafe(string path, string paramName)
        {
            if (path.IndexOfAny(UnsafeCommandCharacters) >= 0)
            {
                throw new ArgumentException("The path contains a character that cannot be quoted into the Open With command.", paramName);
            }
        }

        private static void RejectTrailingBackslash(string trimmed, string paramName)
        {
            if (trimmed.EndsWith('\\'))
            {
                throw new ArgumentException("The path must not end with a backslash.", paramName);
            }
        }
    }
}
