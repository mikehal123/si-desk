using Sovereign.Desktop.Client;
using System.Text.Json;

var install = args[0]; var profile = args[1]; var receipt = args[2];
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
await using var host = new SharedRoomHost(profile, "QA host"); await host.StartAsync(timeout.Token);
using var tunnel = new RoomTunnel(); var address = await tunnel.StartAsync(install,profile,host.Address,timeout.Token);
// Only synthetic QA messages are exposed. Invitations and tokens are never printed or persisted.
using(var probe = new HttpClient()) {
    var reachable = false;
    for(var attempt=0;attempt<12;attempt++) {
        try { using var r=await probe.GetAsync(address,timeout.Token); if(r.IsSuccessStatusCode) { reachable=true; break; } } catch(HttpRequestException) { }
        await Task.Delay(2000,timeout.Token);
    }
    if(!reachable) throw new IOException("Temporary public relay did not become reachable.");
}
using var owner = new RoomConnection(host.Address,host.Owner);
using var peer = await RoomConnection.JoinAsync(host.NewInvitation(address),"QA remote",timeout.Token);
var sent = new RoomPost(Guid.NewGuid(),"human","","Synthetic two-way relay verification.");
await owner.SendAsync(sent,timeout.Token); await owner.SendAsync(sent,timeout.Token);
var observed = await peer.ReadAsync(0,timeout.Token);
if(observed.Messages.Length!=1) throw new Exception("Remote delivery or idempotency failed.");
await peer.SendAsync(new RoomPost(Guid.NewGuid(),"human","","Synthetic reply through HTTPS."),timeout.Token);
var all=await owner.ReadAsync(0,timeout.Token);
if(all.Messages.Length!=2 || all.Members.Length!=2) throw new Exception("Return delivery failed.");
var result=new { passed=true,remoteHttps=true,members=2,messages=2,duplicateRetrySuppressed=true,liveAiGeneration="not_run",note="Two clients on this PC; remote route traversed Cloudflare. Caesar's device/network not yet tested." };
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(receipt))!);
await File.WriteAllTextAsync(receipt,JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine(JsonSerializer.Serialize(result));
