using Sovereign.Desktop.Client;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class ShellOpenFilesTests
{
    [Fact]
    public void Parse_skips_exe_argv0_and_keeps_document_paths_in_order()
    {
        Assert.Throws<ArgumentNullException>(() => ShellOpenFiles.Parse(null!));
        Assert.Equal([".md", ".txt", ".json"], ShellOpenFiles.Extensions);

        var markdown = @"C:\notes\launch.md";
        var text = @"C:\notes\launch.TXT";
        var json = "shell-open-" + Guid.NewGuid().ToString("N") + ".json";
        var afterSmoke = @"C:\notes\after-smoke.md";
        var afterMode = @"C:\notes\after-mode.md";
        var parsed = ShellOpenFiles.Parse(
        [
            @"C:\Program Files\SIDesk\SIDesk.exe",
            markdown,
            text,
            json,
            @"C:\notes\ignored.cs",
            "--profile",
            @"C:\notes.md",
            "--exit-after-smoke",
            afterSmoke,
            "--mode",
            "native",
            afterMode,
            markdown
        ]);

        Assert.Equal(
        [
            Path.GetFullPath(markdown),
            Path.GetFullPath(text),
            Path.GetFullPath(json),
            Path.GetFullPath(afterSmoke),
            Path.GetFullPath(afterMode),
            Path.GetFullPath(markdown)
        ], parsed);
    }

    [Fact]
    public void InstanceKey_is_stable_32_lowercase_hex_and_differs_by_executable()
    {
        Assert.Throws<ArgumentException>(() => ShellOpenFiles.InstanceKey(""));
        Assert.Throws<ArgumentException>(() => ShellOpenFiles.InstanceKey(null!));

        var main = @"C:\Program Files\SIDesk\SIDesk.exe";
        var qa = @"C:\Program Files\SIDesk\qa\SIDesk.exe";
        var first = ShellOpenFiles.InstanceKey(main);
        Assert.Equal(first, ShellOpenFiles.InstanceKey(main));
        Assert.Equal(32, first.Length);
        Assert.Matches("^[0-9a-f]{32}$", first);
        Assert.NotEqual(first, ShellOpenFiles.InstanceKey(qa));
    }

    [Fact]
    public async Task Second_start_with_a_file_exits_and_delivers_the_exact_path()
    {
        var key = NewKey();
        var sent = @"C:\shell-open\exact path.md";
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientDelivered = false;
        ShellOpenHost? listener = null;
        ShellOpenHost? handoff = null;
        try
        {
            listener = ShellOpenHost.Start(key, [], path => received.TrySetResult(path));
            Assert.True(listener.Listening);
            Assert.False(listener.ExitProcess);

            handoff = ShellOpenHost.Start(key, [sent], _ => clientDelivered = true);
            Assert.True(handoff.ExitProcess);
            Assert.False(handoff.Listening);
            Assert.False(clientDelivered);
            var delivered = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(sent, delivered);
        }
        finally
        {
            handoff?.Dispose();
            listener?.Dispose();
        }
    }

    [Fact]
    public void Second_start_without_a_file_does_not_exit_or_deliver()
    {
        var key = NewKey();
        var deliveries = 0;
        ShellOpenHost? listener = null;
        ShellOpenHost? second = null;
        try
        {
            listener = ShellOpenHost.Start(key, [], _ => Interlocked.Increment(ref deliveries));
            Assert.True(listener.Listening);
            Assert.False(listener.ExitProcess);

            second = ShellOpenHost.Start(key, [], _ => Interlocked.Increment(ref deliveries));
            Assert.False(second.ExitProcess);
            Assert.False(second.Listening);
            Assert.Equal(0, Volatile.Read(ref deliveries));
        }
        finally
        {
            second?.Dispose();
            listener?.Dispose();
        }
    }

    [Fact]
    public void Disposed_listener_lets_the_same_key_listen_again()
    {
        var key = NewKey();
        var first = ShellOpenHost.Start(key, [], _ => { });
        try
        {
            Assert.True(first.Listening);
            first.Dispose();
            var second = ShellOpenHost.Start(key, [], _ => { });
            try
            {
                Assert.True(second.Listening);
                Assert.False(second.ExitProcess);
            }
            finally
            {
                second.Dispose();
            }
        }
        finally
        {
            first.Dispose();
        }
    }

    [Fact]
    public void Different_instance_keys_can_listen_together()
    {
        ShellOpenHost? first = null;
        ShellOpenHost? second = null;
        try
        {
            first = ShellOpenHost.Start(NewKey(), [], _ => { });
            second = ShellOpenHost.Start(NewKey(), [], _ => { });
            Assert.True(first.Listening);
            Assert.False(first.ExitProcess);
            Assert.True(second.Listening);
            Assert.False(second.ExitProcess);
        }
        finally
        {
            second?.Dispose();
            first?.Dispose();
        }
    }

    [Fact]
    public void Dispose_twice_does_not_throw()
    {
        var host = ShellOpenHost.Start(NewKey(), [], _ => { });
        try
        {
            host.Dispose();
            host.Dispose();
        }
        finally
        {
            host.Dispose();
        }
    }

    private static string NewKey() => "t" + Guid.NewGuid().ToString("N");
}
