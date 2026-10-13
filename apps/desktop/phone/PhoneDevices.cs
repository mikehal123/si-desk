using System.Security.Cryptography;
using System.Text.Json;

namespace Sovereign.Desktop.Phone;

public sealed record PhoneSettings(bool Enabled, PhoneBindMode Bind, bool Notify)
{
    public static PhoneSettings Off { get; } = new(false, PhoneBindMode.Loopback, false);
}

public sealed record PhoneDeviceRecord(string Id, string Name, string TokenHash, DateTimeOffset Created, DateTimeOffset LastSeen);

public enum PhonePairStatus { Ok, Invalid, Expired, RateLimited }

public sealed record PhonePairResult(PhonePairStatus Status, string? Token, string? DeviceId, string? Name);

/// <summary>Pairing codes and device tokens. Only SHA-256 hashes are written. Raw tokens are returned once and never logged.</summary>
public sealed class PhoneDeviceStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public string Profile { get; }
    private readonly string _settingsPath;
    private readonly string _devicesPath;
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _pairAttempts = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _sends = new();
    private readonly List<PhoneDeviceRecord> _devices = [];
    private string? _pairHash;
    private DateTimeOffset _pairExpires;
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
    public PhoneSettings Settings { get; private set; } = PhoneSettings.Off;

    public PhoneDeviceStore(string profile)
    {
        Profile = profile;
        var folder = Path.Combine(profile, "phone");
        _settingsPath = Path.Combine(folder, "settings.json");
        _devicesPath = Path.Combine(folder, "devices.json");
        Settings = ReadSettings();
        _devices.AddRange(ReadDevices());
    }

    public IReadOnlyList<PhoneDeviceRecord> Devices { get { lock (_gate) return _devices.ToArray(); } }

    public void SaveSettings(PhoneSettings settings)
    {
        lock (_gate)
        {
            Settings = settings;
            WriteAtomic(_settingsPath, JsonSerializer.Serialize(new SettingsFile(settings.Enabled, settings.Bind == PhoneBindMode.Tailscale ? "tailscale" : "loopback", settings.Notify), Json));
        }
    }

    public string BeginPairing()
    {
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        lock (_gate)
        {
            _pairHash = Hash(code);
            _pairExpires = Clock().AddMinutes(10);
        }
        return code;
    }

    public PhonePairResult Redeem(string? code, string? name)
    {
        lock (_gate)
        {
            if (!Charge(_pairAttempts, TimeSpan.FromMinutes(10), 5)) return new(PhonePairStatus.RateLimited, null, null, null);
            if (string.IsNullOrWhiteSpace(code) || _pairHash is null) return new(PhonePairStatus.Invalid, null, null, null);
            if (Clock() > _pairExpires) { _pairHash = null; return new(PhonePairStatus.Expired, null, null, null); }
            var presented = Hash(code.Trim());
            var match = CryptographicOperations.FixedTimeEquals(DecodeHex(_pairHash), DecodeHex(presented));
            if (!match) return new(PhonePairStatus.Invalid, null, null, null);
            _pairHash = null;
            var label = CleanName(name);
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var device = new PhoneDeviceRecord(Guid.NewGuid().ToString("N"), label, Hash(token), Clock(), Clock());
            _devices.Add(device);
            PersistDevices();
            return new(PhonePairStatus.Ok, token, device.Id, device.Name);
        }
    }

    public bool Authorize(string? token, out PhoneDeviceRecord? device)
    {
        device = null;
        if (string.IsNullOrWhiteSpace(token)) return false;
        var presented = Hash(token.Trim());
        var presentedBytes = DecodeHex(presented);
        lock (_gate)
        {
            PhoneDeviceRecord? found = null;
            foreach (var candidate in _devices)
            {
                var same = CryptographicOperations.FixedTimeEquals(DecodeHex(candidate.TokenHash), presentedBytes);
                if (same) found = candidate;
            }
            if (found is null) return false;
            var updated = found with { LastSeen = Clock() };
            var index = _devices.FindIndex(item => item.Id == found.Id);
            if (index >= 0) _devices[index] = updated;
            device = updated;
            PersistDevices();
            return true;
        }
    }

    public bool Revoke(string id)
    {
        lock (_gate)
        {
            var removed = _devices.RemoveAll(item => item.Id == id) > 0;
            if (removed) { _sends.Remove(id); PersistDevices(); }
            return removed;
        }
    }

    public bool AllowSend(string deviceId)
    {
        lock (_gate)
        {
            if (!_sends.TryGetValue(deviceId, out var attempts)) _sends[deviceId] = attempts = new();
            return Charge(attempts, TimeSpan.FromSeconds(10), 10);
        }
    }

    private bool Charge(Queue<DateTimeOffset> attempts, TimeSpan window, int limit)
    {
        var now = Clock();
        while (attempts.Count > 0 && now - attempts.Peek() > window) attempts.Dequeue();
        if (attempts.Count >= limit) return false;
        attempts.Enqueue(now);
        return true;
    }

    private void PersistDevices() => WriteAtomic(_devicesPath, JsonSerializer.Serialize(new DeviceFile(_devices.ToArray()), Json));

    private PhoneSettings ReadSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return PhoneSettings.Off;
            var file = JsonSerializer.Deserialize<SettingsFile>(File.ReadAllText(_settingsPath));
            if (file is null) return PhoneSettings.Off;
            var bind = file.Bind == "tailscale" ? PhoneBindMode.Tailscale : PhoneBindMode.Loopback;
            return new PhoneSettings(file.Enabled, bind, file.Notify);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return PhoneSettings.Off; }
    }

    private PhoneDeviceRecord[] ReadDevices()
    {
        try
        {
            if (!File.Exists(_devicesPath)) return [];
            return JsonSerializer.Deserialize<DeviceFile>(File.ReadAllText(_devicesPath))?.Devices ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    private static void WriteAtomic(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, json); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static byte[] DecodeHex(string hex)
    {
        try { return Convert.FromHexString(hex); }
        catch (FormatException) { return new byte[32]; }
    }

    private static string CleanName(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0 || trimmed.Length > 40 || trimmed.Any(char.IsControl)) return "Phone";
        return trimmed;
    }

    private sealed record SettingsFile(bool Enabled, string Bind, bool Notify);
    private sealed record DeviceFile(PhoneDeviceRecord[] Devices);
}

public static class PhoneLog
{
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var cleaned = System.Text.RegularExpressions.Regex.Replace(text, @"Bearer\s+\S+", "Bearer [redacted]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return System.Text.RegularExpressions.Regex.Replace(cleaned, @"\b[0-9a-fA-F]{32,}\b", "[redacted]");
    }
}
