using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sovereign.Desktop.Client;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class ProjectAgentReliabilityTests
{
    private sealed class LongRunHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var n = ++Calls;
            var message = new JsonObject { ["role"] = "assistant" };
            if (n <= 40)
                message["tool_calls"] = new JsonArray(new JsonObject { ["id"] = "read-" + n, ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = "read_file", ["arguments"] = "{\"path\":\"proof.md\"}" } });
            else message["content"] = "Finished after 40 tool rounds.";
            var reply = new JsonObject { ["choices"] = new JsonArray(new JsonObject {
                ["finish_reason"] = n <= 40 ? "tool_calls" : "stop", ["message"] = message }) };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply.ToJsonString()) });
        }
    }

    [Theory]
    [InlineData("glm")]
    [InlineData("moonshot")]
    public async Task Project_run_can_finish_after_old_32_round_limit(string provider)
    {
        var root = Path.Combine(Path.GetTempPath(), "agent-long-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "proof.md"), "fixture");
            var handler = new LongRunHandler();
            using var client = new CodingPlanChatClient(provider, CodingPlanChatClient.Endpoints(provider)[0], "fixture-key", handler);
            var answer = "";
            await client.SendProjectAsync("fixture-model", [], root, new() { Permissions = CursorPermissionPolicy.AlwaysAllow }, s => answer += s, default);
            Assert.Equal(41, handler.Calls);
            Assert.Equal("Finished after 40 tool rounds.", answer);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Failure_reason_roundtrips_without_becoming_assistant_text()
    {
        var turn = new SavedChatTurn("implement", "model", "partial", "failed", [],
            FailureReason: ProjectAgentException.Describe(new ProjectAgentException("Project agent reached its round budget.")));
        var copy = JsonSerializer.Deserialize<SavedChatTurn>(JsonSerializer.Serialize(turn))!;
        Assert.Equal(turn.FailureReason, copy.FailureReason);
        Assert.Equal("partial", copy.Reply);
        Assert.Null(JsonSerializer.Deserialize<SavedChatTurn>("{\"Prompt\":\"old\",\"Model\":\"model\",\"Reply\":\"old\",\"Outcome\":\"failed\",\"Activity\":[]}")!.FailureReason);
    }

    [Fact]
    public void Diagnostics_do_not_save_arbitrary_exception_or_response_bodies()
    {
        Assert.DoesNotContain("secret", ProjectAgentException.Describe(new Exception("secret")));
        Assert.DoesNotContain("secret", ProjectAgentException.Describe(new JsonException("secret")));
        var error = ProjectAgentException.Describe(new IOException("Provider returned HTTP 429. secret"));
        Assert.Contains("429", error);
        Assert.DoesNotContain("secret", error);
    }
}
