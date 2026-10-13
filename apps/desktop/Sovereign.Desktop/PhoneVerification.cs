using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Sovereign.Desktop.Client;
using Sovereign.Desktop.Phone;

namespace Sovereign.Desktop;

public sealed partial class MainWindow
{
    // Runs only from the existing explicit, disposable chat-layout QA entry point.
    private async Task VerifyPhoneDesktopAsync(List<string> checks, string output)
    {
        ValidateCodexQaProfile(_vm.ProfileDirectory);
        if (Environment.GetEnvironmentVariable("SOVEREIGN_QA_PROJECT_LIVE") == "1")
            throw new IOException("Phone desktop QA does not permit a paid provider.");
        void Check(bool value, string label)
        {
            if (!value) throw new IOException("Phone desktop QA: " + label);
            checks.Add("Phone: " + label);
        }
        Navigate("settings");
        await Task.Delay(200);
        Check(!PhoneStore.Settings.Enabled && _phoneHost is null && !_phoneEnabled.IsOn, "fresh profile starts off without a listener");
        using var script = new ChatSteeringScript("synthetic-phone");
        NewChat(DeskChatConnections.Cursor);
        var chat = _currentChat;
        await chat.AttachSteeringScriptAsync(script);
        Navigate("settings");
        try
        {
            _phoneEnabled.IsOn = true; // Same Toggled handler as the Settings control.
            await ChatSteeringScript.WaitAsync(() => _phoneHost?.Running == true && _phoneCode.Text.StartsWith("Pairing code "), "phone settings listener");
            Check(_phoneHost!.Origin == "http://127.0.0.1:8789", "Settings starts only the selected loopback address");
            using var http = new HttpClient { BaseAddress = new Uri(_phoneHost.Origin ?? throw new IOException("Missing phone origin")), Timeout = TimeSpan.FromSeconds(10) };
            Check((await http.GetAsync("/api/chats")).StatusCode == HttpStatusCode.Unauthorized, "anonymous chat reads are refused");
            var code = _phoneCode.Text["Pairing code ".Length..];
            using var paired = await http.PostAsJsonAsync("/api/pair", new { code, name = "Disposable phone QA" });
            paired.EnsureSuccessStatusCode();
            using var pair = JsonDocument.Parse(await paired.Content.ReadAsStringAsync());
            var token = pair.RootElement.GetProperty("token").GetString()!;
            var device = pair.RootElement.GetProperty("deviceId").GetString()!;
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            Check(PhoneStore.Devices.Count == 1, "pairing records one device through the actual Settings store");
            Check(!File.ReadAllText(Path.Combine(_vm.ProfileDirectory, "phone", "devices.json")).Contains(token), "device file stores a token hash, not the credential");
            using var list = JsonDocument.Parse(await http.GetStringAsync("/api/chats"));
            Check(list.RootElement.GetProperty("chats").EnumerateArray().Any(item => item.GetProperty("id").GetString() == chat.Id), "HTTP lists the actual desktop conversation");
            const string prompt = "Synthetic phone instruction; no model is called.";
            using var sent = await http.PostAsJsonAsync("/api/chats/" + chat.Id + "/messages", new { text = prompt });
            sent.EnsureSuccessStatusCode();
            await ChatSteeringScript.WaitAsync(() => script.Prompts.Count == 1, "phone prompt reaches the existing Cursor client");
            Check(script.Prompts[0].Text.Contains(prompt) && chat.IsWorking, "phone send uses the existing chat and provider session");
            script.CompletePrompt("Synthetic phone response.");
            await ChatSteeringScript.WaitAsync(() => !chat.IsWorking, "phone response finishes");
            using var detail = JsonDocument.Parse(await http.GetStringAsync("/api/chats/" + chat.Id));
            Check(detail.RootElement.GetProperty("turns").EnumerateArray().Any(turn => turn.GetProperty("reply").GetString()!.Contains("Synthetic phone response.")), "HTTP returns the saved desktop reply");

            using var running = await http.PostAsJsonAsync("/api/chats/" + chat.Id + "/messages", new { text = "Synthetic stop check." });
            running.EnsureSuccessStatusCode();
            await ChatSteeringScript.WaitAsync(() => script.Prompts.Count == 2, "phone stop-check prompt");
            using var stopped = await http.PostAsync("/api/chats/" + chat.Id + "/stop", null);
            stopped.EnsureSuccessStatusCode();
            using var stopResult = JsonDocument.Parse(await stopped.Content.ReadAsStringAsync());
            Check(stopResult.RootElement.GetProperty("stopped").GetBoolean(), "phone Stop is accepted by the actual desktop handler");
            await ChatSteeringScript.WaitAsync(() => !chat.IsWorking, "phone stop completes through hard cancellation");
            using var stoppedDetail = JsonDocument.Parse(await http.GetStringAsync("/api/chats/" + chat.Id));
            Check(stoppedDetail.RootElement.GetProperty("turns").EnumerateArray().Last().GetProperty("outcome").GetString() == "stopped"
                && script.Prompts.Count == 2 && script.SessionNewCount == 1, "phone Stop stores stopped without a resend or new session");

            var connectionEntered = new TaskCompletionSource<bool>();
            chat.PhoneConnectionForVerification(async ct => { connectionEntered.TrySetResult(true); await Task.Delay(Timeout.Infinite, ct); });
            using var reconnectSend = await http.PostAsJsonAsync("/api/chats/" + chat.Id + "/messages", new { text = "Do not dispatch after reconnect Stop." });
            reconnectSend.EnsureSuccessStatusCode();
            await connectionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var reconnectStop = await http.PostAsync("/api/chats/" + chat.Id + "/stop", null);
            reconnectStop.EnsureSuccessStatusCode();
            await ChatSteeringScript.WaitAsync(() => !chat.IsWorking, "phone Stop cancels reconnect");
            Check(script.Prompts.Count == 2, "stopping reconnect never dispatches the pending phone prompt");
            chat.PhoneConnectionForVerification(null);

            var permission = new CursorPermissionRequest(new("phone-native-permission", "Synthetic command permission", "execute", "pending", "QA only; no command executes", []),
                [new("allow", "Allow once", "allow_once"), new("deny", "Deny", "reject_once")]);
            var answer = chat.PhonePermissionForVerification(permission);
            await ChatSteeringScript.WaitAsync(() => PhoneGate.Snapshot().Length == 1, "native desktop permission is held");
            var approvalId = PhoneGate.Snapshot().Single().Id;
            using var unconfirmed = await http.PostAsJsonAsync("/api/approvals/" + approvalId, new { decision = "allow", confirm = false });
            Check(unconfirmed.StatusCode == HttpStatusCode.Conflict && !answer.IsCompleted, "first destructive tap cannot authorize the desktop request");
            using var confirmed = await http.PostAsJsonAsync("/api/approvals/" + approvalId, new { decision = "allow", confirm = true });
            Check(confirmed.IsSuccessStatusCode && await answer.WaitAsync(TimeSpan.FromSeconds(5)) == "allow", "confirmed phone decision answers the actual desktop callback");

            var dispatched = false;
            using var cancelled = new CancellationTokenSource();
            var queued = PhoneOnWindow(() => { dispatched = true; return true; }, cancelled.Token);
            cancelled.Cancel();
            try { await queued; Check(false, "cancelled queued work must refuse"); }
            catch (OperationCanceledException) { Check(!dispatched, "cancelled queued phone work does not execute on the window"); }
            Check(PhoneStore.Revoke(device), "desktop revoke removes the paired device");
            Check((await http.GetAsync("/api/chats")).StatusCode == HttpStatusCode.Unauthorized, "revoked phone is refused by the actual desktop host");
            _phoneCode.Text = "Disposable QA pairing code consumed. No credential shown.";
            _phoneQr.Visibility = Visibility.Collapsed;
            Microsoft.UI.Xaml.Controls.ScrollViewer? FindScroll(DependencyObject node)
            {
                if (node is Microsoft.UI.Xaml.Controls.ScrollViewer scroll) return scroll;
                for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node); i++)
                    if (FindScroll(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i)) is { } found) return found;
                return null;
            }
            if (FindScroll(_settingsPage) is { } settingsScroll)
            {
                settingsScroll.ChangeView(null, settingsScroll.ScrollableHeight, null, true);
                await Task.Delay(200);
            }
            await CapturePng(Path.Combine(output, "phone-settings.png"));
        }
        finally
        {
            _phoneEnabled.IsOn = false;
            await ApplyPhoneSettingsAsync();
            Check(!PhoneStore.Settings.Enabled && _phoneHost is null, "turning Settings off stops the listener");
        }
    }
}

internal sealed partial class ChatConversation
{
    internal void PhoneConnectionForVerification(Func<CancellationToken, Task>? probe)
    {
        MainWindow.ValidateCodexQaProfile(_profileDirectory);
        _phoneConnectionProbe = probe;
    }
    internal Task<string?> PhonePermissionForVerification(CursorPermissionRequest request) =>
        RequestProjectPermissionAsync(request, CancellationToken.None);
}
