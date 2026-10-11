using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Sovereign.Desktop.Connector;

public static class GrokbotWake
{
    public const string CredentialResource = "xAIDesk.GrokbotWake";
    public const string CredentialAccount = "sender-key";
    private static readonly Regex Secrets = new(
        @"bearer\s+\S+|api_key\s*=\s*\S+|access_token\s*=\s*\S+|password\s*=\s*\S+|sk-[A-Za-z0-9._\-]+|xai-[A-Za-z0-9._\-]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    public static string Preview(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var trimmed = text.Trim();
        if (trimmed.Length > 80) trimmed = trimmed[..80];
        try { return Secrets.Replace(trimmed, "[redacted]"); }
        catch (RegexMatchTimeoutException) { return "[redacted]"; }
    }

    public static async Task<string> PostAsync(HttpMessageHandler handler, string url, string bearer, string messageId, bool act, string preview, CancellationToken ct, Func<int, TimeSpan>? delay = null) =>
        (await DeliverAsync(handler, url, bearer, messageId, act, preview, ct, delay).ConfigureAwait(false)).Note;

    public static async Task<GrokbotWakeReceipt> DeliverAsync(HttpMessageHandler handler, string url, string bearer, string messageId, bool act, string preview, CancellationToken ct, Func<int, TimeSpan>? delay = null, string? swarmId = null, string? taskId = null, string? title = null, string? details = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (string.IsNullOrWhiteSpace(url)) return new GrokbotWakeReceipt("Wake webhook URL is not set.", null);
        if (string.IsNullOrEmpty(bearer)) return new GrokbotWakeReceipt("Webhook sender key is not set.", null);
        if (bearer.Any(char.IsControl)) return new GrokbotWakeReceipt("Webhook sender key is not valid.", null);
        if (!TryWakeUri(url, out var uri)) return new GrokbotWakeReceipt("Wake URL must be http or https with no user info.", null);
        var json = JsonSerializer.Serialize(new WakePayload(
            messageId ?? "",
            act,
            preview ?? "",
            swarmId is null ? null : swarmId,
            swarmId is null ? null : taskId,
            swarmId is null ? null : title,
            swarmId is null ? null : details));
        var pause = delay ?? DefaultDelay;
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        var error = "Wake failed.";
        int? status = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var retry = false;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, uri);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                request.Content = JsonContent(json);
                using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
                status = (int)response.StatusCode;
                if (response.IsSuccessStatusCode) return new GrokbotWakeReceipt("Grokbot pinged", status);
                error = "Wake failed. HTTP " + status + ".";
                retry = status == (int)HttpStatusCode.TooManyRequests || status >= 500;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                error = "Wake failed. Could not reach the webhook.";
                retry = true;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or FormatException)
            {
                error = ex is FormatException ? "Webhook sender key is not valid." : "Wake failed. Could not reach the webhook.";
                retry = ex is not FormatException;
            }
            if (!retry || attempt == 3) return new GrokbotWakeReceipt(Scrub(error, bearer), status);
            var wait = pause(attempt);
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
            await Task.Delay(wait, ct).ConfigureAwait(false);
        }
        return new GrokbotWakeReceipt(Scrub(error, bearer), status);
    }

    private static TimeSpan DefaultDelay(int attempt) => attempt switch
    {
        <= 1 => TimeSpan.FromMilliseconds(200),
        2 => TimeSpan.FromMilliseconds(800),
        _ => TimeSpan.FromMilliseconds(2000)
    };

    private static bool TryWakeUri(string url, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed)) return false;
        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(parsed.UserInfo) || string.IsNullOrWhiteSpace(parsed.Host)) return false;
        uri = parsed;
        return true;
    }

    private static ByteArrayContent JsonContent(string json)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private static string Scrub(string message, string bearer) =>
        string.IsNullOrEmpty(bearer) ? message : message.Replace(bearer, "[redacted]", StringComparison.Ordinal);

    private sealed record WakePayload(
        [property: JsonPropertyName("message_id")] string MessageId,
        [property: JsonPropertyName("act")] bool Act,
        [property: JsonPropertyName("preview")] string Preview,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [property: JsonPropertyName("swarm_id")] string? SwarmId = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [property: JsonPropertyName("task_id")] string? TaskId = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [property: JsonPropertyName("title")] string? Title = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [property: JsonPropertyName("details")] string? Details = null);
}

public sealed record GrokbotWakeCall(string MessageId, bool Act, string Preview, string? SwarmId = null, string? TaskId = null, string? Title = null, string? Details = null);

/// <summary>Trailing debounce for wake pings. <see cref="FlushAsync"/> sends the latest call without waiting out the quiet period.</summary>
public sealed class GrokbotWakeDebounce
{
    private readonly Func<GrokbotWakeCall, CancellationToken, Task> _send;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;
    private readonly TimeSpan _quiet;
    private readonly object _gate = new();
    private CancellationTokenSource? _waitSource;
    private int _version;
    private GrokbotWakeCall? _latest;

    public GrokbotWakeDebounce(Func<GrokbotWakeCall, CancellationToken, Task> send, Func<TimeSpan, CancellationToken, Task>? wait = null, TimeSpan? quiet = null)
    {
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _wait = wait ?? ((span, token) => Task.Delay(span, token));
        var window = quiet ?? TimeSpan.FromMilliseconds(400);
        _quiet = window < TimeSpan.Zero ? TimeSpan.Zero : window;
    }

    public void Schedule(GrokbotWakeCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        CancellationToken token;
        int version;
        lock (_gate)
        {
            _latest = call;
            version = ++_version;
            _waitSource?.Cancel();
            _waitSource = new CancellationTokenSource();
            token = _waitSource.Token;
        }
        _ = WaitAsync(version, token);
    }

    public async Task FlushAsync()
    {
        GrokbotWakeCall? call;
        lock (_gate)
        {
            _version++;
            call = _latest;
            _latest = null;
            _waitSource?.Cancel();
            _waitSource = null;
        }
        if (call is not null)
            await _send(call, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task WaitAsync(int version, CancellationToken token)
    {
        try
        {
            await _wait(_quiet, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        GrokbotWakeCall? call;
        lock (_gate)
        {
            if (version != _version) return;
            call = _latest;
            _latest = null;
            _waitSource = null;
        }
        if (call is null) return;
        try { await _send(call, CancellationToken.None).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }
}

public sealed record GrokbotWakeReceipt(string Note, int? StatusCode);

/// <summary>Webhook sender key in Windows Credential Manager. A missing key returns null and does not throw.</summary>
public static class GrokbotWakeKey
{
    public const string Target = "xAIDesk.GrokbotWake/sender-key";
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public static string? Read() => Read(Target);

    public static string? Read(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        if (!CredRead(target, CredTypeGeneric, 0, out var handle)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(handle);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize <= 0) return null;
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            var key = Encoding.Unicode.GetString(bytes);
            return string.IsNullOrEmpty(key) ? null : key;
        }
        finally
        {
            CredFree(handle);
        }
    }

    public static void Save(string key) => Save(Target, key);

    public static void Save(string target, string key)
    {
        if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("Wake key target is required.", nameof(target));
        if (string.IsNullOrEmpty(key) || key.Any(char.IsControl)) throw new ArgumentException("Webhook sender key is not valid.", nameof(key));
        var bytes = Encoding.Unicode.GetBytes(key);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = GrokbotWake.CredentialAccount
            };
            if (!CredWrite(ref credential, 0))
                throw new IOException("Wake key could not be saved. Win32 " + Marshal.GetLastWin32Error() + ".");
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    public static void Clear() => Clear(Target);

    public static void Clear(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        if (CredDelete(target, CredTypeGeneric, 0)) return;
        if (Marshal.GetLastWin32Error() == ErrorNotFound) return;
        throw new IOException("Wake key could not be cleared. Win32 " + Marshal.GetLastWin32Error() + ".");
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref NativeCredential credential, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public int Flags;
        public int Type;
        public string? TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }
}
