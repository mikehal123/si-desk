using Sovereign.Desktop.Client;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class CodexRuntimeLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Sovereign-codex-runtime-" + Guid.NewGuid().ToString("N"));

    private string Deploy(string identity, int ageDays)
    {
        var path = Path.Combine(_root, "OpenAI", "Codex", "bin", identity, "codex.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "runtime discovery fixture");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-ageDays));
        return path;
    }

    [Fact]
    public void Current_desktop_runtime_precedes_legacy_alias_and_ignores_incomplete_deployments()
    {
        _ = Deploy("older", 3);
        var latest = Deploy("current", 0);
        Directory.CreateDirectory(Path.Combine(_root, "OpenAI", "Codex", "bin", "incomplete"));
        Assert.Equal(latest, CodexRuntimeLocator.FindExecutable(null, _root, () => throw new InvalidOperationException("Legacy runtime must not be selected")));
    }

    [Fact]
    public void Explicit_user_selection_is_preserved_and_missing_selection_falls_back_to_current_desktop()
    {
        var older = Deploy("older", 3);
        var latest = Deploy("current", 0);
        Assert.Equal(older, CodexRuntimeLocator.FindExecutable(older, _root, () => "fallback"));
        Assert.Equal(latest, CodexRuntimeLocator.FindExecutable(Path.Combine(_root, "missing", "codex.exe"), _root, () => "fallback"));
    }

    [Fact]
    public void Without_desktop_deployment_the_existing_connection_discovery_remains_available()
    {
        Assert.Equal("legacy-runtime", CodexRuntimeLocator.FindExecutable(null, _root, () => "legacy-runtime"));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
