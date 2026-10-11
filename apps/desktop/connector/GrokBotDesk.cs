using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sovereign.Desktop.Connector;

public enum ConnectorBindMode { Loopback, Tailscale }

public static class ConnectorBind
{
    public const int Port = 8790;

    public static bool IsTailscale(IPAddress address)
    {
        address = V4(address);
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127;
    }

    public static bool IsLoopbackV4(IPAddress address)
    {
        address = V4(address);
        return address.AddressFamily == AddressFamily.InterNetwork && IPAddress.IsLoopback(address);
    }

    /// <summary>Loopback is 127.0.0.1. Tailscale is never taken from the NIC list; use <see cref="ParseTailscaleIp"/>.</summary>
    public static IPAddress? Select(ConnectorBindMode mode, IEnumerable<IPAddress> addresses)
    {
        _ = addresses;
        if (mode == ConnectorBindMode.Loopback) return IPAddress.Loopback;
        return null;
    }

    public static IPAddress? ParseTailscaleIp(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (IPAddress.TryParse(line, out var address) && IsTailscale(address)) return address;
        return null;
    }

    public static async Task<(IPAddress? Address, string? Error)> QueryTailscaleIpv4Async(CancellationToken ct = default)
    {
        var executable = FindTailscale();
        if (executable is null) return (null, "Tailscale is not installed. The connector stayed off.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var (code, stdout, stderr) = await RunTailscaleAsync(executable, timeout.Token);
            var address = ParseTailscaleIp(stdout);
            if (code == 0 && address is not null) return (address, null);
            var detail = OneLine(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
            return (null, detail.Length == 0
                ? "tailscale ip -4 did not return an address. The connector stayed off."
                : "tailscale ip -4 failed. The connector stayed off. " + detail);
        }
        catch (OperationCanceledException)
        {
            return (null, "tailscale ip -4 timed out. The connector stayed off.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return (null, "tailscale ip -4 could not run. The connector stayed off. " + OneLine(ex.Message));
        }
    }

    public static bool IsListening(IPAddress address, int port)
    {
        foreach (var end in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
            if (end.Port == port && SameAddress(end.Address, address)) return true;
        return false;
    }

    public static string OneLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var line = text.Trim().Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        return line.Length <= 180 ? line : line[..180];
    }

    private static IPAddress V4(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool SameAddress(IPAddress listener, IPAddress requested) => V4(listener).Equals(V4(requested));

    private static string? FindTailscale()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };
        foreach (var root in roots)
        {
            var candidate = Path.Combine(root, "Tailscale", "tailscale.exe");
            if (File.Exists(candidate)) return candidate;
        }
        var path = Environment.GetEnvironmentVariable("PATH");
        if (path is null) return null;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), "tailscale.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static async Task<(int Code, string Stdout, string Stderr)> RunTailscaleAsync(string executable, CancellationToken ct)
    {
        var info = new ProcessStartInfo(executable, "ip -4")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        using var process = Process.Start(info) ?? throw new IOException("tailscale.exe did not start.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
            return (process.ExitCode, await stdoutTask, await stderrTask);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch (Exception) { }
            throw;
        }
    }

    public static bool ConnectionAllowed(IPAddress? localAddress, ConnectorBindMode mode)
    {
        if (localAddress is null) return false;
        return mode == ConnectorBindMode.Loopback ? IsLoopbackV4(localAddress) : IsTailscale(localAddress);
    }

    public static IReadOnlyList<IPAddress> MachineAddresses()
    {
        var found = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork) found.Add(unicast.Address);
        }
        return found;
    }
}

public sealed record ConnectorSettings(bool Enabled, ConnectorBindMode Bind)
{
    public static ConnectorSettings Off { get; } = new(false, ConnectorBindMode.Loopback);

    /// <summary>A failed bind leaves the saved on/off choice and bind mode unchanged.</summary>
    public static ConnectorSettings AfterFailedStart(ConnectorSettings chosen)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        return chosen;
    }

    public static ConnectorSettings Load(string profile)
    {
        var path = Path.Combine(profile, "connector-settings.json");
        if (!File.Exists(path)) return Off;
        try
        {
            var saved = JsonSerializer.Deserialize<ConnectorSettings>(File.ReadAllText(path));
            return saved is null ? Off : saved with { Bind = saved.Bind == ConnectorBindMode.Tailscale ? ConnectorBindMode.Tailscale : ConnectorBindMode.Loopback };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Off;
        }
    }

    public void Save(string profile)
    {
        Directory.CreateDirectory(profile);
        var path = Path.Combine(profile, "connector-settings.json");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this));
        File.Move(temp, path, true);
    }
}

public interface IConnectorSecrets
{
    string? Current();
    string Generate();
    void Revoke();
}

/// <summary>Test double. The desktop stores the real token in Windows Credential Manager.</summary>
public sealed class MemoryConnectorSecrets : IConnectorSecrets
{
    private string? _token;
    public string? Current() => _token;
    public string Generate()
    {
        _token = ConnectorToken.Create();
        return _token;
    }
    public void Revoke() => _token = null;
}

public static class ConnectorToken
{
    public static string Create() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool Matches(string? saved, string presented)
    {
        if (string.IsNullOrEmpty(saved) || string.IsNullOrEmpty(presented)) return false;
        var left = Encoding.UTF8.GetBytes(saved);
        var right = Encoding.UTF8.GetBytes(presented);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}

public sealed class ConnectorAudit
{
    private readonly string _path;
    private readonly object _gate = new();

    public ConnectorAudit(string path) => _path = path;

    public void Write(string tool, string? chat)
    {
        var line = DateTimeOffset.UtcNow.ToString("o") + "\t" + Clean(tool) + "\t" + Clean(chat) + "\n";
        lock (_gate)
        {
            var folder = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            File.AppendAllText(_path, line);
        }
    }

    public static string DefaultPath() =>
        Path.Combine(Sovereign.Desktop.Core.XaiDeskIdentity.DataDirectory(), "connector", "audit.log");

    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        var text = value.Trim().Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        return text.Length <= 80 ? text : text[..80];
    }
}

public sealed class ConnectorRateGate
{
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _posts = new();
    private readonly Func<DateTimeOffset> _clock;
    public const int Limit = 20;

    public ConnectorRateGate(Func<DateTimeOffset>? clock = null) => _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public bool TryPost()
    {
        lock (_gate)
        {
            var now = _clock();
            while (_posts.Count > 0 && now - _posts.Peek() >= TimeSpan.FromMinutes(1)) _posts.Dequeue();
            if (_posts.Count >= Limit) return false;
            _posts.Enqueue(now);
            return true;
        }
    }
}

public sealed record GrokBotMessage(string Author, string Text, string? Model);
public sealed record GrokBotChat(string Id, string Title);
public sealed record GrokBotSubagent(string Id, string Name, string Task, string State, string Output, string? Model);
public sealed record GrokBotStatus(string Version, string? Connection, string? Model, bool TurnRunning);

public interface IGrokBotDesk
{
    GrokBotStatus Status();
    IReadOnlyList<GrokBotChat> ListChats();
    IReadOnlyList<GrokBotMessage> ReadChat(string? conversation, int limit);
    void SendToChat(string conversation, string text);
    IReadOnlyList<GrokBotMessage> ReadSharedRoom(int limit);
    void PostSharedRoom(string text);
    IReadOnlyList<GrokBotSubagent> ListSubagents(string conversation);
    GrokBotSubagent? ReadSubagent(string id);
    void NoteGrokbotPoll();
    IReadOnlyList<GrokbotMail> ListGrokbotInbox(string? since);
    GrokbotMail ReplyGrokbotInbox(string messageId, string text);
    GrokbotPng CaptureGrokbotScreenshot();
    string GrokbotUiTree();
    string GrokbotClick(string? controlId, int? x, int? y);
    string GrokbotType(string controlId, string text);
    string GrokbotKey(string keys);
    string GrokbotNavigate(string page);
    void RecordGrokbotTool(string tool, string args, string result);
    string ReadSwarmBoard(string? swarmId);
    string SubmitSwarmResult(string taskId, string result, string proof);
}
