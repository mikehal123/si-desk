namespace Sovereign.Desktop.Connector;

static class DoctrineWrite
{
    [ThreadStatic] public static bool Allow;
}

/// <summary>
/// One claim at a time for a board file. A thread may re-enter for the same path.
/// Other threads in this process wait on the monitor. Other processes wait on the lock file.
/// </summary>
static class SwarmBoardLock
{
    [ThreadStatic] static string? _held;
    [ThreadStatic] static int _depth;
    [ThreadStatic] static object? _monitor;
    [ThreadStatic] static FileStream? _stream;

    static readonly Dictionary<string, object> Gates = new(StringComparer.Ordinal);

    public static IDisposable Acquire(string boardPath)
    {
        var key = Path.GetFullPath(boardPath);
        if (_depth > 0 && string.Equals(_held, key, StringComparison.Ordinal))
        {
            _depth++;
            return new Releaser(nested: true);
        }

        var monitor = Gate(key);
        Monitor.Enter(monitor);
        FileStream? stream = null;
        try
        {
            var directory = Path.GetDirectoryName(key);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            stream = Open(key + ".lock");
            _held = key;
            _depth = 1;
            _monitor = monitor;
            _stream = stream;
            return new Releaser(nested: false);
        }
        catch
        {
            stream?.Dispose();
            _held = null;
            _depth = 0;
            _monitor = null;
            _stream = null;
            Monitor.Exit(monitor);
            throw;
        }
    }

    static object Gate(string key)
    {
        lock (Gates)
        {
            if (!Gates.TryGetValue(key, out var gate)) Gates[key] = gate = new object();
            return gate;
        }
    }

    static FileStream Open(string path)
    {
        var until = Environment.TickCount64 + 8000;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (Environment.TickCount64 < until)
            {
                Thread.Sleep(15);
            }
        }
    }

    sealed class Releaser(bool nested) : IDisposable
    {
        public void Dispose()
        {
            if (nested)
            {
                _depth = Math.Max(0, _depth - 1);
                return;
            }

            var monitor = _monitor;
            _stream?.Dispose();
            _stream = null;
            _depth = 0;
            _held = null;
            _monitor = null;
            if (monitor is not null) Monitor.Exit(monitor);
        }
    }
}
