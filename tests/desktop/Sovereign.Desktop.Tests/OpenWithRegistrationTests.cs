using Sovereign.Desktop.SetupLib;
using Xunit;

namespace Sovereign.Desktop.Tests
{
    public sealed class OpenWithRegistrationTests
    {
        [Fact]
        public void AppliesTo_accepts_only_production_and_main()
        {
            Assert.True(OpenWithRegistration.AppliesTo(InstallerEngine.ProductionProductId));
            Assert.True(OpenWithRegistration.AppliesTo(InstallerEngine.ProductionProductId + ".main"));
            Assert.False(OpenWithRegistration.AppliesTo(InstallerEngine.ProductionProductId + ".qa-foo"));
            Assert.False(OpenWithRegistration.AppliesTo(".qa-foo"));
            Assert.False(OpenWithRegistration.AppliesTo(InstallerEngine.ProductionProductId + ".DESK"));
            Assert.False(OpenWithRegistration.AppliesTo(".DESK"));
            Assert.False(OpenWithRegistration.AppliesTo(""));
            Assert.False(OpenWithRegistration.AppliesTo("sovereignai.desktoppreview.main"));
        }

        [Fact]
        public void Register_adds_open_with_entries_without_replacing_the_user_default()
        {
            WithTemp(root =>
            {
                var install = Path.Combine(root, "install dir");
                var profile = Path.Combine(root, "profile dir");
                Directory.CreateDirectory(install);
                Directory.CreateDirectory(profile);
                var store = new MemoryOpenWithStore();
                store.Set(@"Software\Classes\.md", "", "Cursor.md");
                store.Set(@"Software\Classes\.md\OpenWithProgids", "Cursor.md", "");
                store.Sets.Clear();

                var launcher = OpenWithRegistration.Register(install, profile, store);
                var exe = OpenWithRegistration.ExecutablePath(install);
                var command = "\"" + launcher + "\" \"%1\"";
                var expected = new List<OpenWithValue>();
                foreach (var (progId, extension, friendly) in new (string ProgId, string Extension, string Friendly)[]
                {
                    (OpenWithRegistration.MarkdownProgId, ".md", "xAI Desk Markdown"),
                    (OpenWithRegistration.TextProgId, ".txt", "xAI Desk Text"),
                    (OpenWithRegistration.JsonProgId, ".json", "xAI Desk JSON")
                })
                {
                    var progIdKey = @"Software\Classes\" + progId;
                    expected.Add(new OpenWithValue(progIdKey, "", friendly));
                    expected.Add(new OpenWithValue(progIdKey + @"\DefaultIcon", "", exe + ",0"));
                    expected.Add(new OpenWithValue(progIdKey + @"\shell\open\command", "", command));
                    expected.Add(new OpenWithValue(@"Software\Classes\" + extension + @"\OpenWithProgids", progId, ""));
                }

                Assert.Equal(expected.Count, store.Sets.Count);
                Assert.Equal(Sorted(expected), Sorted(store.Sets));
                Assert.Equal("\"" + launcher + "\" \"%1\"", store.Get(@"Software\Classes\xAIDesk.md\shell\open\command", ""));
                Assert.Contains("%1", store.Get(@"Software\Classes\xAIDesk.md\shell\open\command", "")!, StringComparison.Ordinal);
                Assert.Equal("Cursor.md", store.Get(@"Software\Classes\.md", ""));
                Assert.Equal("", store.Get(@"Software\Classes\.md\OpenWithProgids", "Cursor.md"));
                Assert.Equal("", store.Get(@"Software\Classes\.md\OpenWithProgids", OpenWithRegistration.MarkdownProgId));
                Assert.DoesNotContain(store.Sets, value => value.KeyPath == @"Software\Classes\.md" && value.ValueName == "");
                Assert.DoesNotContain(store.Sets, value =>
                    value.KeyPath.Contains("UserChoice", StringComparison.Ordinal)
                    || value.KeyPath.Contains("FileExts", StringComparison.Ordinal)
                    || value.KeyPath.Contains("RegisteredApplications", StringComparison.Ordinal));
            });
        }

        [Fact]
        public void Register_writes_launcher_and_rewrites_profile()
        {
            WithTemp(root =>
            {
                var install = Path.Combine(root, "main", "app");
                var firstProfile = Path.Combine(root, "alpha profile");
                var secondProfile = Path.Combine(root, "beta profile");
                Directory.CreateDirectory(install);
                Directory.CreateDirectory(firstProfile);
                Directory.CreateDirectory(secondProfile);
                var store = new MemoryOpenWithStore();
                var launcher = OpenWithRegistration.Register(install, firstProfile, store);
                var exe = OpenWithRegistration.ExecutablePath(install);
                var firstScript = File.ReadAllText(launcher);
                Assert.Equal(ExpectedScript(exe, Path.GetFullPath(firstProfile)), firstScript);
                Assert.Contains(exe, firstScript, StringComparison.Ordinal);
                Assert.Contains("--mode native", firstScript, StringComparison.Ordinal);
                Assert.Contains(Path.GetFullPath(firstProfile), firstScript, StringComparison.Ordinal);
                Assert.Contains("\"%~1\"", firstScript, StringComparison.Ordinal);
                var bytes = File.ReadAllBytes(launcher);
                Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

                OpenWithRegistration.Register(install, secondProfile, store);
                var rewritten = File.ReadAllText(launcher);
                Assert.Equal(ExpectedScript(exe, Path.GetFullPath(secondProfile)), rewritten);
                Assert.Contains(Path.GetFullPath(secondProfile), rewritten, StringComparison.Ordinal);
                Assert.DoesNotContain(Path.GetFullPath(firstProfile), rewritten, StringComparison.Ordinal);
            });
        }

        [Fact]
        public void Register_omits_profile_when_missing()
        {
            WithTemp(root =>
            {
                var install = Path.Combine(root, "app");
                Directory.CreateDirectory(install);
                var store = new MemoryOpenWithStore();
                var exe = OpenWithRegistration.ExecutablePath(install);
                var launcher = OpenWithRegistration.Register(install, null, store);
                Assert.Equal(ExpectedScript(exe, null), File.ReadAllText(launcher));
                Assert.DoesNotContain("--profile", File.ReadAllText(launcher), StringComparison.Ordinal);
                Assert.Contains("--mode native", File.ReadAllText(launcher), StringComparison.Ordinal);
                Assert.Contains("\"%~1\"", File.ReadAllText(launcher), StringComparison.Ordinal);

                OpenWithRegistration.Register(install, "   ", store);
                Assert.Equal(ExpectedScript(exe, null), File.ReadAllText(launcher));
                Assert.DoesNotContain("--profile", File.ReadAllText(launcher), StringComparison.Ordinal);
            });
        }

        [Fact]
        public void Register_rejects_quote_in_profile_before_writing_launcher()
        {
            WithTemp(root =>
            {
                var install = Path.Combine(root, "app");
                var profile = Path.Combine(root, "profile");
                Directory.CreateDirectory(install);
                Directory.CreateDirectory(profile);
                foreach (var suffix in new[] { "\"", "%", "&", "^", "|", "<", ">", "\n", "\r", "\\" })
                {
                    var store = new MemoryOpenWithStore();
                    Assert.Throws<ArgumentException>(() => OpenWithRegistration.Register(install, profile + suffix, store));
                    Assert.Empty(store.Sets);
                }

                var installStore = new MemoryOpenWithStore();
                Assert.Throws<ArgumentException>(() => OpenWithRegistration.Register(install + "\"", profile, installStore));
                Assert.Throws<ArgumentException>(() => OpenWithRegistration.Register(install + "\\", profile, installStore));
                Assert.Empty(installStore.Sets);
                Assert.False(File.Exists(OpenWithRegistration.LauncherPath(install)));
            });
        }

        [Fact]
        public void Unregister_removes_only_this_apps_progids_and_launcher()
        {
            WithTemp(root =>
            {
                var install = Path.Combine(root, "main", "app");
                var profile = Path.Combine(root, "profile");
                Directory.CreateDirectory(install);
                Directory.CreateDirectory(profile);
                var store = new MemoryOpenWithStore();
                store.Set(@"Software\Classes\.md", "", "Cursor.md");
                store.Set(@"Software\Classes\.md\OpenWithProgids", "Cursor.md", "");
                var launcher = OpenWithRegistration.Register(install, profile, store);
                Assert.True(File.Exists(launcher));

                OpenWithRegistration.Unregister(install, store);

                Assert.False(store.ContainsKey(@"Software\Classes\xAIDesk.md"));
                Assert.False(store.ContainsKey(@"Software\Classes\xAIDesk.md\DefaultIcon"));
                Assert.False(store.ContainsKey(@"Software\Classes\xAIDesk.md\shell\open\command"));
                Assert.False(store.ContainsKey(@"Software\Classes\xAIDesk.txt"));
                Assert.False(store.ContainsKey(@"Software\Classes\xAIDesk.txt\DefaultIcon"));
                Assert.False(store.ContainsKey(@"Software\Classes\xAIDesk.txt\shell\open\command"));
                Assert.False(store.ContainsKey(@"Software\Classes\xAIDesk.json"));
                Assert.False(store.ContainsKey(@"Software\Classes\xAIDesk.json\DefaultIcon"));
                Assert.False(store.ContainsKey(@"Software\Classes\xAIDesk.json\shell\open\command"));
                Assert.Null(store.Get(@"Software\Classes\.md\OpenWithProgids", OpenWithRegistration.MarkdownProgId));
                Assert.Null(store.Get(@"Software\Classes\.txt\OpenWithProgids", OpenWithRegistration.TextProgId));
                Assert.Null(store.Get(@"Software\Classes\.json\OpenWithProgids", OpenWithRegistration.JsonProgId));
                Assert.Equal("Cursor.md", store.Get(@"Software\Classes\.md", ""));
                Assert.Equal("", store.Get(@"Software\Classes\.md\OpenWithProgids", "Cursor.md"));
                Assert.True(store.ContainsKey(@"Software\Classes\.md"));
                Assert.False(File.Exists(launcher));
                Assert.True(Directory.Exists(install));
                Assert.True(Directory.Exists(Path.GetDirectoryName(install)));
            });
        }

        [Fact]
        public void LauncherPath_is_the_parent_of_the_install_directory()
        {
            WithTemp(root =>
            {
                var install = Path.Combine(root, "main", "app");
                Directory.CreateDirectory(install);
                var launcher = OpenWithRegistration.LauncherPath(install);
                var parent = Directory.GetParent(Path.GetFullPath(install))!.FullName;
                Assert.Equal(Path.Combine(parent, OpenWithRegistration.LauncherFileName), launcher);
                Assert.NotEqual(Path.Combine(Path.GetFullPath(install), OpenWithRegistration.LauncherFileName), launcher);
                Assert.False(launcher.StartsWith(Path.GetFullPath(install) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                Assert.Equal(
                    Path.Combine(Path.GetFullPath(install), "xAIDesk.exe"),
                    OpenWithRegistration.ExecutablePath(install));
            });
        }

        private static string ExpectedScript(string exe, string? profile)
        {
            var line = profile is null
                ? "start \"\" \"" + exe + "\" --mode native \"%~1\""
                : "start \"\" \"" + exe + "\" --mode native --profile \"" + profile + "\" \"%~1\"";
            return "@echo off" + Environment.NewLine + line + Environment.NewLine;
        }

        private static IEnumerable<OpenWithValue> Sorted(IEnumerable<OpenWithValue> values)
        {
            return values.OrderBy(value => value.KeyPath, StringComparer.Ordinal).ThenBy(value => value.ValueName, StringComparer.Ordinal);
        }

        private static void WithTemp(Action<string> body)
        {
            var root = Path.Combine(Path.GetTempPath(), "xAI Desk-openwith-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                body(root);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        }

        private sealed class MemoryOpenWithStore : IOpenWithStore
        {
            private readonly Dictionary<string, Dictionary<string, string>> _values = new(StringComparer.Ordinal);
            public List<OpenWithValue> Sets { get; } = [];

            public void Set(string keyPath, string valueName, string data)
            {
                if (!_values.TryGetValue(keyPath, out var values))
                {
                    values = new Dictionary<string, string>(StringComparer.Ordinal);
                    _values[keyPath] = values;
                }

                values[valueName] = data;
                Sets.Add(new OpenWithValue(keyPath, valueName, data));
            }

            public void DeleteKeyTree(string keyPath)
            {
                var prefix = keyPath.TrimEnd('\\') + "\\";
                foreach (var key in _values.Keys.Where(key => string.Equals(key, keyPath, StringComparison.Ordinal) || key.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
                {
                    _values.Remove(key);
                }
            }

            public void DeleteValue(string keyPath, string valueName)
            {
                if (_values.TryGetValue(keyPath, out var values))
                {
                    values.Remove(valueName);
                }
            }

            public string? Get(string keyPath, string valueName)
            {
                if (_values.TryGetValue(keyPath, out var values) && values.TryGetValue(valueName, out var data))
                {
                    return data;
                }

                return null;
            }

            public bool ContainsKey(string keyPath)
            {
                return _values.ContainsKey(keyPath);
            }
        }
    }
}
