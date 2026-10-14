using Sovereign.Desktop.SetupLib;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class InstallerOpenWithTests
{
    [Fact]
    public void Main_product_registers_open_with_without_replacing_the_default_app()
    {
        var root = Path.Combine(Path.GetTempPath(), "sai-openwith-" + Guid.NewGuid().ToString("N"));
        var install = Path.Combine(root, "app");
        var profile = Path.Combine(root, "profile");
        Directory.CreateDirectory(install);
        var store = new MemoryOpenWithStore();
        store.Set(@"Software\Classes\.md", "", "Cursor.md");
        store.Set(@"Software\Classes\.md\OpenWithProgids", "Cursor.md", "");
        var log = new List<string>();
        var request = Request(InstallerEngine.ProductionProductId + ".main", install, profile);
        try
        {
            InstallerEngine.ConfigureOpenWith(request, install, log, store, remove: false);
            Assert.Contains(log, line => line.StartsWith("open-with launcher ", StringComparison.Ordinal));
            Assert.Equal("Cursor.md", store.Get(@"Software\Classes\.md", ""));
            Assert.Equal("", store.Get(@"Software\Classes\.md\OpenWithProgids", "Cursor.md"));
            Assert.Equal("", store.Get(@"Software\Classes\.md\OpenWithProgids", "SIDesk.md"));
            Assert.Equal("", store.Get(@"Software\Classes\.txt\OpenWithProgids", "SIDesk.txt"));
            Assert.Equal("", store.Get(@"Software\Classes\.json\OpenWithProgids", "SIDesk.json"));
            var command = store.Get(@"Software\Classes\SIDesk.md\shell\open\command", "");
            Assert.Contains("\"%1\"", command, StringComparison.Ordinal);
            Assert.DoesNotContain("UserChoice", string.Join("\n", store.Keys), StringComparison.Ordinal);
            var launcher = OpenWithRegistration.LauncherPath(install);
            Assert.True(File.Exists(launcher));
            Assert.Contains(profile, File.ReadAllText(launcher), StringComparison.Ordinal);
            Assert.False(launcher.StartsWith(Path.GetFullPath(install), StringComparison.OrdinalIgnoreCase));

            InstallerEngine.ConfigureOpenWith(request, install, log, store, remove: true);
            Assert.Contains("open-with removed", log);
            Assert.False(File.Exists(launcher));
            Assert.Equal("Cursor.md", store.Get(@"Software\Classes\.md", ""));
            Assert.Equal("", store.Get(@"Software\Classes\.md\OpenWithProgids", "Cursor.md"));
            Assert.Null(store.Get(@"Software\Classes\.md\OpenWithProgids", "SIDesk.md"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Qa_install_and_uninstall_do_not_claim_open_with()
    {
        var root = Path.Combine(Path.GetTempPath(), "sai-openwith-qa-" + Guid.NewGuid().ToString("N"));
        var payload = Path.Combine(root, "payload");
        var install = Path.Combine(root, "app");
        var profile = Path.Combine(root, "profile");
        Directory.CreateDirectory(payload);
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(payload, "app.txt"), "A");
        var launcher = Path.Combine(root, "SovereignAI.OpenWith.cmd");
        File.WriteAllText(launcher, "keep");
        var product = "SovereignAI.DesktopPreview.qa-openwith-" + Guid.NewGuid().ToString("N")[..8];
        var start = "SI Desk OpenWith " + product;
        try
        {
            var installed = InstallerEngine.Run(new SetupRequest(
                SetupAction.Install, install, payload, product, start, "0.1.0", profile, start, true, null, null));
            Assert.True(installed.Ok, installed.Message);
            Assert.Contains("open-with skipped " + product, installed.Log);
            Assert.Equal("keep", File.ReadAllText(launcher));

            var removed = InstallerEngine.Run(new SetupRequest(
                SetupAction.Uninstall, install, payload, product, start, "0.1.0", profile, start, true, null, null));
            Assert.True(removed.Ok, removed.Message);
            Assert.Contains("open-with skipped " + product, removed.Log);
            Assert.Equal("keep", File.ReadAllText(launcher));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + product, false);
            var startDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", start);
            if (Directory.Exists(startDir)) Directory.Delete(startDir, true);
        }
    }

    private static SetupRequest Request(string productId, string install, string profile) =>
        new(SetupAction.Install, install, null, productId, "SI Desk", "0.3.53", profile, "SI Desk", true, null, null);

    private sealed class MemoryOpenWithStore : IOpenWithStore
    {
        private readonly Dictionary<(string Key, string Name), string> _values = new();
        private readonly HashSet<string> _deletedTrees = new(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<string> Keys => _values.Keys.Select(pair => pair.Key);

        public void Set(string keyPath, string valueName, string data) => _values[(keyPath, valueName)] = data;

        public void DeleteKeyTree(string keyPath)
        {
            _deletedTrees.Add(keyPath);
            foreach (var key in _values.Keys.Where(pair => pair.Key.Equals(keyPath, StringComparison.OrdinalIgnoreCase)
                || pair.Key.StartsWith(keyPath + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
                _values.Remove(key);
        }

        public void DeleteValue(string keyPath, string valueName) => _values.Remove((keyPath, valueName));

        public string? Get(string keyPath, string valueName) =>
            _values.TryGetValue((keyPath, valueName), out var data) ? data : null;
    }
}
