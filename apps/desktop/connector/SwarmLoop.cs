using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sovereign.Desktop.Connector;

public sealed record SwarmWorkerView(string SessionId, string Name, string Task, string State, string Output, string RolePrompt);

public static class SwarmLoop
{
    public static SwarmBoard OpenGoal(string goal, string title, string role, string details, string root)
    {
        var task = new SwarmTask
        {
            Id = "t-" + Guid.NewGuid().ToString("N")[..8],
            Title = title,
            Role = role,
            Details = details,
            Status = SwarmStatus.Todo
        };
        return SwarmBoard.Create(goal, [task], root);
    }

    public static SwarmWorkerView View(SwarmBoard board, SwarmTask task) => new(
        "swarm-" + board.Id + "-" + task.Id,
        task.Role + " · " + task.Title,
        task.Details,
        SwarmBoard.PanelState(task.Status),
        string.IsNullOrWhiteSpace(task.Result) ? task.Proof : task.Result,
        SwarmRoles.Worker(task, board.BoardPath));

    public static SwarmWorkerView Begin(SwarmBoard board, string taskId, string assignee)
    {
        var task = board.Start(taskId, assignee);
        return View(board, task);
    }

    public static async Task<SwarmTask> EngineerProofAsync(SwarmBoard board, string taskId, string workDir)
    {
        var task = board.TaskById(taskId);
        if (task.Status != SwarmStatus.Doing) throw new InvalidOperationException("Engineer proof starts from doing.");
        Directory.CreateDirectory(workDir);
        var file = Path.Combine(workDir, "proof.txt");
        var before = File.Exists(file) ? File.ReadAllText(file).Replace("\r", "").Replace("\n", "") : "";
        var body = "swarm phase 1 " + task.Id;
        File.WriteAllText(file, body + Environment.NewLine);
        var output = await CommandAsync("cmd.exe", "/d /c findstr /C:\"" + body + "\" \"" + file + "\"").ConfigureAwait(false);
        if (output.ExitCode != 0 || !output.Text.Contains(body, StringComparison.Ordinal))
            throw new IOException("Engineer proof command failed.");
        var diff = "--- a/proof.txt\n+++ b/proof.txt\n-" + before + "\n+" + body;
        var proof = VerifyResultSkill.Format(
            "proof.txt contains the line for " + task.Id,
            "Wrote proof.txt and ran findstr /C on that file.",
            "exit " + output.ExitCode + "\n" + output.Text.Trim(),
            "Success: the new proof.txt line is findable. Skipped: error, empty, cancel, and persistence were not part of this write.",
            "pass because findstr printed the task line");
        return board.Submit(taskId, diff + "\n" + proof, proof);
    }

    public static async Task<SwarmTask> VerifyProofAsync(SwarmBoard board, string taskId, string workDir)
    {
        var task = board.TaskById(taskId);
        var review = VerifyResultSkill.Review(task.Proof);
        if (!review.Pass) return board.Verify(taskId, false, review.Note);
        var file = Path.Combine(workDir, "proof.txt");
        if (!File.Exists(file))
            return board.Verify(taskId, false, VerifyResultSkill.Format(
                "The proof file exists",
                "Looked for proof.txt before rerunning findstr.",
                "(missing)",
                "The success path was not reached. Skipped: the file is not there.",
                "fail because the proof file is missing"));
        var body = File.ReadAllText(file).Trim();
        var output = await CommandAsync("cmd.exe", "/d /c findstr /C:\"" + body + "\" \"" + file + "\"").ConfigureAwait(false);
        var stamp = File.GetLastWriteTimeUtc(file).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var matched = output.ExitCode == 0
            && output.Text.Contains(body, StringComparison.Ordinal)
            && task.Proof.Contains(body, StringComparison.Ordinal);
        if (!matched)
            return board.Verify(taskId, false, VerifyResultSkill.Format(
                "proof.txt matches the submitted proof",
                "Reran findstr on proof.txt at " + stamp + ".",
                "exit " + output.ExitCode + "\n" + output.Text.Trim(),
                "The success path failed. Skipped: no further paths.",
                "fail because the proof did not match the file"));
        return board.Verify(taskId, true, VerifyResultSkill.Format(
            "proof.txt contains " + body,
            "Reran findstr on the current proof.txt at " + stamp + ". This is the file on disk, not a cached result.",
            "exit " + output.ExitCode + "\n" + output.Text.Trim(),
            "Success: the file exists and findstr matched. Skipped: error, empty, cancel, and persistence were not part of this file check.",
            "pass because findstr matched the current proof file"));
    }

    public static string WakeJson(SwarmBoard board, SwarmTask task)
    {
        var swarmId = board.Id;
        string? runId = null;
        var root = (board.Root ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Path.GetFileName(root).Equals("runs", StringComparison.OrdinalIgnoreCase))
        {
            runId = board.Id;
            var swarm = Path.GetFileName(Path.GetDirectoryName(root) ?? "");
            if (!string.IsNullOrEmpty(swarm)) swarmId = swarm;
        }
        var payload = new SwarmWakePayload(task.Id, false, GrokbotWake.Preview(task.Title), swarmId, task.Id, task.Title, task.Details ?? "", runId);
        return JsonSerializer.Serialize(payload);
    }

    public static async Task<(int ExitCode, string Text)> CommandAsync(string file, string arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(file, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        var text = stdout + stderr;
        return (process.ExitCode, text);
    }

    private sealed record SwarmWakePayload(
        [property: JsonPropertyName("message_id")] string MessageId,
        [property: JsonPropertyName("act")] bool Act,
        [property: JsonPropertyName("preview")] string Preview,
        [property: JsonPropertyName("swarm_id")] string SwarmId,
        [property: JsonPropertyName("task_id")] string TaskId,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("details")] string Details,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [property: JsonPropertyName("run_id")] string? RunId = null);
}
