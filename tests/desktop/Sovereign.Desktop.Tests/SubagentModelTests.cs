using System.Text.Json;
using Sovereign.Desktop.Client;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class SubagentModelTests
{
    private const string HighFast = "grok-4.7[context=256k,reasoning_effort=high,fast=true]";
    private static string Reply(int id, object result) => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result });
    private static string Update(string session, object update) => JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "session/update", @params = new { sessionId = session, update } });
    private static object Spawn(string id) => new { sessionUpdate = "subagent_spawned", subagentSessionId = id, name = "Review", task = "Review the implementation" };

    private static CursorAcpSession Protocol(string selected, List<CursorActivityEvent> activity, StringWriter writer, params string[] events)
    {
        var messages = new[]
        {
            Reply(1, new { protocolVersion = 1, agentCapabilities = new { sessionCapabilities = new { subagents = new { } } } }),
            Reply(2, new { sessionId = "parent", models = new { currentModelId = "grok-4.7", availableModels = new[]
            {
                new { modelId = selected, name = "Selected model" },
                new { modelId = "grok-4.7", name = "Default Grok 4.7" }
            } } }),
            Reply(3, new { }), Reply(4, new { })
        }.Concat(events).Append(Update("parent", new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "Parent summary" } }))
            .Append(Reply(5, new { stopReason = "end_turn" }));
        return new(new StringReader(string.Join('\n', messages)), writer, new() { EnableSubagents = true }, activity.Add);
    }

    [Theory]
    [InlineData(HighFast, "grok-4.6", "Grok 4.6")]
    [InlineData("grok-4.6", HighFast, "Grok 4.7 High Fast")]
    public async Task Runtime_child_model_reaches_saved_sidebar_formatter_without_parent_or_default_fallback(string selected, string reported, string rendered)
    {
        var observed = new List<CursorActivityEvent>();
        var writer = new StringWriter();
        var session = Protocol(selected, observed, writer,
            Update("parent", Spawn("helper")),
            Update("helper", new { sessionUpdate = "current_model_update", currentModelId = reported }),
            Update("parent", new { sessionUpdate = "subagent_state_update", subagentSessionId = "helper", state = "completed" }));
        await session.ConnectAsync("fixture", default);
        await session.SendAsync(selected, "Review", _ => { }, default);
        var snapshot = observed.Last(e => e.Subagent is not null).Subagent!;
        Assert.Equal(reported, snapshot.ModelId);
        Assert.Null(snapshot.ModelName); // The parent's catalog is not helper metadata.
        Assert.Equal("runtime model update", snapshot.ModelSource);
        Assert.Equal(rendered, SubagentModelPresentation.Display(snapshot));
        Assert.Equal(selected, session.SelectedModelId);
        var sent = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            var selection = Assert.Single(sent, d => d.RootElement.GetProperty("method").GetString() == "session/set_model");
            Assert.Equal(selected, selection.RootElement.GetProperty("params").GetProperty("modelId").GetString());
        }
        finally { foreach (var document in sent) document.Dispose(); }

        var root = Path.Combine(Path.GetTempPath(), "Sovereign-helper-model-" + Guid.NewGuid().ToString("N"));
        try
        {
            var archive = new ChatArchive(root);
            var chat = ChatArchive.New() with { Model = HighFast, Turns = [new("Review", selected, "Summary", "completed", observed.ToArray())] };
            archive.Save(chat);
            var loaded = Assert.Single(archive.Load());
            var restored = loaded.Turns[0].Activity.Last(e => e.Subagent is not null).Subagent!;
            Assert.Equal(JsonSerializer.Serialize(snapshot), JsonSerializer.Serialize(restored));
            Assert.Equal(rendered, SubagentModelPresentation.Display(restored));
            // A later settings/parent selection must not rewrite the recorded helper.
            archive.Save(loaded with { Model = "gpt-6.1-sol" });
            Assert.Equal(rendered, SubagentModelPresentation.Display(Assert.Single(archive.Load()).Turns[0].Activity.Last(e => e.Subagent is not null).Subagent!));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Missing_child_model_stays_unknown_despite_parent_model_and_spawn_request_metadata()
    {
        var observed = new List<CursorActivityEvent>();
        var session = Protocol(HighFast, observed, new StringWriter(),
            Update("parent", new { sessionUpdate = "current_model_update", currentModelId = HighFast }),
            Update("parent", new { sessionUpdate = "subagent_spawned", subagentSessionId = "helper", name = "Review", task = "Review",
                requestedModelId = HighFast, _meta = new { cursor = new { modelId = HighFast } } }),
            Update("parent", new { sessionUpdate = "subagent_state_update", subagentSessionId = "helper", state = "completed" }));
        await session.ConnectAsync("fixture", default);
        await session.SendAsync(HighFast, "Review", _ => { }, default);
        var snapshot = observed.Last(e => e.Subagent is not null).Subagent!;
        Assert.Null(snapshot.ModelId);
        Assert.Equal("Model unknown", SubagentModelPresentation.Display(snapshot));
        Assert.Equal(HighFast, snapshot.RequestedModelId);
        Assert.Equal("Grok 4.7 High Fast", SubagentModelPresentation.RequestedDisplay(snapshot));
        Assert.Equal("Requested model", SubagentModelPresentation.RequestedCaption(snapshot));
        var turn = new SavedChatTurn("Review", HighFast, "Summary", "completed", observed.ToArray());
        var restored = JsonSerializer.Deserialize<SavedChatTurn>(JsonSerializer.Serialize(turn))!;
        Assert.Equal("Model unknown", SubagentModelPresentation.Display(restored.Activity.Last(e => e.Subagent is not null).Subagent!));
        var legacy = JsonSerializer.Deserialize<CursorSubagent>("{\"SessionId\":\"old\",\"Name\":\"Review\",\"Task\":\"Review\",\"State\":\"completed\"}")!;
        Assert.Equal("Model unknown", SubagentModelPresentation.Display(legacy));
    }

    [Fact]
    public void Reroute_keeps_configuration_and_resume_requires_fresh_reported_identity()
    {
        var tracker = new CursorSubagents();
        tracker.Observe("parent", "parent", JsonSerializer.SerializeToElement(Spawn("helper")));
        var first = tracker.SetReportedModel("helper", HighFast, "Grok 4.7", "xai", "runtime configuration")!;
        Assert.Equal("Model unknown", SubagentModelPresentation.Display(first));
        Assert.Equal("Grok 4.7 High Fast", SubagentModelPresentation.RequestedDisplay(first));
        Assert.Equal("Configured model", SubagentModelPresentation.RequestedCaption(first));
        var rerouted = tracker.SetReportedModel("helper", "gpt-6.1-sol", modelSource: "runtime reroute")!;
        Assert.Null(rerouted.ModelName);
        Assert.Null(rerouted.ModelProvider);
        Assert.Equal("GPT 6.1 Sol", SubagentModelPresentation.Display(rerouted));
        Assert.Equal("Grok 4.7 High Fast", SubagentModelPresentation.RequestedDisplay(rerouted));
        Assert.Equal("Provider unknown", SubagentModelPresentation.ProviderLabel(rerouted));
        Assert.Null(tracker.SetReportedModel("helper", " "));
        Assert.Equal(rerouted, tracker.Get("helper"));
        tracker.Observe("parent", "parent", JsonSerializer.SerializeToElement(new { sessionUpdate = "subagent_state_update", subagentSessionId = "helper", state = "completed" }));
        var resumed = tracker.Resume("helper", "Another task");
        Assert.Equal("Model unknown", SubagentModelPresentation.Display(resumed));
        Assert.Equal("Grok 4.7 High Fast", SubagentModelPresentation.RequestedDisplay(resumed));
        Assert.True(SubagentModelPresentation.HasRequestedModel(resumed));
        Assert.Throws<IOException>(() => tracker.SetReportedModel("stranger", HighFast));
    }

    [Fact]
    public void Reported_metadata_is_bounded_without_stopping_a_helper_or_inventing_identity()
    {
        var tracker = new CursorSubagents();
        tracker.Observe("parent", "parent", JsonSerializer.SerializeToElement(Spawn("helper")));
        Assert.Null(tracker.SetReportedModel("helper", new string('a', 513)));
        Assert.Null(tracker.SetReportedModel("helper", "bad\nmodel"));
        Assert.Equal("running", tracker.Get("helper")!.State);
        Assert.Equal("Model unknown", SubagentModelPresentation.Display(tracker.Get("helper")!));
        tracker.SetReportedModel("helper", "default", modelSource: "runtime configuration");
        Assert.Equal("Model unknown", SubagentModelPresentation.Display(tracker.Get("helper")!));
        tracker.SetReportedModel("helper", "AUTO", modelSource: "runtime configuration");
        Assert.Equal("Model unknown", SubagentModelPresentation.Display(tracker.Get("helper")!));
    }

    [Fact]
    public void Legacy_configuration_is_preserved_separately_and_never_confirms_an_active_run_model()
    {
        var legacy = new CursorSubagent("helper", "Review", "Review", "completed", "Result",
            ModelId: HighFast, ModelName: "Grok 4.7", ModelProvider: "xai", ModelSource: "runtime configuration");
        var restored = JsonSerializer.Deserialize<CursorSubagent>(JsonSerializer.Serialize(legacy))!;
        Assert.Equal("Model unknown", SubagentModelPresentation.Display(restored));
        Assert.Equal("Provider unknown", SubagentModelPresentation.ProviderLabel(restored));
        Assert.Equal("Grok 4.7 High Fast", SubagentModelPresentation.RequestedDisplay(restored));
        Assert.Equal("xAI", SubagentModelPresentation.RequestedProviderLabel(restored));
        Assert.Equal("Configured model", SubagentModelPresentation.RequestedCaption(restored));
        Assert.Equal("Result", restored.Output);
    }

    [Fact]
    public void Terminal_failure_final_result_and_both_model_identities_survive_late_metadata_and_reopen_without_clock_changes()
    {
        var tracker = new CursorSubagents();
        tracker.Observe("parent", "parent", JsonSerializer.SerializeToElement(new { sessionUpdate = "subagent_spawned",
            subagentSessionId = "helper", name = "Review", task = "Review", requestedModelId = HighFast, requestedModelProvider = "xai" }));
        tracker.Observe("parent", "helper", JsonSerializer.SerializeToElement(new { sessionUpdate = "current_model_update",
            currentModelId = "grok-4.6", modelProvider = "xai" }));
        tracker.Observe("parent", "helper", JsonSerializer.SerializeToElement(new { sessionUpdate = "agent_message_chunk",
            content = new { type = "text", text = "Partial result" } }));
        var failed = tracker.Observe("parent", "parent", JsonSerializer.SerializeToElement(new { sessionUpdate = "subagent_state_update",
            subagentSessionId = "helper", state = "failed", error = new { message = "The helper exceeded its runtime allowance." } }))!;
        Assert.Equal("The helper exceeded its runtime allowance.", failed.Failure);
        var ended = failed.CompletedAt;
        var lastWork = failed.UpdatedAt;
        Assert.NotNull(ended);
        tracker.SetRequestedModel("helper", HighFast, modelProvider: "xai", modelSource: "runtime configuration");
        var final = tracker.SetFinalOutput("helper", "Partial result with the final finding.")!;
        Assert.Equal("failed", final.State);
        Assert.Equal(ended, final.CompletedAt);
        Assert.Equal(lastWork, final.UpdatedAt);
        Assert.Equal("Grok 4.6", SubagentModelPresentation.Display(final));
        Assert.Equal("Grok 4.7 High Fast", SubagentModelPresentation.RequestedDisplay(final));
        Assert.Equal(failed.Failure, final.Failure);
        tracker.SetFailure("helper", "The helper exceeded its runtime allowance. Actual RPC code: limit_exceeded.");
        var snapshot = tracker.Get("helper")!;
        var turn = new SavedChatTurn("Review", "parent-model", "Parent result", "completed", [new("subagent", Subagent: snapshot)]);
        var restored = JsonSerializer.Deserialize<SavedChatTurn>(JsonSerializer.Serialize(turn))!.Activity[0].Subagent!;
        Assert.Equal(JsonSerializer.Serialize(snapshot), JsonSerializer.Serialize(restored));
        Assert.Equal(ended, restored.CompletedAt);
        Assert.Equal(lastWork, restored.UpdatedAt);
        Assert.Equal("Partial result with the final finding.", restored.Output);
        Assert.Contains("limit_exceeded", restored.Failure);
        Assert.Equal("Grok 4.6", SubagentModelPresentation.Display(restored));
        Assert.Equal("Grok 4.7 High Fast", SubagentModelPresentation.RequestedDisplay(restored));
        var resumed = tracker.Resume("helper", "New run");
        Assert.Equal("Model unknown", SubagentModelPresentation.Display(resumed));
        Assert.Equal("Grok 4.7 High Fast", SubagentModelPresentation.RequestedDisplay(resumed));
        Assert.Equal("Grok 4.7 High Fast", SubagentModelPresentation.RequestedDisplay(tracker.Get("helper")!));
        Assert.Null(tracker.Get("helper")!.Failure);
        Assert.Throws<IOException>(() => tracker.SetFinalOutput("foreign", "Fake result"));
        Assert.Throws<IOException>(() => tracker.SetFailure("foreign", "Fake failure"));
    }

    [Fact]
    public async Task Spawn_meta_model_is_the_helper_model_and_known_provider()
    {
        var observed = new List<CursorActivityEvent>();
        var session = Protocol(HighFast, observed, new StringWriter(),
            Update("parent", new { sessionUpdate = "subagent_spawned", subagentSessionId = "helper", name = "Review", task = "Review",
                _meta = new { cursor = new { toolCallId = "call-1", agentId = "helper-agent", model = HighFast } } }),
            Update("parent", new { sessionUpdate = "subagent_state_update", subagentSessionId = "helper", state = "completed",
                _meta = new { cursor = new { toolCallId = "call-1", model = HighFast } } }));
        await session.ConnectAsync("fixture", default);
        await session.SendAsync(HighFast, "Review", _ => { }, default);
        var snapshot = observed.Last(e => e.Subagent is not null).Subagent!;
        Assert.Equal(HighFast, snapshot.ModelId);
        Assert.Equal("xai", snapshot.ModelProvider);
        Assert.Equal("runtime assignment", snapshot.ModelSource);
        Assert.Equal("Grok 4.7 High Fast", SubagentModelPresentation.Display(snapshot));
        Assert.Equal("xAI", SubagentModelPresentation.ProviderLabel(snapshot));
        Assert.False(SubagentModelPresentation.HasRequestedModel(snapshot));
    }

    [Fact]
    public void Late_runtime_model_fills_the_helper_and_an_unknown_family_stays_unnamed()
    {
        var tracker = new CursorSubagents();
        var spawned = tracker.Observe("parent", "parent", JsonSerializer.SerializeToElement(new {
            sessionUpdate = "subagent_spawned", subagentSessionId = "helper", name = "Review", task = "Review" }))!;
        Assert.Equal("Model unknown", SubagentModelPresentation.Display(spawned));
        Assert.Equal("Provider unknown", SubagentModelPresentation.ProviderLabel(spawned));
        var done = tracker.Observe("parent", "parent", JsonSerializer.SerializeToElement(new {
            sessionUpdate = "subagent_state_update", subagentSessionId = "helper", state = "completed",
            _meta = new { cursor = new { model = "gpt-6.1-sol" } } }))!;
        Assert.Equal("completed", done.State);
        Assert.Equal("GPT 6.1 Sol", SubagentModelPresentation.Display(done));
        Assert.Equal("OpenAI", SubagentModelPresentation.ProviderLabel(done));
        var other = tracker.Observe("parent", "parent", JsonSerializer.SerializeToElement(new {
            sessionUpdate = "subagent_spawned", subagentSessionId = "plain", name = "Review", task = "Review",
            _meta = new { cursor = new { model = "composer-2" } } }))!;
        Assert.Equal("composer-2", other.ModelId);
        Assert.Null(other.ModelProvider);
        Assert.Equal("Provider unknown", SubagentModelPresentation.ProviderLabel(other));
    }
}
