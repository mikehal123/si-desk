using Sovereign.Desktop.Client;
using System.Text.Json;
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
using var ollama = new OllamaChatClient();
var models = await ollama.ModelsAsync(timeout.Token);
var answer = "";
try
{
    await ollama.SendAsync(args.FirstOrDefault() ?? "derivatives-structurer:latest", [new("user", "Reply with only the word READY. Do not use tools.")], s => answer += s, timeout.Token);
    Console.WriteLine(JsonSerializer.Serialize(new { ollama = "completed", models, response = answer }));
}
catch (Exception ex) { Console.WriteLine(JsonSerializer.Serialize(new { ollama = "failed", models, error = ex.Message })); }
using var codex = new CodexChatClient(Path.Combine(Path.GetTempPath(), "Sovereign-chat-smoke-" + Guid.NewGuid().ToString("N")));
var signedIn = await codex.IsSubscriptionAsync(timeout.Token);
Console.WriteLine(JsonSerializer.Serialize(new { codexHandshake = "passed", subscriptionSignedIn = signedIn, note = "Isolated Codex home; user sign-in required. No cloud turn sent." }));
