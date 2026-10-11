using System.Net;
using System.Text.Json;
using Sovereign.Desktop.Client;
using Xunit;
namespace Sovereign.Desktop.Tests;
public sealed class KimiPlatformTests
{
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> respond):HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(respond(request)); }
    [Fact]
    public async Task Platform_uses_its_explicit_endpoint_and_streams_a_reply()
    {
        using var client=new CodingPlanChatClient("moonshot","https://api.moonshot.ai/v1/","test-only",new Handler(r=> {
            Assert.Equal("https://api.moonshot.ai/v1/chat/completions",r.RequestUri!.ToString());
            var body=JsonDocument.Parse(r.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("disabled",body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
            return new(HttpStatusCode.OK){Content=new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n")};
        }));
        var answer=""; await client.SendAsync("kimi-k2.6",[],s=>answer+=s,CancellationToken.None); Assert.Equal("OK",answer);
    }
    [Fact]
    public async Task Platform_catalog_order_does_not_change_default_chat_model()
    {
        using var client=new CodingPlanChatClient("moonshot",CodingPlanChatClient.Endpoints("moonshot")[0],"test-only",new Handler(_ => new(HttpStatusCode.OK){Content=new StringContent("{\"data\":[{\"id\":\"kimi-k2.7-code\"},{\"id\":\"kimi-k2.6\"},{\"id\":\"kimi-k3\"}]}")}));
        Assert.Equal("kimi-k2.6",(await client.ModelsAsync(CancellationToken.None))[0]);
    }
    [Fact]
    public async Task Code_key_rejection_explains_platform_choice_without_retrying()
    {
        var calls=0;
        using var client=new CodingPlanChatClient("kimi",CodingPlanChatClient.Endpoints("kimi")[0],"test-only",new Handler(_=> { calls++; return new(HttpStatusCode.Unauthorized){Content=new StringContent("private-response")}; }));
        var ex=await Assert.ThrowsAsync<IOException>(()=>client.ModelsAsync(CancellationToken.None));
        Assert.Contains("Kimi Platform API",ex.Message); Assert.DoesNotContain("private-response",ex.Message); Assert.Equal(1,calls);
        Assert.Throws<ArgumentException>(()=>new CodingPlanChatClient("kimi","https://api.moonshot.ai/v1/","test-only"));
        Assert.Throws<ArgumentException>(()=>new CodingPlanChatClient("moonshot","https://api.kimi.com/coding/v1/","test-only"));
    }
    [Fact]
    public void Platform_chat_survives_reopening_without_changing_existing_provider_numbers()
    {
        var profile=Path.Combine(Path.GetTempPath(),"kimi-platform-"+Guid.NewGuid().ToString("N"));
        var store=new ChatArchive(profile);
        store.Save(ChatArchive.New(5) with { Model="kimi-k2.6",Draft="draft" });
        store.Save(ChatArchive.New(4));
        var saved=new ChatArchive(profile).Load();
        Assert.Contains(saved,c=>c.Provider==5 && c.Model=="kimi-k2.6" && c.Draft=="draft");
        Assert.Contains(saved,c=>c.Provider==4);
    }
}
