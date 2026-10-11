using System.Text.Json;
using Sovereign.Desktop.Client;
using Xunit;

namespace XaiDesk.Tests;

public sealed class GrokBuildAcpTests
{
    [Fact]
    public void Session_traffic_includes_the_two_extension_notification_names()
    {
        Assert.True(GrokBuildAcp.IsSessionUpdate("session/update"));
        Assert.True(GrokBuildAcp.IsSessionUpdate("_x.ai/session_notification"));
        Assert.True(GrokBuildAcp.IsSessionUpdate("_x.ai/session/update"));
        Assert.True(GrokBuildAcp.IsPromptComplete("_x.ai/session/prompt_complete"));
        Assert.False(GrokBuildAcp.IsSessionUpdate("_x.ai/session/prompt_complete"));
        Assert.True(GrokBuildAcp.IsSessionTraffic("_x.ai/session/prompt_complete"));
    }

    [Fact]
    public void Subagent_sample_json_reads_spawned_progress_and_finished_fields()
    {
        var spawned = Parse("""
            {"sessionUpdate":"subagent_spawned","subagent_id":"child-9","type":"explore","description":"Scan the repo"}
            """);
        Assert.True(GrokBuildAcp.TryParseSubagent(spawned, "subagent_spawned", out var born));
        Assert.Equal("child-9", born.Id);
        Assert.Equal("spawned", born.Phase);
        Assert.Equal("explore", born.Type);
        Assert.Equal("Scan the repo", born.Description);
        Assert.Equal("running", born.State);

        var progress = Parse("""
            {"sessionUpdate":"subagent_progress","subagent_id":"child-9","status":"running","turns":2,"tool_calls":4,"tokens_used":1200,"duration_ms":3500}
            """);
        Assert.True(GrokBuildAcp.TryParseSubagent(progress, "subagent_progress", out var working));
        Assert.Equal("running", working.State);
        Assert.Equal(2, working.Turns);
        Assert.Equal(4, working.ToolCalls);
        Assert.Equal(1200, working.TokensUsed);
        Assert.Equal(3500, working.DurationMs);
        Assert.Contains("2 turns", working.Progress);
        Assert.Contains("4 tool calls", working.Progress);
        Assert.Contains("1200 tokens", working.Progress);
        Assert.Contains("3500 ms", working.Progress);

        var aliased = Parse("""
            {"sessionUpdate":"subagent_progress","subagentId":"child-9","turn_count":1,"tool_call_count":1}
            """);
        Assert.True(GrokBuildAcp.TryParseSubagent(aliased, "subagent_progress", out var alias));
        Assert.Equal(1, alias.Turns);
        Assert.Equal(1, alias.ToolCalls);
        Assert.Contains("1 turn", alias.Progress);
        Assert.Contains("1 tool call", alias.Progress);

        var finished = Parse("""
            {"sessionUpdate":"subagent_finished","subagent_id":"child-9","type":"explore","description":"Scan the repo","status":"completed","duration_ms":9000,"turns":3,"tool_calls":5,"tokens_used":1800,"output":"Found 3 errors"}
            """);
        Assert.True(GrokBuildAcp.TryParseSubagent(finished, "subagent_finished", out var done));
        Assert.Equal("completed", done.State);
        Assert.Equal("Found 3 errors", done.Output);
        Assert.Equal(3, done.Turns);
        Assert.Equal(5, done.ToolCalls);
        Assert.Equal(1800, done.TokensUsed);
        Assert.Equal(9000, done.DurationMs);
        Assert.False(GrokBuildAcp.TryParseSubagent(finished, "agent_message_chunk", out _));
    }

    [Fact]
    public void LoadSession_is_true_only_for_a_boolean_true_flag()
    {
        Assert.True(GrokBuildAcp.SupportsLoadSession(Parse("""{"loadSession":true}""")));
        Assert.True(GrokBuildAcp.SupportsLoadSession(Parse("""{"agentCapabilities":{"loadSession":true}}""")));
        Assert.True(GrokBuildAcp.SupportsLoadSession(Parse("""{"agentCapabilities":{"sessionCapabilities":{"loadSession":true}}}""")));
        Assert.True(GrokBuildAcp.SupportsLoadSession(Parse("""{"capabilities":{"loadSession":true}}""")));
        Assert.False(GrokBuildAcp.SupportsLoadSession(Parse("""{"protocolVersion":1}""")));
        Assert.False(GrokBuildAcp.SupportsLoadSession(Parse("""{"loadSession":false}""")));
        Assert.False(GrokBuildAcp.SupportsLoadSession(Parse("""{"agentCapabilities":{"sessionCapabilities":{"loadSession":{}}}}""")));
    }

    [Fact]
    public void Permission_kinds_keep_allow_always_option_ids()
    {
        Assert.True(GrokBuildAcp.IsSelectablePermissionKind("allow_once"));
        Assert.True(GrokBuildAcp.IsSelectablePermissionKind("reject_once"));
        Assert.True(GrokBuildAcp.IsSelectablePermissionKind("allow_always"));
        Assert.True(GrokBuildAcp.IsSelectablePermissionKind("allow_always_command"));
        Assert.False(GrokBuildAcp.IsSelectablePermissionKind("reject_always"));
        Assert.False(GrokBuildAcp.IsSelectablePermissionKind("allow"));
    }

    [Fact]
    public void Tool_name_is_the_xai_tool_string()
    {
        var named = Parse("""{"toolCallId":"call-read","_meta":{"x.ai/tool":"read_file"}}""");
        Assert.Equal("read_file", GrokBuildAcp.ToolName(named));
        var objectName = Parse("""{"_meta":{"x.ai/tool":{"name":"grep","kind":"search"}}}""");
        Assert.Equal("grep", GrokBuildAcp.ToolName(objectName));
        Assert.Null(GrokBuildAcp.ToolName(Parse("""{"title":"Read"}""")));
    }

    [Fact]
    public void Saved_chat_omits_a_null_grok_session_id_and_keeps_one_when_set()
    {
        var bare = JsonSerializer.Serialize(ChatArchive.New());
        Assert.DoesNotContain("GrokBuildSessionId", bare);
        var loaded = JsonSerializer.Deserialize<SavedChat>(bare);
        Assert.Null(loaded!.GrokBuildSessionId);

        var json = JsonSerializer.Serialize(ChatArchive.New() with { GrokBuildSessionId = "sess-9" });
        Assert.Contains("\"GrokBuildSessionId\":\"sess-9\"", json);
        Assert.Equal("sess-9", JsonSerializer.Deserialize<SavedChat>(json)!.GrokBuildSessionId);
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
