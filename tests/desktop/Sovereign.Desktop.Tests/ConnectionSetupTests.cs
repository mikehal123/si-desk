using Sovereign.Desktop.Client;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class ConnectionSetupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "sov-connections-" + Guid.NewGuid().ToString("N"));
    private string FileAt(params string[] parts) { var file = Path.Combine(new[] { root }.Concat(parts).ToArray()); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, "test only"); return file; }
    [Fact] public void Finds_native_binary_behind_npm_wrapper()
    {
        FileAt("npm", "codex.cmd");
        var exe = FileAt("npm", "node_modules", "@openai", "codex", "node_modules", "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "codex", "codex.exe");
        Assert.Equal(exe, ConnectionSetup.FindCodex(null, root, root, Path.Combine(root,"npm")));
    }
    [Fact] public void Wrapper_without_native_binary_is_not_claimed_installed()
    {
        FileAt("npm", "codex.cmd");
        Assert.Throws<FileNotFoundException>(() => ConnectionSetup.FindCodex(null,root,root,Path.Combine(root,"npm")));
    }
    [Fact] public void Explicit_existing_path_wins_and_missing_override_falls_back()
    {
        var chosen = FileAt("custom", "codex.exe"); var fallback = FileAt("bin", "codex.exe");
        Assert.Equal(chosen, ConnectionSetup.FindCodex(chosen,root,root,Path.Combine(root,"bin")));
        Assert.Equal(fallback, ConnectionSetup.FindCodex(Path.Combine(root,"missing.exe"),root,root,Path.Combine(root,"bin")));
    }
    [Fact] public void Relative_path_entries_are_not_executed()
    {
        Assert.Throws<FileNotFoundException>(() => ConnectionSetup.FindCodex("codex.exe",root,root,"."));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root,true); }
}
