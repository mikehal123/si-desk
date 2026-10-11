using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using Sovereign.Desktop.Client;
using WinRT.Interop;

namespace Sovereign.Desktop;

public sealed partial class MainWindow
{
    private async Task RunHelperPanelsReopenVerificationAsync(string output)
    {
        var checks = new List<string>();
        void Check(bool value, string message) { if (!value) throw new IOException(message); checks.Add(message); }
        try
        {
            ValidateCodexQaProfile(_vm.ProfileDirectory);
            if (Environment.GetEnvironmentVariable("SOVEREIGN_QA_PROJECT_LIVE") == "1") throw new IOException("Helper row reopen QA forbids live providers.");
            await Task.Delay(400);
            var chat = _conversations.Single(c => c.Title == "QA helper row reopen");
            SelectChat(chat);
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 850));
            await Task.Delay(400);
            Check(chat.SubagentPanelVisibleCount == 2 && chat.SubagentPanelTabCount == 3, "fresh process restores two helper panels and three tabs");
            Check(new[] { "qa-panel-reopen-a", "qa-panel-reopen-b", "qa-panel-reopen-c" }.All(chat.SubagentPanelReadOnly), "fresh process restores all helper panels as read-only");
            Check(chat._cursorChat is null && !chat.IsWorking, "reopening helper panels does not reconnect or send work");
            chat.VerifyRestoredHelperPanelData(Check);
            await CapturePng(Path.Combine(output, "helper-row-reopened.png"));
            File.WriteAllText(Path.Combine(output, "receipt.json"), JsonSerializer.Serialize(new { passed = true, checks, provider_calls = 0, main_accessed = false }));
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(output, "receipt.json"), JsonSerializer.Serialize(new { passed = false, error = ex.ToString() })); }
        finally { Close(); }
    }

    private async Task RunHelperCardFoldVerificationAsync(string output)
    {
        var checks = new List<string>();
        void Check(bool value, string message) { if (!value) throw new IOException(message); checks.Add(message); }
        var trace = Path.Combine(output, "trace.txt");
        void Mark(string step) { try { File.AppendAllText(trace, step + Environment.NewLine); } catch { } }
        Mark("enter");
        try
        {
            ValidateCodexQaProfile(_vm.ProfileDirectory);
            Mark("profile");
            if (Environment.GetEnvironmentVariable("SOVEREIGN_QA_PROJECT_LIVE") == "1") throw new IOException("Helper card fold QA forbids live providers.");
            await Task.Delay(400);
            OpenChat();
            Mark("chat");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 900));
            await Task.Delay(300);
            Mark("verify");
            await _currentChat.VerifyHelperCardFoldAsync(Check, SendFoldKeyAsync, Mark);
            Mark("done");
            File.WriteAllText(Path.Combine(output, "receipt.json"), JsonSerializer.Serialize(new { passed = true, checks, provider_calls = 0 }));
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(output, "receipt.json"), JsonSerializer.Serialize(new { passed = false, error = ex.ToString() })); }
        finally { Close(); }
    }

    private async Task SendFoldKeyAsync(Microsoft.UI.Xaml.Controls.Control focus, ushort virtualKey)
    {
        var window = WindowNative.GetWindowHandle(this);
        Activate();
        for (var attempt = 0; attempt < 8 && GetFoldForegroundWindow() != window; attempt++)
        {
            var ownThread = GetFoldCurrentThreadId();
            var foregroundThread = GetFoldWindowThreadProcessId(GetFoldForegroundWindow(), out _);
            var attached = false;
            try
            {
                if (foregroundThread != 0 && foregroundThread != ownThread)
                    attached = AttachFoldThreadInput(ownThread, foregroundThread, true);
                ShowFoldWindow(window, 9);
                BringFoldWindowToTop(window);
                SetFoldActiveWindow(window);
                SetFoldForegroundWindow(window);
            }
            finally
            {
                if (attached) AttachFoldThreadInput(ownThread, foregroundThread, false);
            }
            await Task.Delay(80);
        }
        if (GetFoldForegroundWindow() != window) throw new IOException("Helper card fold QA does not own keyboard focus.");
        if (!focus.Focus(Microsoft.UI.Xaml.FocusState.Programmatic)) throw new IOException("Could not focus the helper card header.");
        await Task.Delay(60);
        var input = new[]
        {
            new FoldInput { Type = 1, Keyboard = new FoldKeyboard { VirtualKey = virtualKey } },
            new FoldInput { Type = 1, Keyboard = new FoldKeyboard { VirtualKey = virtualKey, Flags = 2 } }
        };
        if (SendFoldInput((uint)input.Length, input, Marshal.SizeOf<FoldInput>()) != input.Length)
            throw new IOException("Could not send a key to the helper card.");
        await Task.Delay(120);
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct FoldInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public FoldKeyboard Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FoldKeyboard
    {
        public ushort VirtualKey, Scan;
        public uint Flags, Time;
        public UIntPtr ExtraInfo;
    }
    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")] private static extern bool SetFoldForegroundWindow(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")] private static extern IntPtr GetFoldForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")] private static extern uint GetFoldWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")] private static extern uint GetFoldCurrentThreadId();
    [DllImport("user32.dll", EntryPoint = "AttachThreadInput")] private static extern bool AttachFoldThreadInput(uint ownThread, uint foregroundThread, bool attach);
    [DllImport("user32.dll", EntryPoint = "ShowWindow")] private static extern bool ShowFoldWindow(IntPtr window, int command);
    [DllImport("user32.dll", EntryPoint = "BringWindowToTop")] private static extern bool BringFoldWindowToTop(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "SetActiveWindow")] private static extern IntPtr SetFoldActiveWindow(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "SendInput")] private static extern uint SendFoldInput(uint count, FoldInput[] input, int size);
}

internal sealed partial class ChatConversation
{
    internal void VerifyRestoredHelperPanelData(Action<bool, string> check)
    {
        check(_subagentRows["qa-panel-reopen-a"].Output == "Saved builder result" && _subagentRows["qa-panel-reopen-b"].Output == "Saved reviewer result",
            "fresh process retains both completed helper results");
        check(_subagentRows["qa-panel-reopen-c"].State == "disconnected" && SubagentPanelTranscript("qa-panel-reopen-c").Contains("Saved partial finding"),
            "fresh process labels unfinished helper as interrupted and keeps partial output");
        VerifyHelperRowBounds(check);
    }

    internal async Task VerifyHelperCardFoldAsync(Action<bool, string> check, Func<Control, ushort, Task> sendKey, Action<string> mark)
    {
        try
        {
        mark("agents");
        ShowAgents(true);
        SetChangesOpen(true);
        mark("agents-open");
        var started = DateTimeOffset.UtcNow.AddMinutes(-3);
        var running = new CursorSubagent("fold-running", "Live helper", "Watch the build", "running", Reasoning: "Still looking", StartedAt: started);
        var finished = new CursorSubagent("fold-done", "Windows CUDA build recipe", "Write the CUDA steps", "completed", "Packed the recipe",
            Reasoning: "Checked the toolkit", StartedAt: started, CompletedAt: DateTimeOffset.UtcNow);
        var nested = new CursorSubagent("fold-done/leaf", "Nested packager", "Pack the installer", "completed", "Packed",
            StartedAt: started, CompletedAt: DateTimeOffset.UtcNow);
        mark("observe");
        ObserveSubagent(new("subagent", Subagent: running));
        ObserveSubagent(new("subagent", Subagent: finished));
        ObserveSubagent(new("subagent", Subagent: nested));
        mark("open");
        OpenSubagentPanel(nested.SessionId);
        OpenSubagentPanel(finished.SessionId);
        mark("opened");
        await Task.Delay(450);
        mark("settled");
        var live = _openSubagentPanels[running.SessionId];
        var card = _openSubagentPanels[finished.SessionId];
        var leaf = _openSubagentPanels[nested.SessionId];
        check(live.BodyExpanded, "running helper starts expanded");
        check(!card.BodyExpanded && card.TitleText == "Windows CUDA build recipe" && card.StatusLine == "Helper · Finished",
            "finished helper starts collapsed with its title and status");
        check(!leaf.BodyExpanded, "nested finished helper starts collapsed on its own");
        check(!card.ShowsBody("Write the CUDA steps"), "collapsed card hides the prompt");
        var parent = _subagentViews[finished.SessionId];
        parent.Root.ApplyTemplate();
        parent.Root.UpdateLayout();
        await Task.Delay(200);
        var collapsedParent = ExpanderChevronMark(parent.Root);
        var collapsedCard = card.ChevronMark();
        check(collapsedCard == collapsedParent && collapsedCard != "missing",
            "collapsed chevron matches the parent row (" + collapsedCard + " / " + collapsedParent + ")");
        parent.Root.IsExpanded = true;
        card.ToggleHeader();
        await Task.Delay(350);
        check(parent.Root.IsExpanded && card.BodyExpanded, "header click expands the nested card and leaves the parent row open");
        check(card.ShowsBody("Write the CUDA steps"), "expanded card shows the prompt");
        check(card.ShowsBody("Show full reasoning") || card.ShowsBody("Activity"), "expanded card shows the activity or reasoning");
        var expandedParent = ExpanderChevronMark(parent.Root);
        var expandedCard = card.ChevronMark();
        check(expandedCard == expandedParent && expandedCard != collapsedCard,
            "chevron rotates with the parent row (" + collapsedCard + " -> " + expandedCard + ", parent " + expandedParent + ")");
        check(live.BodyExpanded && !leaf.BodyExpanded, "other cards, including a nested one, keep their own state");
        var beforeKey = card.BodyExpanded;
        await sendKey(card.HeaderControl(), 0x20);
        check(card.BodyExpanded != beforeKey, "Space toggles the focused header");
        beforeKey = card.BodyExpanded;
        await sendKey(card.HeaderControl(), 0x0D);
        check(card.BodyExpanded != beforeKey, "Enter toggles the focused header");
        check(live.BodyExpanded && parent.Root.IsExpanded && !leaf.BodyExpanded, "keyboard toggle stays on this card");
        check(!card.ApplyKey("Escape") && !card.ApplyKey("Tab"), "other keys do not toggle");
        var remembered = card.BodyExpanded;
        ObserveSubagent(new("subagent", Subagent: finished with { Output = finished.Output + " again" }));
        check(_openSubagentPanels[finished.SessionId].BodyExpanded == remembered, "a later update keeps the card's open state for this chat");
        OpenSubagentPanel(nested.SessionId);
        await Task.Delay(250);
        leaf = _openSubagentPanels[nested.SessionId];
        leaf.ToggleHeader();
        await Task.Delay(200);
        check(leaf.BodyExpanded && _openSubagentPanels[finished.SessionId].BodyExpanded == remembered,
            "a nested helper card toggles without changing its parent card");
        OpenSubagentPanel(finished.SessionId);
        OpenSubagentPanel(nested.SessionId);
        check(string.Join(" > ", HelperBreadcrumbLabels) == "Root > Windows CUDA build recipe > Nested packager",
            "nested helper breadcrumb is the main chat, the parent helper, then the selected helper");
        check(HelperBreadcrumbIds is [null, "fold-done", "fold-done/leaf"], "breadcrumb ids keep root, parent, and the selected helper");
        ActivateHelperBreadcrumb(1);
        check(string.Join(" > ", HelperBreadcrumbLabels) == "Root > Windows CUDA build recipe", "the parent crumb opens that helper");
        ActivateHelperBreadcrumb(0);
        check(HelperBreadcrumbLabels.Length == 0 && _openSubagentPanels.Count == 0, "the root crumb returns to the main chat");
        var openBefore = _openSubagentPanels.Count;
        leaf.InvokeClose();
        check(!_openSubagentPanels.ContainsKey(nested.SessionId) && _openSubagentPanels.Count == openBefore - 1, "close still closes one card");
        mark("complete");
        }
        catch (Exception ex)
        {
            mark(ex.ToString());
            throw;
        }
    }
}
