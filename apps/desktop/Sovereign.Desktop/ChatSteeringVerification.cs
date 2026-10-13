using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Sovereign.Desktop.Client;

namespace Sovereign.Desktop;

public sealed partial class MainWindow
{
    // Explicit, isolated acceptance path. Only the ACP peer is scripted; the client, queue,
    // transcript, persistence and composer handlers are the production implementations.
    public async Task RunChatSteeringVerificationAsync(string output)
    {
        ValidateCodexQaProfile(_vm.ProfileDirectory);
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        var scripts = new List<ChatSteeringScript>();
        var screenshots = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new IOException("Chat steering verification failed: " + name);
            checks.Add(name);
        }
        async Task Capture(string name)
        {
            await Task.Delay(200);
            var path = Path.Combine(output, name + ".png");
            await CapturePng(path);
            screenshots.Add(path);
        }
        ChatConversation NewScriptedChat(string name, out ChatSteeringScript script)
        {
            NewChat(DeskChatConnections.Cursor);
            script = new ChatSteeringScript("synthetic-" + name);
            scripts.Add(script);
            return _currentChat;
        }
        try
        {
            Title = "SI Desk · synthetic Cursor steering QA · no provider calls";
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 950));
            await Task.Delay(400);
            OpenChat();
            var continuation = NewScriptedChat("continue", out var continueScript);
            await continuation.AttachSteeringScriptAsync(continueScript);
            await continuation.VerifySteeringContinuationAsync(continueScript, Check, Capture);

            var early = NewScriptedChat("early-ordered", out var earlyScript);
            await early.AttachSteeringScriptAsync(earlyScript);
            await early.VerifyEarlyOrderedSteeringAsync(earlyScript, Check, Capture);

            var stopped = NewScriptedChat("stop", out var stopScript);
            await stopped.AttachSteeringScriptAsync(stopScript);
            await stopped.VerifySteeringStopAsync(stopScript, Check, Capture);

            var background = NewScriptedChat("background", out var backgroundScript);
            await background.AttachSteeringScriptAsync(backgroundScript);
            const string backgroundOriginal = "Synthetic background original instruction.";
            const string backgroundCorrection = "Synthetic correction belongs only to the background conversation.";
            var backgroundSend = background.BeginSteeringVerificationSend(backgroundOriginal);
            await ChatSteeringScript.WaitAsync(() => backgroundScript.Prompts.Count == 1 && background.IsWorking,
                "background prompt running");
            await background.AcceptSteeringVerificationAsync(backgroundCorrection);
            await ChatSteeringScript.WaitAsync(() => backgroundScript.CancelCount == 1,
                "background redirect requested");
            await Capture("switch-background-queued");

            var foreground = NewScriptedChat("foreground", out var foregroundScript);
            await foreground.AttachSteeringScriptAsync(foregroundScript);
            const string foregroundPrompt = "Synthetic foreground instruction with no correction.";
            var foregroundSend = foreground.BeginSteeringVerificationSend(foregroundPrompt);
            await ChatSteeringScript.WaitAsync(() => foregroundScript.Prompts.Count == 1 && foreground.IsWorking,
                "foreground prompt running");
            Check(background.View.Visibility == Visibility.Collapsed && foreground.View.Visibility == Visibility.Visible,
                "switching conversations changes only the selected view");
            Check(backgroundScript.CancelCount == 1 && foregroundScript.CancelCount == 0,
                "switching does not redirect the other ACP session");
            backgroundScript.ReleaseCancelledPrompt();
            await ChatSteeringScript.WaitAsync(() => backgroundScript.Prompts.Count == 2,
                "hidden conversation continues its own correction");
            Check(background.IsWorking && foreground.IsWorking,
                "hidden and selected conversations keep independent working state");
            await Capture("switch-foreground-running");
            foregroundScript.CompletePrompt("Synthetic foreground response.");
            backgroundScript.CompletePrompt("Synthetic background response after correction.");
            await Task.WhenAll(backgroundSend, foregroundSend).WaitAsync(TimeSpan.FromSeconds(10));
            background.VerifySteeringSwitchResult(backgroundCorrection, "Synthetic background response after correction.", Check);
            // The unredirected foreground prompt retains its scripted first delta as well as its final delta.
            foreground.VerifySteeringSwitchResult(foregroundPrompt,
                ChatSteeringScript.PartialReply + "Synthetic foreground response.", Check);
            Check(foregroundScript.Prompts.Count == 1 &&
                foregroundScript.Prompts.All(prompt => !prompt.Text.Contains(backgroundCorrection, StringComparison.Ordinal)),
                "queued correction never leaks into another conversation");
            Check(backgroundScript.Prompts.All(prompt => prompt.SessionId == backgroundScript.SessionId) &&
                foregroundScript.Prompts.All(prompt => prompt.SessionId == foregroundScript.SessionId),
                "each conversation keeps its original ACP session");
            SelectChat(background);
            await Capture("switch-background-completed");
            File.WriteAllText(Path.Combine(output, "receipt.json"), JsonSerializer.Serialize(new
            {
                passed = true, utc = DateTimeOffset.UtcNow, profile = _vm.ProfileDirectory,
                transport = "scripted ACP TextReader/TextWriter through actual CursorChatClient",
                provider_calls = 0, checks, screenshots,
                sessions = scripts.Select(script => script.Receipt())
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(output, "receipt.json"), JsonSerializer.Serialize(new
            {
                passed = false, utc = DateTimeOffset.UtcNow, profile = _vm.ProfileDirectory,
                provider_calls = 0, checks, screenshots, error = error.ToString(),
                sessions = scripts.Select(script => script.Receipt())
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            foreach (var script in scripts) script.Dispose();
            Close();
        }
    }
}

internal sealed partial class ChatConversation
{
    internal async Task AttachSteeringScriptAsync(ChatSteeringScript script)
    {
        MainWindow.ValidateCodexQaProfile(_profileDirectory);
        if (_saved.Provider != DeskChatConnections.CursorStored || _saved.Turns.Length != 0 || _chatBusy)
            throw new IOException("Steering verification requires a new isolated Cursor conversation.");
        _cursorChat?.Dispose();
        _cursorChat = new CursorChatClient(Path.Combine(_profileDirectory, "synthetic-cursor-workspace"),
            script.Reader, script.Writer, activity: activity =>
            {
                _activeChatTurn?.Observe(activity);
                _partialActivity.Enqueue(activity);
            });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var models = await _cursorChat.ModelsAsync(timeout.Token);
        BindChatModels(models.Select(model => (model.Id, (string?)model.Name)).ToArray(), false);
        _chatModel.SelectedIndex = 0;
        _chatReady = models.Length == 1;
        _reconnectOnSend = false;
        _chatStatus.Text = "Synthetic ACP peer connected. No Cursor runtime, sign-in, network or model call.";
        RefreshAttachmentStatus();
        Save();
    }

    internal Task BeginSteeringVerificationSend(string prompt)
    {
        _chatInput.Text = prompt;
        return SendChatAsync();
    }

    internal async Task AcceptSteeringVerificationAsync(string correction)
    {
        _chatInput.Text = correction;
        // A duplicate handler invocation sees the cleared composer or the production acceptance guard.
        var first = SendChatAsync();
        var repeated = SendChatAsync();
        await Task.WhenAll(first, repeated).WaitAsync(TimeSpan.FromSeconds(5));
    }

    internal async Task VerifySteeringContinuationAsync(ChatSteeringScript script,
        Action<bool, string> check, Func<string, Task> capture)
    {
        const string original = "Synthetic original instruction before steering.";
        const string correction = "Synthetic Enter correction: use the corrected scope and continue.";
        var running = BeginSteeringVerificationSend(original);
        await ChatSteeringScript.WaitAsync(() => script.Prompts.Count == 1 && _chatBusy &&
            _chatTurns.Last().DisplayReply.Contains(ChatSteeringScript.PartialReply, StringComparison.Ordinal),
            "original prompt emits real client delta");
        check(_composerSpinner.IsActive, "actual running Cursor send drives rendered progress");
        CheckSteeringComposerAvailable(check);
        await capture("cursor-original-running");
        _chatInput.Text = correction;
        await PressSteeringVerificationEnterAsync();
        await ChatSteeringScript.WaitAsync(() => script.CancelCount == 1 && _chatInput.Text.Length == 0,
            "ordinary Enter accepts correction and requests redirect");
        await SendChatAsync(); // Duplicate Enter dispatch after acceptance must not append another instruction.
        check(CountSteeringUserText(correction) == 1, "accepted correction is rendered once before cancellation drains");
        check(script.Prompts.Count == 1, "correction waits until the old ACP prompt drains");
        var accepted = _steeringQueue.Snapshot.Single(instruction => instruction.Text == correction);
        check((_saved.PendingInstructions ?? []).Count(instruction => instruction.Id == accepted.Id &&
            instruction.Text == correction && !instruction.MayHaveBeenSent) == 1,
            "queued correction is persisted before provider drain");
        await capture("cursor-enter-correction-queued");
        script.ReleaseCancelledPrompt();
        await ChatSteeringScript.WaitAsync(() => script.Prompts.Count == 2,
            "accepted correction automatically continues");
        check(script.SessionNewCount == 1 && script.Prompts.All(prompt => prompt.SessionId == script.SessionId),
            "redirect and automatic continuation reuse the same ACP session");
        check(script.Prompts[1].Text.EndsWith(correction, StringComparison.Ordinal) &&
            !script.Prompts[1].Text.Contains("SAVED CONVERSATION", StringComparison.Ordinal),
            "continuation sends the accepted correction without replaying saved history");
        check(CountSteeringUserText(correction) == 1, "automatic continuation reuses the accepted user bubble");
        await capture("cursor-correction-running");
        script.CompletePrompt("Synthetic completed reply under corrected scope.");
        await running.WaitAsync(TimeSpan.FromSeconds(10));
        check(_saved.Turns.Length == 2 && _saved.Turns[0].Prompt == original && _saved.Turns[1].Prompt == correction,
            "original instruction and correction are retained in transcript order");
        check(_saved.Turns[0].Reply.Contains(ChatSteeringScript.PartialReply, StringComparison.Ordinal) &&
            _saved.Turns[0].Reply.Contains(ChatSteeringScript.DrainedTail, StringComparison.Ordinal) &&
            _saved.Turns[0].Outcome == "redirected",
            "redirect keeps old partial reply and drained tail without marking them complete");
        check(_saved.Turns[1].Outcome == "completed" &&
            _saved.Turns[1].Reply == "Synthetic completed reply under corrected scope." &&
            _saved.Turns[1].Instructions is { Length: 1 } delivered && delivered[0].Id == accepted.Id,
            "automatic correction completes through the real client delta path");
        check(!_chatBusy && !_composerSpinner.IsActive && _chatReady && _chatInput.Text.Length == 0,
            "completed correction leaves the connected composer ready");
        check(script.Prompts.Count == 2 && script.CancelCount == 1,
            "duplicate Enter dispatch sends one correction and one cancellation");
        check(_steeringQueue.Count == 0 && (_saved.PendingInstructions ?? []).Length == 0,
            "completed instruction leaves no duplicate automatic work");
        var reopened = _archive.Load().Single(chat => chat.Id == Id);
        check(reopened.Turns.Length == 2 && reopened.Turns[0].Reply == _saved.Turns[0].Reply &&
            reopened.Turns[1].Prompt == correction && reopened.Turns[1].Outcome == "completed",
            "persisted transcript reopens with prior reply and corrected result");
        await capture("cursor-correction-completed");
    }

    internal async Task VerifyEarlyOrderedSteeringAsync(ChatSteeringScript script,
        Action<bool, string> check, Func<string, Task> capture)
    {
        const string original = "Synthetic instruction held before its ACP prompt starts.";
        const string firstCorrection = "Synthetic first correction captured while set_model is outstanding.";
        const string secondCorrection = "Synthetic second correction must follow the first in submission order.";
        const string newerDraft = "Synthetic newer draft typed while the accepted corrections continue.";
        script.HoldNextModelSelection();
        var running = BeginSteeringVerificationSend(original);
        await ChatSteeringScript.WaitAsync(() => script.IsModelSelectionHeld && _chatBusy,
            "set_model remains outstanding");
        CheckSteeringComposerAvailable(check);
        _chatInput.Text = "Synthetic multiline draft";
        await PressSteeringVerificationEnterAsync(shift: true);
        await ChatSteeringScript.WaitAsync(() => _chatInput.Text.Contains('\n') || _chatInput.Text.Contains('\r'),
            "Shift Enter adds a newline");
        check(script.Prompts.Count == 0 && script.CancelCount == 0 && _steeringQueue.Count == 1,
            "Shift Enter preserves a multiline draft without queuing or redirecting");
        await AcceptSteeringVerificationAsync(firstCorrection);
        await AcceptSteeringVerificationAsync(secondCorrection);
        var accepted = _steeringQueue.Snapshot.Where(item => item.IsSteering).ToArray();
        check(accepted.Select(item => item.Text).SequenceEqual(new[] { firstCorrection, secondCorrection }),
            "multiple early corrections are captured once in submission order");
        check(script.Prompts.Count == 0 && script.CancelCount == 0,
            "correction before prompt waits through set_model without cancelling another request");
        _chatInput.Text = newerDraft;
        await capture("cursor-early-corrections-queued");
        script.ReleaseModelSelection();
        await ChatSteeringScript.WaitAsync(() => script.Prompts.Count == 1 && script.CancelCount == 1,
            "early correction redirects the exact prompt after set_model drains");
        script.ReleaseCancelledPrompt();
        await ChatSteeringScript.WaitAsync(() => script.Prompts.Count == 2,
            "ordered early corrections continue automatically");
        var submitted = script.Prompts[1].Text;
        check(submitted.IndexOf(firstCorrection, StringComparison.Ordinal) >= 0 &&
            submitted.IndexOf(firstCorrection, StringComparison.Ordinal) < submitted.IndexOf(secondCorrection, StringComparison.Ordinal),
            "one continuation sends the captured corrections in original order");
        check(_chatInput.Text == newerDraft && !submitted.Contains(newerDraft, StringComparison.Ordinal),
            "automatic continuation neither sends nor clears a newer composer draft");
        check(script.CancelCount == 1 && script.SessionNewCount == 1 &&
            script.Prompts.All(prompt => prompt.SessionId == script.SessionId),
            "early correction and ordered batch keep one cancellation and one ACP session");
        script.CompletePrompt("Synthetic ordered corrections completed.");
        await running.WaitAsync(TimeSpan.FromSeconds(10));
        check(_saved.Turns.Length == 2 && _saved.Turns[1].Instructions is { Length: 2 } delivered &&
            delivered.Select(item => item.Id).SequenceEqual(accepted.Select(item => item.Id)),
            "ordered correction batch retains both original instruction identities");
        check(_chatInput.Text == newerDraft && _saved.Draft == newerDraft && _steeringQueue.Count == 0,
            "completion persists the untouched newer draft and clears delivered instructions");
        await capture("cursor-early-ordered-completed");
    }

    private void CheckSteeringComposerAvailable(Action<bool, string> check)
    {
        var hasDraft = !string.IsNullOrWhiteSpace(_chatInput.Text) || _draftFiles.Count > 0;
        check(_chatInput.IsEnabled && _chatSend is { IsEnabled: true } && _chatStop is { IsEnabled: true } &&
            _chatSend.Visibility == (hasDraft ? Visibility.Visible : Visibility.Collapsed) &&
            _chatStop.Visibility == (hasDraft ? Visibility.Collapsed : Visibility.Visible),
            "Cursor busy composer switches one action between Stop and Send when a draft is entered");
    }

    internal async Task VerifySteeringStopAsync(ChatSteeringScript script,
        Action<bool, string> check, Func<string, Task> capture)
    {
        const string original = "Synthetic original instruction for explicit Stop.";
        const string correction = "Synthetic pending correction retained after Stop; do not auto-run.";
        var running = BeginSteeringVerificationSend(original);
        await ChatSteeringScript.WaitAsync(() => script.Prompts.Count == 1 && _chatBusy, "Stop fixture starts");
        await AcceptSteeringVerificationAsync(correction);
        await ChatSteeringScript.WaitAsync(() => script.CancelCount == 1, "Stop fixture queues correction");
        check(CountSteeringUserText(correction) == 1, "concurrent Send dispatch accepts the instruction once");
        StopChatResponse();
        script.ReleaseCancelledPrompt();
        await running.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(150);
        check(script.Prompts.Count == 1, "explicit Stop suppresses queued automatic continuation");
        check(!_chatBusy && !_composerSpinner.IsActive, "explicit Stop clears actual running progress");
        var saved = _archive.Load().Single(chat => chat.Id == Id);
        check((saved.PendingInstructions ?? []).Count(instruction => instruction.Text == correction &&
            !instruction.MayHaveBeenSent) == 1 && !_steeringQueue.HasAutomaticPending,
            "explicit Stop keeps the accepted correction in the local archive");
        await capture("cursor-stop-pending-retained");
    }

    internal void VerifySteeringSwitchResult(string instruction, string reply, Action<bool, string> check)
    {
        check(!_chatBusy && _saved.Turns.Last().Prompt == instruction && _saved.Turns.Last().Reply == reply,
            "conversation " + Id + " receives only its own continuation result");
    }

    private int CountSteeringUserText(string text) => _chatMessages.Children
        .SelectMany(Descendants).OfType<TextBlock>().Count(block => block.Text == "› " + text);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject element)
    {
        yield return element;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(element, index))) yield return child;
    }

    private async Task PressSteeringVerificationEnterAsync(bool shift = false)
    {
        var window = WinRT.Interop.WindowNative.GetWindowHandle(_owner);
        _owner.AppWindow.Show();
        _owner.Activate();
        // A runner started by a background shell can lack foreground permission. Temporarily
        // join its input queue to the current foreground thread, target only our exact HWND,
        // and always detach before awaiting or sending any keyboard input.
        for (var attempt = 0; attempt < 8 && GetForegroundWindow() != window; attempt++)
        {
            var ownThread = GetCurrentThreadId();
            var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            var attached = false;
            try
            {
                if (foregroundThread != 0 && foregroundThread != ownThread)
                    attached = AttachThreadInput(ownThread, foregroundThread, true);
                ShowWindow(window, 9); // SW_RESTORE
                BringWindowToTop(window);
                SetActiveWindow(window);
                SetForegroundWindow(window);
            }
            finally
            {
                if (attached) AttachThreadInput(ownThread, foregroundThread, false);
            }
            await Task.Delay(100);
        }
        if (GetForegroundWindow() != window) throw new IOException("Synthetic QA window does not own keyboard focus.");
        if (!_chatInput.Focus(FocusState.Programmatic)) throw new IOException("Could not focus the synthetic composer.");
        await Task.Delay(50);
        if (GetForegroundWindow() != window) throw new IOException("Synthetic QA window lost keyboard focus before Enter.");
        var input = new List<SteeringInput>();
        if (shift) input.Add(new SteeringInput { Type = 1, Keyboard = new SteeringKeyboard { VirtualKey = 0x10 } });
        input.Add(new SteeringInput { Type = 1, Keyboard = new SteeringKeyboard { VirtualKey = 0x0D } });
        input.Add(new SteeringInput { Type = 1, Keyboard = new SteeringKeyboard { VirtualKey = 0x0D, Flags = 2 } });
        if (shift) input.Add(new SteeringInput { Type = 1, Keyboard = new SteeringKeyboard { VirtualKey = 0x10, Flags = 2 } });
        if (SendInput((uint)input.Count, input.ToArray(), Marshal.SizeOf<SteeringInput>()) != input.Count)
            throw new IOException("Could not send ordinary Enter to the synthetic composer.");
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct SteeringInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public SteeringKeyboard Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SteeringKeyboard
    {
        public ushort VirtualKey, Scan;
        public uint Flags, Time;
        public UIntPtr ExtraInfo;
    }
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint ownThread, uint foregroundThread, bool attach);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr SetActiveWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, SteeringInput[] input, int size);
}

internal sealed class ChatSteeringScript : IDisposable
{
    internal const string Model = "grok-4.7[context=256k,reasoning_effort=high,fast=true]";
    internal const string PartialReply = "Synthetic reply before correction. ";
    internal const string DrainedTail = "Synthetic final chunk drained after cancel. ";
    internal sealed record Prompt(int Id, string SessionId, string Text);
    private readonly Channel<string> _responses = Channel.CreateUnbounded<string>();
    private readonly List<string> _methods = [];
    private Prompt? _active;
    private bool _holdNextModelSelection;
    private int? _heldModelSelection;
    private bool _disposed;
    internal string SessionId { get; }
    internal List<Prompt> Prompts { get; } = [];
    internal int CancelCount { get; private set; }
    internal int SessionNewCount { get; private set; }
    internal bool IsModelSelectionHeld => _heldModelSelection is not null;
    internal TextReader Reader { get; }
    internal TextWriter Writer { get; }

    internal ChatSteeringScript(string sessionId)
    {
        SessionId = sessionId;
        Reader = new ScriptReader(_responses.Reader);
        Writer = new ScriptWriter(Receive);
    }
    private void Receive(string line)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var json = JsonDocument.Parse(line);
        var message = json.RootElement;
        var method = message.GetProperty("method").GetString()!;
        _methods.Add(method);
        var parameters = message.GetProperty("params");
        if (method == "session/cancel")
        {
            if (parameters.GetProperty("sessionId").GetString() != SessionId)
                throw new IOException("Synthetic cancellation targeted another ACP session.");
            CancelCount++;
            return;
        }
        var id = message.GetProperty("id").GetInt32();
        switch (method)
        {
            case "initialize": Reply(id, new { protocolVersion = 1 }); break;
            case "session/new":
                SessionNewCount++;
                Reply(id, new { sessionId = SessionId, models = new
                { availableModels = new[] { new { modelId = Model, name = "Grok 4.7 High Fast" } } } });
                break;
            case "session/set_mode":
                Reply(id, new { }); break;
            case "session/set_model":
                if (_holdNextModelSelection)
                {
                    _holdNextModelSelection = false;
                    _heldModelSelection = id;
                }
                else Reply(id, new { });
                break;
            case "session/prompt":
                if (_active is not null) throw new IOException("A correction started before the old ACP prompt drained.");
                var prompt = new Prompt(id, parameters.GetProperty("sessionId").GetString()!,
                    parameters.GetProperty("prompt")[0].GetProperty("text").GetString()!);
                if (prompt.SessionId != SessionId) throw new IOException("Synthetic prompt targeted another ACP session.");
                _active = prompt;
                Prompts.Add(prompt);
                if (Prompts.Count == 1) Update(PartialReply);
                break;
            default: throw new IOException("Unexpected synthetic ACP method: " + method);
        }
    }
    internal void ReleaseCancelledPrompt()
    {
        if (_active is not { } prompt || CancelCount == 0)
            throw new IOException("Synthetic cancelled prompt is not available to drain.");
        Update(DrainedTail);
        _active = null;
        Reply(prompt.Id, new { stopReason = "cancelled" });
    }
    internal void CompletePrompt(string text)
    {
        var prompt = _active ?? throw new IOException("No synthetic prompt is active.");
        Update(text);
        _active = null;
        Reply(prompt.Id, new { stopReason = "end_turn" });
    }
    internal void HoldNextModelSelection() => _holdNextModelSelection = true;
    internal void ReleaseModelSelection()
    {
        var id = _heldModelSelection ?? throw new IOException("No synthetic model selection is held.");
        _heldModelSelection = null;
        Reply(id, new { });
    }
    private void Reply(int id, object result) => Enqueue(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }));
    private void Update(string text) => Enqueue(JsonSerializer.Serialize(new
    {
        jsonrpc = "2.0", method = "session/update", @params = new
        { sessionId = SessionId, update = new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text } } }
    }));
    private void Enqueue(string line)
    {
        if (!_responses.Writer.TryWrite(line)) throw new IOException("Synthetic transport closed.");
    }
    internal object Receipt() => new { session_id = SessionId, session_new_count = SessionNewCount,
        cancel_count = CancelCount, prompts = Prompts.ToArray(), methods = _methods.ToArray() };
    internal static async Task WaitAsync(Func<bool> ready, string label)
    {
        var watch = Stopwatch.StartNew();
        while (!ready())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException(label);
            await Task.Delay(20);
        }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _responses.Writer.TryComplete();
    }
    private sealed class ScriptReader(ChannelReader<string> responses) : TextReader
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            try { return await responses.ReadAsync(cancellationToken); }
            catch (ChannelClosedException) { return null; }
        }
        public override Task<string?> ReadLineAsync() => ReadLineAsync(CancellationToken.None).AsTask();
    }
    private sealed class ScriptWriter(Action<string> receive) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override Task WriteLineAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            receive(value.ToString());
            return Task.CompletedTask;
        }
        public override Task WriteLineAsync(string? value)
        {
            receive(value ?? "");
            return Task.CompletedTask;
        }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
