using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Sovereign.Desktop.Client;

public static class ShellOpenFiles
{
    public static readonly string[] Extensions = [".md", ".txt", ".json"];

    private static readonly HashSet<string> FlagsWithValue = new(StringComparer.Ordinal)
    {
        "--mode",
        "--profile",
        "--scenario",
        "--archive",
        "--smoke-dump",
        "--walkthrough-out",
        "--capture-dir",
        "--reviewed-case-verify",
        "--desk-store-verify",
        "--chat-layout-verify",
        "--chat-steering-verify",
        "--quant-desk-verify",
        "--codex-chat-verify",
        "--ollama-chat-verify",
        "--ollama-verify-model",
        "--ollama-verify-prompts",
        "--host-profile",
        "--test-dpi"
    };

    public static IReadOnlyList<string> Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var files = new List<string>();
        var index = 0;
        if (args.Count > 0 && IsRuntimeBinary(args[0]))
            index = 1;

        for (; index < args.Count; index++)
        {
            var token = args[index];
            if (IsFlag(token))
            {
                if (FlagsWithValue.Contains(token) && index + 1 < args.Count && !IsFlag(args[index + 1]))
                    index++;
                continue;
            }

            if (IsOpenFile(token))
                files.Add(Path.GetFullPath(token));
        }

        return files;
    }

    public static string InstanceKey(string executablePath)
    {
        if (string.IsNullOrEmpty(executablePath))
            throw new ArgumentException("Executable path is required.", nameof(executablePath));

        var fullPath = Path.GetFullPath(executablePath);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(fullPath));
        return Convert.ToHexStringLower(hash)[..32];
    }

    private static bool IsRuntimeBinary(string token) =>
        token.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
        token.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

    private static bool IsFlag(string token) => token.StartsWith("--", StringComparison.Ordinal);

    private static bool IsOpenFile(string token)
    {
        var extension = Path.GetExtension(token);
        foreach (var candidate in Extensions)
        {
            if (extension.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

public sealed class ShellOpenHost : IDisposable
{
    private const int MaxFrameBytes = 32768;
    private const int ConnectTimeoutMilliseconds = 2000;
    private const string MutexPrefix = @"Local\xAIDesk.SingleInstance.";
    private const string PipePrefix = "xAIDesk.SingleInstance.";

    // WaitOne is recursive for the owning thread. A second Start in this process must hand the file
    // off instead of taking that extra ownership and opening another listener.
    private static readonly ConcurrentDictionary<string, byte> OwnedKeys = new(StringComparer.Ordinal);

    private readonly object _gate = new();
    private readonly string? _instanceKey;
    private readonly Mutex? _mutex;
    private readonly bool _ownsMutex;
    private ManualResetEventSlim? _ready;
    private Thread? _thread;
    private NamedPipeServerStream? _server;
    private volatile Exception? _startupFailure;
    private int _disposed;

    private ShellOpenHost(bool exitProcess, bool listening, string? instanceKey, Mutex? mutex, bool ownsMutex)
    {
        ExitProcess = exitProcess;
        Listening = listening;
        _instanceKey = instanceKey;
        _mutex = mutex;
        _ownsMutex = ownsMutex;
    }

    public bool ExitProcess { get; }

    public bool Listening { get; }

    public static ShellOpenHost Start(string instanceKey, IReadOnlyList<string> files, Action<string> deliver)
    {
        if (!IsInstanceKey(instanceKey))
            throw new ArgumentException("Instance key must be 1..40 characters from [A-Za-z0-9._-].", nameof(instanceKey));
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(deliver);

        var pipeName = PipePrefix + instanceKey;
        if (OwnedKeys.ContainsKey(instanceKey))
            return Handoff(pipeName, files);

        // initiallyOwned: false. new Mutex(true, name) would block until the current owner releases.
        var mutex = new Mutex(false, MutexPrefix + instanceKey, out var createdNew);
        _ = createdNew;
        bool owned;
        try
        {
            try
            {
                owned = mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                owned = true;
            }
        }
        catch
        {
            mutex.Dispose();
            throw;
        }

        if (!owned || !OwnedKeys.TryAdd(instanceKey, 0))
        {
            if (owned)
            {
                try { mutex.ReleaseMutex(); }
                catch (ApplicationException) { }
            }

            mutex.Dispose();
            return Handoff(pipeName, files);
        }

        var host = new ShellOpenHost(exitProcess: false, listening: true, instanceKey, mutex, ownsMutex: true);
        try
        {
            host.Listen(pipeName, deliver);
            return host;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        NamedPipeServerStream? server;
        lock (_gate)
        {
            server = _server;
            _server = null;
        }

        try { server?.Dispose(); }
        catch { }

        if (_thread is { } thread && !ReferenceEquals(thread, Thread.CurrentThread))
        {
            try { thread.Join(TimeSpan.FromSeconds(2)); }
            catch { }
        }

        if (_ownsMutex)
        {
            if (_instanceKey is not null)
                OwnedKeys.TryRemove(_instanceKey, out _);
            try { _mutex?.ReleaseMutex(); }
            catch { }
            try { _mutex?.Dispose(); }
            catch { }
        }

        try { _ready?.Dispose(); }
        catch { }
    }

    private static ShellOpenHost Handoff(string pipeName, IReadOnlyList<string> files)
    {
        if (files.Count == 0)
            return new ShellOpenHost(exitProcess: false, listening: false, instanceKey: null, mutex: null, ownsMutex: false);

        Send(pipeName, files);
        return new ShellOpenHost(exitProcess: true, listening: false, instanceKey: null, mutex: null, ownsMutex: false);
    }

    private void Listen(string pipeName, Action<string> deliver)
    {
        var ready = new ManualResetEventSlim(false);
        _ready = ready;
        _thread = new Thread(() => AcceptLoop(pipeName, deliver, ready))
        {
            IsBackground = true,
            Name = "xAIDesk.OpenFile"
        };
        _thread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(5)))
            throw new IOException("Open-file listener did not start.");
        if (_startupFailure is not null)
            throw new IOException("Open-file listener failed to start.", _startupFailure);
    }

    private void AcceptLoop(string pipeName, Action<string> deliver, ManualResetEventSlim ready)
    {
        try
        {
            while (Volatile.Read(ref _disposed) == 0)
            {
                NamedPipeServerStream server;
                try
                {
                    server = new NamedPipeServerStream(
                        pipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);
                }
                catch (Exception ex)
                {
                    if (!ready.IsSet)
                        _startupFailure = ex;
                    return;
                }

                lock (_gate)
                {
                    if (Volatile.Read(ref _disposed) != 0)
                    {
                        server.Dispose();
                        return;
                    }

                    _server = server;
                }

                try
                {
                    try { ready.Set(); }
                    catch (ObjectDisposedException) { return; }

                    server.WaitForConnection();
                    ReadFrames(server, deliver);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (IOException) when (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }
                catch (IOException)
                {
                }
                finally
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_server, server))
                            _server = null;
                    }

                    try { server.Dispose(); }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            if (!ready.IsSet)
                _startupFailure = ex;
        }
        finally
        {
            try { ready.Set(); }
            catch { }
        }
    }

    private static void Send(string pipeName, IReadOnlyList<string> files)
    {
        var frames = new List<byte[]>(files.Count);
        foreach (var path in files)
        {
            if (path is null)
                throw new IOException("Open-file path is missing.");
            var body = Encoding.UTF8.GetBytes(path);
            if (body.Length is < 1 or > MaxFrameBytes)
                throw new IOException("Open-file path must be 1..32768 UTF-8 bytes.");
            frames.Add(body);
        }

        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            client.Connect(ConnectTimeoutMilliseconds);
            var header = new byte[4];
            foreach (var body in frames)
            {
                BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
                client.Write(header, 0, header.Length);
                client.Write(body, 0, body.Length);
            }

            client.Flush();
        }
        catch (IOException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new IOException("Could not hand off the open-file path to the running instance.", ex);
        }
    }

    private static void ReadFrames(Stream stream, Action<string> deliver)
    {
        var header = new byte[4];
        while (TryReadExact(stream, header))
        {
            var count = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (count is < 1 or > MaxFrameBytes)
                return;

            var body = new byte[count];
            if (!TryReadExact(stream, body))
                return;

            var path = Encoding.UTF8.GetString(body);
            try { deliver(path); }
            catch { }
        }
    }

    private static bool TryReadExact(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            int read;
            try
            {
                read = stream.Read(buffer, offset, buffer.Length - offset);
            }
            catch (IOException)
            {
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }

            if (read == 0)
                return false;
            offset += read;
        }

        return true;
    }

    private static bool IsInstanceKey(string? instanceKey)
    {
        if (instanceKey is not { Length: >= 1 and <= 40 })
            return false;

        foreach (var character in instanceKey)
        {
            if (character is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or '-')
                continue;
            return false;
        }

        return true;
    }
}
