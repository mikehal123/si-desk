using Sovereign.Desktop.G3Harness;

string? installDir = null;
string? profile = null;
string? receipt = Path.Combine(
    "D:", "xAI Desk-windows-desktop", "docs", "desktop", "evidence", "native-integration", "g3-installed-host-receipt.json");
var component = false;
for (var i = 0; i < args.Length; i++)
{
    string? Next() => i + 1 < args.Length ? args[++i] : null;
    switch (args[i])
    {
        case "--install-dir": installDir = Next(); break;
        case "--profile": profile = Next(); break;
        case "--receipt": receipt = Next(); break;
        case "--component": component = true; break;
    }
}

if (component)
{
    return await G3InterruptProbe.RunAndWriteAsync(receipt);
}

if (string.IsNullOrWhiteSpace(installDir) || string.IsNullOrWhiteSpace(profile))
{
    Console.Error.WriteLine("G3 installed probe requires --install-dir and --profile. Use --component only for the simulated-host client mapping.");
    return 2;
}

return await G3InstalledHostProbe.RunAndWriteAsync(installDir, profile, receipt);
