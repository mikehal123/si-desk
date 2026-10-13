using System.Text.Json;
using Sovereign.Desktop.Client;
using Sovereign.Desktop.Phone;

namespace Sovereign.Desktop.Phone.Tests;

public static class GrantChecks
{
    public static async Task<int> RunAsync()
    {
        var failed = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) { failed.Add(name); Console.Error.WriteLine("FAIL " + name); }
            else Console.WriteLine("ok " + name);
        }
        var workspace = Path.Combine(Path.GetTempPath(), "sovereign-phone-grant-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var parameters = JsonDocument.Parse("""
            {
              "reason": "edit the note",
              "itemId": "item-1",
              "threadId": "thread-1",
              "permissions": { "fileSystem": { "entries": [ { "path": { "type": "path", "path": "note.txt" }, "access": "write" } ] } }
            }
            """).RootElement;
        var project = new CursorProjectOptions { Mode = CursorProjectMode.Agent, Permissions = CursorPermissionPolicy.AlwaysAsk };
        var gate = new PhoneApprovalGate();
        var confirmed = false;
        Func<CursorPermissionRequest, Task<string?>> ask = request =>
        {
            var waiting = new TaskCompletionSource<string?>();
            var id = gate.Track(request, waiting, CancellationToken.None, "Desk");
            Check(PhoneApprovalGate.IsDestructive(request.Tool), "codex write permission is destructive");
            var blocked = gate.Decide(id, true, false);
            Check(!blocked.Accepted && !waiting.Task.IsCompleted, "codex write is held until the second tap");
            confirmed = gate.Decide(id, true, true).Accepted;
            return waiting.Task;
        };
        var granted = await CodexAppSession.BuildPermissionGrantAsync(workspace, project, parameters, true, ask);
        Check(confirmed && Access(granted) == "write", "confirmed phone allow uses the shared codex grant");

        var deniedGate = new PhoneApprovalGate();
        Func<CursorPermissionRequest, Task<string?>> deny = request =>
        {
            var waiting = new TaskCompletionSource<string?>();
            var id = deniedGate.Track(request, waiting, CancellationToken.None, "Desk");
            deniedGate.Decide(id, false, false);
            return waiting.Task;
        };
        var denied = await CodexAppSession.BuildPermissionGrantAsync(workspace, project, parameters, true, deny);
        Check(Access(denied) == "deny", "phone deny uses the shared codex grant");
        if (failed.Count == 0) return 0;
        Console.Error.WriteLine(failed.Count + " grant checks failed");
        return 1;
    }

    private static string Access(Dictionary<string, object?> grant)
    {
        var permissions = (Dictionary<string, object?>)grant["permissions"]!;
        var fileSystem = (Dictionary<string, object>)permissions["fileSystem"]!;
        var entries = (List<object>)fileSystem["entries"]!;
        var first = (Dictionary<string, object>)entries[0];
        return (string)first["access"];
    }
}
