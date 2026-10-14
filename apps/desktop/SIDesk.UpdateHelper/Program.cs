using Sovereign.Desktop.Updates;

// Started by SI Desk from a copy in %TEMP%, so nothing in the app folder is locked while it swaps.
var logPath = Path.Combine(AppContext.BaseDirectory, "update.log");
void Log(string line)
{
    try { File.AppendAllText(logPath, DateTimeOffset.Now.ToString("u") + " " + line + Environment.NewLine); }
    catch (IOException) { }
}

SiDeskHelperRequest request;
try { request = SiDeskHelperRequest.Parse(args); }
catch (ArgumentException ex)
{
    Log("Bad arguments: " + ex.Message);
    return 2;
}

var result = await SiDeskUpdateHelper.RunAsync(request, new SiDeskProcessHost(), SiDeskInstall.ProtectedPaths(request.ProfileDirectory),
    TimeSpan.FromSeconds(90), Log);
if (result == 0)
{
    foreach (var zip in Directory.EnumerateFiles(AppContext.BaseDirectory, "SIDesk-*-win-x64.zip"))
    {
        try { File.Delete(zip); }
        catch (IOException) { }
    }
}
Log("Exit " + result);
return result;
