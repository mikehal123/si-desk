using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sovereign.Desktop.Client;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Sovereign.Desktop;

public sealed partial class MainWindow
{
    // Opt-in QA path: real provider, normal Connect/Send handlers and rendered UI.
    // Exported prompts contain no expected answers. Failed first attempts remain in the receipt.
    public async Task RunOllamaChatVerificationAsync(string outputDirectory, string model, string promptsPath, bool thinking = false)
    {
        ValidateCodexQaProfile(_vm.ProfileDirectory);
        Directory.CreateDirectory(outputDirectory);
        var receiptPath = Path.Combine(outputDirectory, "ollama-qualification.json");
        if (File.Exists(receiptPath)) throw new IOException("Refusing to overwrite a qualification run.");
        var cases = new List<object>();
        var runtime = new List<object>();
        var settings = new OllamaChatOptions(8192, thinking ? 2048 : 512, 0, thinking);
        var stage = "starting";
        string? digest = null;
        string? failure = null;
        using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:11434/"), Timeout = TimeSpan.FromSeconds(10) };
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        void Record() => File.WriteAllText(receiptPath, JsonSerializer.Serialize(new
        {
            schema_version = 1, capture_kind = "live_capture", provider = "ollama", model, model_digest = digest,
            recorded_at_utc = DateTimeOffset.UtcNow, profile = _vm.ProfileDirectory, stage, failure,
            configured_context_tokens = settings.ContextTokens,
            generation_settings = new { temperature = settings.Temperature, num_predict = settings.MaxOutputTokens, thinking = settings.Think, thinking_policy = "Set only when the model advertises thinking; otherwise omitted" },
            candidate_package_identity = new { executable = Environment.ProcessPath, client_sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(OllamaChatClient).Assembly.Location))) },
            prompts_file_sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(promptsPath))), cases, runtime
        }, jsonOptions));
        async Task Prepare()
        {
            ResetChat();
            await ConnectChatAsync();
            if (!_chatReady || !HasChatModel(model)) throw new IOException("Requested local model is not available: " + model);
            SelectChatModel(model);
        }
        async Task SendBounded(string prompt)
        {
            _chatInput.Text = prompt;
            var pending = SendChatAsync();
            _chatCancellation?.CancelAfter(TimeSpan.FromSeconds(120));
            await pending;
            await Task.Delay(100);
        }
        string VisibleReply() => _currentChat.LastReply;
        try
        {
            Title = "xAI Desk · QA local model verification";
            OpenChat();
            _chatProvider.SelectedIndex = 1;
            using var tags = JsonDocument.Parse(await http.GetStringAsync("api/tags"));
            digest = tags.RootElement.GetProperty("models").EnumerateArray().First(m => m.GetProperty("name").GetString() == model).GetProperty("digest").GetString();
            using var prompts = JsonDocument.Parse(await File.ReadAllTextAsync(promptsPath));
            _ollamaQaOptions = settings;
            Record();
            foreach (var fixture in prompts.RootElement.GetProperty("cases").EnumerateArray())
            {
                var id = fixture.GetProperty("case_id").GetString()!;
                stage = "running:" + id; Record();
                await Prepare();
                var messages = fixture.GetProperty("messages").EnumerateArray().Select(message => message.Clone()).ToArray();
                if (messages.Length == 0 || messages[^1].GetProperty("role").GetString() != "user") throw new IOException("Expected a final user message.");
                foreach (var message in messages[..^1]) _ollamaHistory.Add(new(message.GetProperty("role").GetString()!, message.GetProperty("content").GetString()!));
                var started = DateTimeOffset.UtcNow;
                var timer = Stopwatch.StartNew();
                await SendBounded(messages[^1].GetProperty("content").GetString()!);
                var complete = _chatStatus.Text == "Response complete." && _lastOllamaReceipt is not null;
                using var residency = JsonDocument.Parse(await http.GetStringAsync("api/ps"));
                cases.Add(new
                {
                    case_id = id, prompt_sha256 = fixture.GetProperty("prompt_sha256").GetString(), dispatched_messages = messages,
                    provider = "ollama", model = _lastOllamaReceipt?.Model ?? model,
                    started_at_utc = started, ended_at_utc = DateTimeOffset.UtcNow,
                    status = complete ? "completed" : "failed", finish_reason = _lastOllamaReceipt?.FinishReason,
                    final_text = VisibleReply(), error = complete ? null : _chatStatus.Text, refusal = (string?)null, tool_calls = Array.Empty<object>(),
                    first_content_ms = _lastOllamaReceipt?.FirstContentMs, elapsed_ms = timer.Elapsed.TotalMilliseconds,
                    provider_receipt = _lastOllamaReceipt, residency = residency.RootElement.Clone(), ui_status = _chatStatus.Text
                });
                Record();
                if (id == "saved-finding" && complete) await CapturePng(Path.Combine(outputDirectory, "saved-finding-reply.png"));
            }

            stage = "output-limit"; Record(); await Prepare();
            _ollamaQaOptions = settings with { MaxOutputTokens = 1 };
            await SendBounded("Write a detailed three-paragraph explanation of why source dates matter. Do not use tools.");
            runtime.Add(new { check = "output-limit", passed = _chatStatus.Text.StartsWith("Response failed:") && _chatStatus.Text.Contains("length"), status = _chatStatus.Text, reply = VisibleReply() });

            stage = "cancel"; Record(); await Prepare();
            _ollamaQaOptions = settings with { MaxOutputTokens = 2048 };
            _chatInput.Text = "Write 2000 numbered sentences about software testing. Do not stop early or summarize.";
            var request = SendChatAsync();
            var first = Stopwatch.StartNew();
            // Thinking may use the entire output budget before any final text appears.
            // Exercise Stop while the request is alive, even when no answer text is visible yet.
            while (!request.IsCompleted && VisibleReply().Length < 20 && first.Elapsed < TimeSpan.FromSeconds(1)) await Task.Delay(50);
            var wasInFlight = !request.IsCompleted;
            var cancellation = Stopwatch.StartNew();
            _chatCancellation?.Cancel();
            await request;
            runtime.Add(new { check = "cancellation", passed = wasInFlight && _chatStatus.Text.StartsWith("Response stopped"), cancelled_in_flight = wasInFlight, elapsed_after_cancel_ms = cancellation.Elapsed.TotalMilliseconds, status = _chatStatus.Text });
            await Task.Delay(2000);

            // Recovery also verifies ordinary application defaults, without QA generation overrides.
            stage = "recovery-default-settings"; Record(); await Prepare();
            _ollamaQaOptions = null;
            await SendBounded("Reply with exactly: xAI Desk local Ollama connection verified. Do not use tools.");
            var recovery = _chatStatus.Text == "Response complete." && VisibleReply().Trim() == "xAI Desk local Ollama connection verified.";
            runtime.Add(new { check = "recovery-default-settings", passed = recovery, status = _chatStatus.Text, reply = VisibleReply(), provider_receipt = _lastOllamaReceipt });
            if (recovery) await CapturePng(Path.Combine(outputDirectory, "ollama-chat-complete.png"));
            stage = "finished"; Record();
        }
        catch (Exception ex) { failure = ex.Message; stage = "incomplete"; Record(); }
        finally { _ollamaQaOptions = null; SetChatBusy(false); }
    }
}
