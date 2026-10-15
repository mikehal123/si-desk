using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Sovereign.Desktop.Connector;
using Xunit;

namespace XaiDesk.Tests;

public sealed class ConnectorSecretTests
{
    private const string Name = "SIDesk.GrokBotConnector";
    private const string LegacyName = "xAIDesk.GrokBotConnector";
    private const string WakeName = "SIDesk.GrokbotWake/sender-key";
    private const string LegacyWakeName = "xAIDesk.GrokbotWake/sender-key";

    [Fact]
    public void Token_is_stable_across_two_loads()
    {
        var slot = new MemorySlot();
        var first = new StoredConnectorSecrets(slot, Name).LoadOrCreate();
        var second = new StoredConnectorSecrets(slot, Name).LoadOrCreate();
        Assert.False(string.IsNullOrWhiteSpace(first));
        Assert.Equal(first, second);
        Assert.Equal(1, slot.Writes);
    }

    [Fact]
    public void Generate_and_revoke_touch_only_the_new_name()
    {
        var slot = new MemorySlot { [LegacyName] = "old-token-value", ["UpdaterDesk.GrokBotConnector"] = "other-app-token" };
        var secrets = new StoredConnectorSecrets(slot, Name);
        var first = secrets.LoadOrCreate();
        var regenerated = secrets.Generate();
        Assert.NotEqual(first, regenerated);
        Assert.Equal(regenerated, new StoredConnectorSecrets(slot, Name).Current());
        secrets.Revoke();
        Assert.Null(secrets.Current());
        Assert.Equal("old-token-value", slot.Read(LegacyName));
        Assert.Equal("other-app-token", slot.Read("UpdaterDesk.GrokBotConnector"));
    }

    [Fact]
    public void Migration_copies_both_keys_and_leaves_old_entries()
    {
        var profile = TempProfile();
        try
        {
            var vault = new MemorySlot { [LegacyName] = "legacy-connector-token" };
            var creds = new MemorySlot { [LegacyWakeName] = "legacy-wake-key" };
            var copied = LegacySecretMigration.RunOnce(profile, Pairs(vault, creds));
            Assert.Equal([Name, WakeName], copied);
            Assert.Equal("legacy-connector-token", vault.Read(Name));
            Assert.Equal("legacy-wake-key", creds.Read(WakeName));
            Assert.Equal("legacy-connector-token", vault.Read(LegacyName));
            Assert.Equal("legacy-wake-key", creds.Read(LegacyWakeName));
            Assert.True(File.Exists(Path.Combine(profile, LegacySecretMigration.MarkerFileName)));
            Assert.Equal("legacy-connector-token", new StoredConnectorSecrets(vault, Name).LoadOrCreate());
        }
        finally { Directory.Delete(profile, true); }
    }

    [Fact]
    public void Migration_keeps_existing_new_entries_skips_when_legacy_is_absent_and_runs_once()
    {
        var profile = TempProfile();
        try
        {
            var vault = new MemorySlot { [Name] = "current-token", [LegacyName] = "legacy-connector-token" };
            var creds = new MemorySlot();
            Assert.Empty(LegacySecretMigration.RunOnce(profile, Pairs(vault, creds)));
            Assert.Equal("current-token", vault.Read(Name));
            Assert.Null(creds.Read(WakeName));

            vault.Remove(Name);
            creds.Write(LegacyWakeName, "legacy-wake-key");
            Assert.Empty(LegacySecretMigration.RunOnce(profile, Pairs(vault, creds)));
            Assert.Null(vault.Read(Name));
            Assert.Null(creds.Read(WakeName));
        }
        finally { Directory.Delete(profile, true); }
    }

    [Fact]
    public void Migration_without_marker_retries_after_a_failed_copy()
    {
        var profile = TempProfile();
        try
        {
            var vault = new MemorySlot { [LegacyName] = "legacy-connector-token", FailWrites = true };
            var creds = new MemorySlot();
            Assert.Empty(LegacySecretMigration.RunOnce(profile, Pairs(vault, creds)));
            Assert.False(File.Exists(Path.Combine(profile, LegacySecretMigration.MarkerFileName)));
            vault.FailWrites = false;
            Assert.Equal([Name], LegacySecretMigration.RunOnce(profile, Pairs(vault, creds)));
        }
        finally { Directory.Delete(profile, true); }
    }

    [Fact]
    public void Migration_copies_a_real_windows_credential()
    {
        if (!OperatingSystem.IsWindows()) return;
        var id = Guid.NewGuid().ToString("N");
        var legacy = "xAIDesk.GrokbotWake/test-" + id;
        var current = "SIDesk.GrokbotWake/test-" + id;
        var profile = TempProfile();
        var slot = new WindowsCredentialSlot();
        try
        {
            slot.Write(legacy, "wake-key-test-value");
            Assert.Equal([current], LegacySecretMigration.RunOnce(profile, [new LegacySecret(slot, legacy, current)]));
            Assert.Equal("wake-key-test-value", slot.Read(current));
            Assert.Equal("wake-key-test-value", slot.Read(legacy));
        }
        finally
        {
            slot.Remove(legacy);
            slot.Remove(current);
            Directory.Delete(profile, true);
        }
    }

    [Fact]
    public void Address_in_use_is_found_in_inner_exceptions()
    {
        var socket = new SocketException((int)SocketError.AddressAlreadyInUse);
        Assert.True(ConnectorBind.IsAddressInUse(socket));
        Assert.True(ConnectorBind.IsAddressInUse(new IOException("Failed to bind.", socket)));
        Assert.True(ConnectorBind.IsAddressInUse(new IOException("Failed to bind to address http://127.0.0.1:1: address already in use.")));
        Assert.False(ConnectorBind.IsAddressInUse(new IOException("Access denied.")));
        Assert.False(ConnectorBind.IsAddressInUse(null));
        Assert.Equal("Port 8790 busy, retrying", ConnectorBind.BusyStatus(8790));
    }

    [Fact]
    public async Task Retry_loop_binds_after_the_port_frees()
    {
        var blocker = new TcpListener(IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
        blocker.Start();
        var port = ((IPEndPoint)blocker.LocalEndpoint).Port;
        var busyCalls = 0;
        var busySeen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = NewHost();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var start = host.ListenWithRetryForChecksAsync(IPAddress.Loopback, port, ConnectorBindMode.Loopback, status =>
        {
            Interlocked.Increment(ref busyCalls);
            busySeen.TrySetResult(status);
        }, TimeSpan.FromMilliseconds(100), timeout.Token);
        try
        {
            Assert.Equal("Port " + port + " busy, retrying", await busySeen.Task.WaitAsync(timeout.Token));
            Assert.False(start.IsCompleted);
        }
        finally { blocker.Stop(); }

        var error = await start.WaitAsync(timeout.Token);
        Assert.Null(error);
        Assert.Equal(1, busyCalls);
        Assert.False(host.PortBusy);
        Assert.True(host.IsBound);
        Assert.Equal("http://127.0.0.1:" + port + "/mcp", host.Url);
        using var http = new HttpClient();
        using var response = await http.PostAsync(host.Url, new StringContent("{}"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Retry_loop_stops_when_cancelled()
    {
        var blocker = new TcpListener(IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
        blocker.Start();
        try
        {
            var port = ((IPEndPoint)blocker.LocalEndpoint).Port;
            var busySeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var host = NewHost();
            using var cancel = new CancellationTokenSource();
            var start = host.ListenWithRetryForChecksAsync(IPAddress.Loopback, port, ConnectorBindMode.Loopback,
                _ => busySeen.TrySetResult(), TimeSpan.FromMilliseconds(100), cancel.Token);
            await busySeen.Task.WaitAsync(TimeSpan.FromSeconds(30));
            cancel.Cancel();
            try { await start.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (OperationCanceledException) { }
            Assert.True(start.IsCompleted);
            Assert.False(host.Running);
        }
        finally { blocker.Stop(); }
    }

    [Fact]
    public async Task Other_bind_errors_do_not_retry()
    {
        await using var host = NewHost();
        var busy = 0;
        var error = await host.ListenWithRetryForChecksAsync(IPAddress.Parse("192.0.2.1"), 0, ConnectorBindMode.Loopback,
            _ => busy++, TimeSpan.FromMilliseconds(10));
        Assert.Equal("Refusing an address that is not loopback or Tailscale.", error);
        Assert.Equal(0, busy);
    }

    private static LegacySecret[] Pairs(MemorySlot vault, MemorySlot creds) =>
        [new LegacySecret(vault, LegacyName, Name), new LegacySecret(creds, LegacyWakeName, WakeName)];

    private static string TempProfile()
    {
        var path = Path.Combine(Path.GetTempPath(), "sidesk-secret-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static ConnectorHost NewHost() =>
        new(new MemoryConnectorSecrets(), DispatchProxy.Create<IGrokBotDesk, UnusedDesk>(),
            new ConnectorAudit(Path.Combine(Path.GetTempPath(), "sidesk-retry-audit-" + Guid.NewGuid().ToString("N") + ".log")));

    public class UnusedDesk : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException("The retry checks do not call desk tools.");
    }

    private sealed class MemorySlot : Dictionary<string, string>, ISecretSlot
    {
        public int Writes { get; private set; }
        public bool FailWrites { get; set; }
        public string? Read(string name) => TryGetValue(name, out var value) ? value : null;
        public void Write(string name, string value)
        {
            if (FailWrites) throw new IOException("Write refused.");
            Writes++;
            this[name] = value;
        }
        void ISecretSlot.Remove(string name) => base.Remove(name);
    }
}
