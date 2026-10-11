using System.Diagnostics;
using System.Globalization;

namespace Sovereign.Desktop.Client;

/// <summary>Calls the existing Chroma 384-d model. A 1024-d vector is refused. Pgvector is not used.</summary>
public sealed class Chroma384Embedder(string scriptPath) : ISkillEmbedder
{
    private bool _unavailable;

    public float[]? Embed(string text)
    {
        if (_unavailable || string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath)) return null;
        var python = PythonExecutable();
        if (python is null) { _unavailable = true; return null; }
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo(python, "\"" + scriptPath + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (!process.Start()) return null;
            process.StandardInput.Write(text);
            process.StandardInput.Close();
            if (!process.WaitForExit(12000))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                _unavailable = true;
                return null;
            }
            if (process.ExitCode != 0) { _unavailable = true; return null; }
            var parts = process.StandardOutput.ReadToEnd().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != SkillLibrary.Dimensions) return null;
            var vector = new float[SkillLibrary.Dimensions];
            for (var i = 0; i < parts.Length; i++)
                if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out vector[i])) return null;
            return SkillLibrary.Accept(vector);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return null; }
    }

    private static string? PythonExecutable()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), "python.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
