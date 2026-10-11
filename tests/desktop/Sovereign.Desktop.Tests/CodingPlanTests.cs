using System.Net;
using Sovereign.Desktop.Client;
using Xunit;
namespace Sovereign.Desktop.Tests;
public sealed class CodingPlanTests
{
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> handler):HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(handler(request)); }
    [Fact]
    public async Task Coding_plan_error_never_retries_on_paid_endpoint_or_leaks_response()
    {
        var count=0;
        using var client=new CodingPlanChatClient("glm",CodingPlanChatClient.Endpoints("glm")[0],"test-key",new Handler(r=>{count++; Assert.Contains("/coding/",r.RequestUri!.AbsolutePath); return new(HttpStatusCode.Forbidden){Content=new StringContent("secret diagnostic")};}));
        var ex=await Assert.ThrowsAsync<IOException>(()=>client.SendAsync("test",[],_=>{},CancellationToken.None));
        Assert.Equal(1,count); Assert.DoesNotContain("secret",ex.Message);
    }
    [Fact]
    public async Task Streaming_requires_normal_finish_not_truncation()
    {
        using var good=new CodingPlanChatClient("kimi",CodingPlanChatClient.Endpoints("kimi")[0],"test-key",new Handler(_=>new(HttpStatusCode.OK){Content=new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"},\"finish_reason\":null}]}\n\ndata: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n")}));
        var text=""; await good.SendAsync("test",[],s=>text+=s,CancellationToken.None); Assert.Equal("Hello",text);
        using var bad=new CodingPlanChatClient("kimi",CodingPlanChatClient.Endpoints("kimi")[0],"test-key",new Handler(_=>new(HttpStatusCode.OK){Content=new StringContent("data: [DONE]\n")}));
        await Assert.ThrowsAsync<IOException>(()=>bad.SendAsync("test",[],_=>{},CancellationToken.None));
    }
    [Fact]
    public void Imports_only_the_selected_provider_and_refuses_ambiguous_accounts()
    {
        var file=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".json");
        try {
            File.WriteAllText(file,"{\"credential_pool\":{\"zai\":[{\"auth_type\":\"api_key\",\"access_token\":\"glm-test\"}],\"openai-codex\":[{\"access_token\":\"do-not-import\"}]}}");
            Assert.Equal("glm-test",ImigrokCredentialImport.ReadKey(file,"glm"));
            Assert.Throws<IOException>(()=>ImigrokCredentialImport.ReadKey(file,"kimi"));
            File.WriteAllText(file,"{\"providers\":{\"zai\":{\"api_key\":\"one\"}},\"credential_pool\":{\"zai\":[{\"api_key\":\"two\"}]}}");
            Assert.Throws<IOException>(()=>ImigrokCredentialImport.ReadKey(file,"glm"));
        } finally { File.Delete(file); }
    }
    [Fact]
    public void Only_coding_plan_endpoints_are_accepted()
    { Assert.Throws<ArgumentException>(()=>new CodingPlanChatClient("glm","https://api.z.ai/api/paas/v4/","test")); }
}
