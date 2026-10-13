using Sovereign.Desktop.Client.Adapters;
using Sovereign.Desktop.Client.Scenarios;
using Sovereign.Desktop.Client.Storage;
using Sovereign.Desktop.Core;
using Sovereign.Desktop.Presentation;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class WalkthroughTests
{
    private static string Fixtures => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures"));

    [Fact]
    public async Task Closure_walkthrough_covers_archive_draft_export_modes_and_distinct_outcomes()
    {
        var profile = Path.Combine(Path.GetTempPath(), "sov-walk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        var vm = new ShellViewModel(
            new PreviewOperatorClient(ScenarioCatalog.Load(Fixtures, "admitted-blocked-execution")),
            new FileDraftStore(profile),
            profile,
            Fixtures);
        var receipt = await ClosureWalkthrough.RunAsync(vm, Fixtures);
        Assert.True(receipt.Passed, string.Join(Environment.NewLine, receipt.Steps.Select(s => $"{s.Id}: {s.Passed} {s.Detail}")));
        Assert.Equal(5, receipt.Steps.Count);
    }
}
