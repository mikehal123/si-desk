using Xunit;

namespace XaiDesk.Tests;

public sealed class IsolationSourceTests
{
    private static readonly string[] Forbidden =
    [
        "SovereignAI.DesktopPreview",
        "SovereignAI.CodingPlan",
        "SovereignAI.MarketData",
        "8788"
    ];

    private static readonly string[] RelativeFiles =
    [
        Path.Combine("apps", "desktop", "Sovereign.Desktop", "App.xaml.cs"),
        Path.Combine("apps", "desktop", "Sovereign.Desktop", "SettingsPage.cs"),
        Path.Combine("apps", "desktop", "Sovereign.Desktop", "ChatRoom.cs"),
        Path.Combine("apps", "desktop", "phone", "PhoneBindAddress.cs"),
        Path.Combine("apps", "desktop", "Sovereign.Desktop.Client", "ShellOpenFiles.cs"),
        Path.Combine("apps", "desktop", "Sovereign.Desktop.Updates", "UpdateTrust.cs")
    ];

    [Fact]
    public void Desktop_sources_omit_sovereign_identity_literals()
    {
        var root = FindSourceRoot();
        var failures = new List<string>();
        foreach (var relative in RelativeFiles)
        {
            var path = Path.Combine(root, relative);
            if (!File.Exists(path))
            {
                failures.Add(relative + " is missing");
                continue;
            }

            var text = File.ReadAllText(path);
            foreach (var literal in Forbidden)
            {
                if (text.Contains(literal, StringComparison.Ordinal))
                    failures.Add(relative + " contains " + literal);
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static string FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))
                && Directory.Exists(Path.Combine(dir.FullName, "apps", "desktop")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("No Directory.Build.props above the test output contains apps\\desktop.");
    }
}
