using System.Net;
using System.Net.Http.Json;
using Sovereign.Desktop.Client;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class SharedRoomTests
{
    [Fact]
    public async Task Two_participants_share_ordered_messages_and_retries_do_not_duplicate()
    {
        var profile = Path.Combine(Path.GetTempPath(), "sovereign-room-test-" + Guid.NewGuid().ToString("N"));
        await using var host = new SharedRoomHost(profile, "user"); await host.StartAsync();
        using var owner = new RoomConnection(host.Address, host.Owner);
        var invitation = host.NewInvitation(host.Address);
        using var peer = await RoomConnection.JoinAsync(invitation, "Caesar", CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(() => RoomConnection.JoinAsync(invitation, "Intruder", CancellationToken.None));
        var post = new RoomPost(Guid.NewGuid(), "human", "", "Hello Caesar");
        var first = await owner.SendAsync(post, CancellationToken.None);
        Assert.Equal(first, await owner.SendAsync(post, CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => owner.SendAsync(post with { Text = "Different payload" }, CancellationToken.None));
        await peer.SendAsync(new RoomPost(Guid.NewGuid(), "human", "", "Hello user"), CancellationToken.None);
        await peer.SendAsync(new RoomPost(Guid.NewGuid(), "ai", "Test model", "This is a test reply, not a live model generation."), CancellationToken.None);
        var snapshot = await owner.ReadAsync(0, CancellationToken.None);
        Assert.Equal(2, snapshot.Members.Length); Assert.Equal(3, snapshot.Messages.Length);
        Assert.Equal("Caesar", snapshot.Messages[2].Name); Assert.Equal("ai", snapshot.Messages[2].Kind);
        Assert.Single((await peer.ReadAsync(2, CancellationToken.None)).Messages);
        var disk = await File.ReadAllTextAsync(Path.Combine(profile,"shared-rooms",host.RoomId,"transcript.json"));
        Assert.Contains("Hello Caesar", disk); Assert.DoesNotContain(host.Owner.Token, disk); Assert.DoesNotContain(peer.Session.Token, disk);
        await peer.LeaveAsync(CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(()=>peer.ReadAsync(0,CancellationToken.None));
        using var rejoined=await RoomConnection.JoinAsync(host.NewInvitation(host.Address),"Caesar",CancellationToken.None);
        Assert.Equal(3,(await rejoined.ReadAsync(0,CancellationToken.None)).Messages.Length);
    }
    [Fact]
    public async Task Anonymous_wrong_room_origin_and_rotated_invite_are_refused()
    {
        await using var host = new SharedRoomHost(Path.Combine(Path.GetTempPath(), "sovereign-room-test-" + Guid.NewGuid().ToString("N")), "Owner"); await host.StartAsync();
        using var http = new HttpClient { BaseAddress = host.Address };
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync($"/room/{host.RoomId}/messages")).StatusCode);
        var old = host.NewInvitation(host.Address); var current = host.NewInvitation(host.Address);
        await Assert.ThrowsAsync<IOException>(()=>RoomConnection.JoinAsync(old,"Peer",CancellationToken.None));
        using var peer = await RoomConnection.JoinAsync(current,"Peer",CancellationToken.None);
        using var wrong = new RoomConnection(host.Address, peer.Session with { RoomId = new string('0',32) });
        await Assert.ThrowsAsync<IOException>(()=>wrong.ReadAsync(0,CancellationToken.None));
        http.DefaultRequestHeaders.Add("Origin", "https://attacker.invalid");
        Assert.Equal(HttpStatusCode.Forbidden,(await http.GetAsync("/")).StatusCode);
        Assert.Throws<ArgumentException>(() => RoomConnection.ValidateAddress(new Uri("http://192.168.1.20/")));
        Assert.Throws<ArgumentException>(() => RoomConnection.ValidateAddress(new Uri("https://user:pass@example.com/")));
        await Assert.ThrowsAsync<IOException>(() => peer.SendAsync(new RoomPost(Guid.NewGuid(),"system","","fake authority"),CancellationToken.None));
    }
}
