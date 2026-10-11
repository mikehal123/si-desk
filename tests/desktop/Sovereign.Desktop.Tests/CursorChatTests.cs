using System.Text.Json;
using Sovereign.Desktop.Client;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class CursorChatTests
{
    private const string Model = "grok-4.7[context=256k,reasoning_effort=high,fast=true]";
    private static string Reply(int id, object result) => JsonSerializer.Serialize(new { jsonrpc="2.0", id, result });
    private static string Update(string kind, string text, string session="s1") => JsonSerializer.Serialize(new {
        jsonrpc="2.0", method="session/update", @params=new {sessionId=session,update=new {sessionUpdate=kind, content=new {type="text",text}}} });
    private static string[] Connect => [Reply(1,new {protocolVersion=1}), Reply(2,new {sessionId="s1",models=new {availableModels=new[] {
        new {modelId="default[]",name="Auto"},new {modelId=Model,name="grok-4.7"}}}}),Reply(3,new {})];
    private static (CursorAcpSession Session, StringWriter Sent) Create(params string[] tail)
    {
        var output=new StringWriter();
        return (new CursorAcpSession(new StringReader(string.Join("\n",Connect.Concat(tail))+"\n"),output),output);
    }

    [Fact]
    public async Task Advertised_model_is_selected_and_only_final_text_is_streamed()
    {
        var (client,sent)=Create(Reply(4,new {}),Update("agent_thought_chunk","not a final reply"),Update("agent_message_chunk","Hello "),Update("agent_message_chunk","world"),Reply(5,new {stopReason="end_turn"}));
        await client.ConnectAsync("test-workspace",CancellationToken.None);
        Assert.Single(client.Models); Assert.Equal("Grok 4.7 High Fast",client.Models[0].ToString());
        var answer="";await client.SendAsync(Model,"hi",s=>answer+=s,CancellationToken.None);
        Assert.Equal("Hello world",answer);Assert.Equal("end_turn",client.LastStopReason);Assert.Equal(Model,client.SelectedModelId);
        var messages=sent.ToString().Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(s=>JsonDocument.Parse(s).RootElement.Clone()).ToArray();
        Assert.Equal("ask",messages[2].GetProperty("params").GetProperty("modeId").GetString());
        Assert.Equal(Model,messages[3].GetProperty("params").GetProperty("modelId").GetString());
        Assert.False(messages[0].GetProperty("params").GetProperty("clientCapabilities").GetProperty("terminal").GetBoolean());
    }

    [Fact]
    public async Task Unadvertised_model_refused_without_sending_a_prompt()
    {
        var (client,sent)=Create();await client.ConnectAsync("workspace",CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(()=>client.SendAsync("grok-4.7-high-fast","hi",_=>{},CancellationToken.None));
        Assert.DoesNotContain("session/prompt",sent.ToString());
    }

    [Theory]
    [InlineData("cancelled","partial")]
    [InlineData("cancelled","")]
    [InlineData("max_tokens","partial")]
    [InlineData("end_turn","")]
    public async Task Partial_or_empty_reply_is_not_success(string reason,string text)
    {
        var (client,_)=Create(Reply(4,new {}),Update("agent_message_chunk",text),Reply(5,new {stopReason=reason}));
        await client.ConnectAsync("workspace",CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(()=>client.SendAsync(Model,"hi",_=>{},CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Wrong_session_or_eof_cannot_complete_a_turn(bool wrongSession)
    {
        var (client,_)=Create(Reply(4,new {}),Update("agent_message_chunk","partial",wrongSession?"other":"s1"));
        await client.ConnectAsync("workspace",CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(()=>client.SendAsync(Model,"hi",_=>{},CancellationToken.None));
    }

    [Fact]
    public async Task Tools_and_file_requests_are_never_approved()
    {
        var (client,sent)=Create(Reply(4,new {}),
            "{\"jsonrpc\":\"2.0\",\"id\":\"permission\",\"method\":\"session/request_permission\",\"params\":{}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"file\",\"method\":\"fs/read_text_file\",\"params\":{}}",
            Update("agent_message_chunk","Cannot access files."),Reply(5,new {stopReason="end_turn"}));
        await client.ConnectAsync("workspace",CancellationToken.None);await client.SendAsync(Model,"hi",_=>{},CancellationToken.None);
        Assert.Contains("\"outcome\":\"cancelled\"",sent.ToString());Assert.Contains("\"code\":-32601",sent.ToString());Assert.DoesNotContain("allow-once",sent.ToString());
    }

    [Fact]
    public async Task Cancellation_during_stream_does_not_complete()
    {
        using var cancel=new CancellationTokenSource();
        var (client,_)=Create(Reply(4,new {}),Update("agent_message_chunk","partial"),Reply(5,new {stopReason="end_turn"}));
        await client.ConnectAsync("workspace",cancel.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>client.SendAsync(Model,"hi",_=>cancel.Cancel(),cancel.Token));
        Assert.Null(client.LastStopReason);
    }
}
