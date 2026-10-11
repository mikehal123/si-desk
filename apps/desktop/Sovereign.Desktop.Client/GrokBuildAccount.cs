using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Sovereign.Desktop.Client;

public sealed record GrokBuildStatusReport(string Version, string SignedInAs, string Plan, string? Error)
{
    public bool Ok => string.IsNullOrWhiteSpace(Error);
}

public static class GrokBuildAccount
{
    public static async Task<GrokBuildStatusReport> CheckAsync(CancellationToken ct = default)
    {
        var executable = GrokBuildChatClient.FindExecutable();
        if (executable is null)
            return new GrokBuildStatusReport("not found", "not logged in", "unknown", "Grok CLI was not found.");

        var version = await CaptureAsync(executable, "--version", TimeSpan.FromSeconds(15), ct);
        var auth = ReadAuthFile();
        var models = await CaptureAsync(executable, "models", TimeSpan.FromSeconds(20), ct);
        return Interpret(version.Stdout, auth, models.Stdout, models.Stderr);
    }

    public static GrokBuildStatusReport Interpret(string? versionOutput, string? authJson, string? modelsOutput, string? modelsError)
    {
        var email = EmailOf(authJson);
        var signedInAs = string.IsNullOrWhiteSpace(email) ? "not logged in" : email;
        var probe = ProbeLine(modelsOutput) ?? ProbeLine(modelsError);
        string? error = probe is null ? null : GrokBuildJson.Redact(probe);
        if (error is null && signedInAs == "not logged in") error = "not logged in";
        var plan = PlanOf((modelsOutput ?? "") + "\n" + (modelsError ?? ""));
        return new GrokBuildStatusReport(VersionOf(versionOutput), signedInAs, plan, error);
    }

    private static string VersionOf(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return "not found";
        foreach (var raw in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length > 0) return line;
        }
        return "not found";
    }

    private static string? EmailOf(string? authJson)
    {
        if (string.IsNullOrWhiteSpace(authJson)) return null;
        try
        {
            using var json = JsonDocument.Parse(authJson);
            if (json.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var account in json.RootElement.EnumerateObject())
            {
                if (account.Value.ValueKind != JsonValueKind.Object) continue;
                ReadName(account.Value, "first_name");
                ReadName(account.Value, "last_name");
                if (!account.Value.TryGetProperty("email", out var email) || email.ValueKind != JsonValueKind.String)
                    continue;
                var value = email.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }
        catch (JsonException)
        {
            return null;
        }
        return null;
    }

    private static void ReadName(JsonElement account, string property)
    {
        if (account.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
            _ = value.GetString();
    }

    private static string? ProbeLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var raw in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Contains("429", StringComparison.OrdinalIgnoreCase)
                || line.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                || line.Contains("usage limit", StringComparison.OrdinalIgnoreCase)
                || line.Contains("quota", StringComparison.OrdinalIgnoreCase)
                || line.Contains("free-usage-exhausted", StringComparison.OrdinalIgnoreCase)
                || line.Contains("free usage", StringComparison.OrdinalIgnoreCase)
                || line.Contains("not logged in", StringComparison.OrdinalIgnoreCase)
                || line.Contains("not signed in", StringComparison.OrdinalIgnoreCase))
                return line;
        }
        return null;
    }

    private static string PlanOf(string text)
    {
        if (HasWord(text, "free")) return "free";
        if (HasWord(text, "paid") || HasWord(text, "pro") || HasWord(text, "subscription")
            || text.Contains("team plan", StringComparison.OrdinalIgnoreCase))
            return "paid";
        return "unknown";
    }

    private static bool HasWord(string text, string word)
    {
        var start = 0;
        while (start < text.Length)
        {
            var index = text.IndexOf(word, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;
            var before = index == 0 || !char.IsAsciiLetterOrDigit(text[index - 1]);
            var end = index + word.Length;
            var after = end >= text.Length || !char.IsAsciiLetterOrDigit(text[end]);
            if (before && after) return true;
            start = index + 1;
        }
        return false;
    }

    private static string? ReadAuthFile()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok", "auth.json");
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static async Task<(string Stdout, string Stderr)> CaptureAsync(string executable, string argument, TimeSpan timeout, CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) return ("", "Grok CLI could not start.");

        // Read both pipes together. Draining stdout alone deadlocks once stderr fills.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(timeout);
        OperationCanceledException? cancelled = null;
        try { await process.WaitForExitAsync(timeoutSource.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { TryKill(process); }
        catch (OperationCanceledException ex)
        {
            cancelled = ex;
            TryKill(process);
        }
        finally { if (!process.HasExited) TryKill(process); }

        var stdout = await ReadPipeAsync(stdoutTask);
        var stderr = await ReadPipeAsync(stderrTask);
        if (cancelled is not null) throw cancelled;
        return (stdout, stderr);
    }

    private static async Task<string> ReadPipeAsync(Task<string> pipe)
    {
        try { return await pipe; }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { return ""; }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException) { }
    }
}
