using Sovereign.Desktop.Client;
using Xunit;

namespace XaiDesk.Tests;

public sealed class SharedRoomTests
{
    [Fact(Timeout = 20000)]
    public async Task Host_and_guest_exchange_a_human_post_on_loopback()
    {
        var profile = Path.Combine(Path.GetTempPath(), "SIDesk", "room-" + Guid.NewGuid().ToString("N"));
        Assert.Contains("SIDesk", profile);
        Assert.DoesNotContain("Sovereign" + "AI", profile);

        SharedRoomHost? host = null;
        try
        {
            host = new SharedRoomHost(profile, "Ada");
            await host.StartAsync();
            Assert.True(host.Address.IsLoopback);

            using var owner = new RoomConnection(host.Address, host.Owner);
            var invitation = host.NewInvitation(host.Address);
            using var guest = await RoomConnection.JoinAsync(invitation, "Bea", CancellationToken.None);
            const string text = "Hello from the room";
            await owner.SendAsync(new RoomPost(Guid.NewGuid(), "human", "", text), CancellationToken.None);

            var snapshot = await guest.ReadAsync(0, CancellationToken.None);
            var message = Assert.Single(snapshot.Messages);
            Assert.Equal("human", message.Kind);
            Assert.Equal(text, message.Text);
            Assert.Equal("Ada", message.Name);
        }
        finally
        {
            if (host is not null) await host.DisposeAsync();
            try
            {
                if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
