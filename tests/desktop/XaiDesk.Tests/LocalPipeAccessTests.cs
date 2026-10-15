using System.IO.Pipes;
using Sovereign.Desktop.Client;
using Sovereign.Desktop.Client.Host;
using Xunit;

namespace XaiDesk.Tests;

public sealed class LocalPipeAccessTests
{
    [Fact]
    public void Server_options_limit_the_pipe_to_the_current_user_and_the_first_instance()
    {
        Assert.True(LocalPipeAccess.RestrictsToCurrentUser(LocalPipeAccess.Server));
        Assert.True(LocalPipeAccess.FailsIfNameExists(LocalPipeAccess.Server));
        Assert.Equal(PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance, LocalPipeAccess.Server);
        Assert.Equal(PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, LocalPipeAccess.Client);
        Assert.Equal(PipeOptions.CurrentUserOnly, LocalPipeAccess.SynchronousClient);
        Assert.False(LocalPipeAccess.RestrictsToCurrentUser(PipeOptions.Asynchronous));
        Assert.False(LocalPipeAccess.FailsIfNameExists(PipeOptions.CurrentUserOnly));
    }

    [Fact]
    public void Pipe_names_and_the_prefix_strip_are_unchanged()
    {
        var pipe = InstalledHostSupervisor.ExpectedPipeName("pabc");
        Assert.Equal(@"\\.\pipe\SovereignAI.Offline.pabc.v2", pipe);
        Assert.Equal("SovereignAI.Offline.pabc.v2", NamedPipeTransport.NormalizePipeName(pipe));
        Assert.Equal("already-short", NamedPipeTransport.NormalizePipeName("already-short"));
    }

    [Fact]
    public async Task Host_client_connects_to_a_current_user_server_and_a_second_server_cannot_take_the_name()
    {
        var name = "si-desk-local-" + Guid.NewGuid().ToString("N");
        await using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, LocalPipeAccess.Server))
        {
            using var transport = new NamedPipeTransport();
            var connect = transport.ConnectAsync(@"\\.\pipe\" + name, TimeSpan.FromSeconds(2), CancellationToken.None);
            var wait = server.WaitForConnectionAsync();
            await Task.WhenAll(connect, wait);
            Assert.True(transport.IsConnected);
            Assert.True(server.IsConnected);

            var occupied = Record.Exception(() =>
                new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, LocalPipeAccess.Server));
            Assert.True(occupied is IOException or UnauthorizedAccessException, occupied?.ToString());
        }

        await using var recreated = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, LocalPipeAccess.Server);
        Assert.False(recreated.IsConnected);
    }

    [Fact]
    public async Task Open_file_handoff_still_delivers_the_exact_path()
    {
        var key = Guid.NewGuid().ToString("N")[..16];
        var sent = @"C:\shell-open\exact path.md";
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        ShellOpenHost? listener = null;
        ShellOpenHost? handoff = null;
        try
        {
            listener = ShellOpenHost.Start(key, [], path => received.TrySetResult(path));
            Assert.True(listener.Listening);
            handoff = ShellOpenHost.Start(key, [sent], _ => { });
            Assert.True(handoff.ExitProcess);
            Assert.Equal(sent, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            handoff?.Dispose();
            listener?.Dispose();
        }
    }

    [Fact]
    public void Call_sites_use_the_shared_pipe_options()
    {
        var root = FindRepoRoot();
        var open = File.ReadAllText(Path.Combine(root, "apps", "desktop", "Sovereign.Desktop.Client", "ShellOpenFiles.cs"));
        var transport = File.ReadAllText(Path.Combine(root, "apps", "desktop", "Sovereign.Desktop.Client", "Host", "NamedPipeTransport.cs"));
        Assert.Contains("LocalPipeAccess.Server", open, StringComparison.Ordinal);
        Assert.Contains("LocalPipeAccess.SynchronousClient", open, StringComparison.Ordinal);
        Assert.Contains("SIDesk.SingleInstance.", open, StringComparison.Ordinal);
        Assert.Contains("LocalPipeAccess.Client", transport, StringComparison.Ordinal);
        Assert.DoesNotContain("PipeOptions.Asynchronous);", transport, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))
                && Directory.Exists(Path.Combine(dir.FullName, "apps", "desktop")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("No Directory.Build.props above the test output contains apps\\desktop.");
    }
}
