using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sovereign.Desktop.Client;
using System.Text.Json;

namespace Sovereign.Desktop;

public sealed partial class MainWindow
{
    // Explicit opt-in acceptance path. It drives the same handlers as the Chat page.
    public static void ValidateCodexQaProfile(string profile)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(profile));
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SovereignAI.DesktopPreview");
        if (!directory.Name.Equals("profile", StringComparison.OrdinalIgnoreCase)
            || directory.Parent is not { } instance
            || !instance.Name.StartsWith("qa-", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(instance.Parent?.FullName, root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Codex chat verification requires an explicit SI Desk qa-* instance profile. Main and default profiles are refused.");
    }

    public async Task RunCodexChatVerificationAsync(string outputDirectory)
    {
        var startupPassed = false;
        var chatgptSignedIn = false;
        var officialSignInOpened = false;
        var turnCompleted = false;
        var reply = "";
        const string expected = "SI Desk local Codex connection verified.";
        const string prompt = "Reply with exactly: " + expected + " Do not use tools.";
        var receiptPath = Path.Combine(outputDirectory, "codex-chat-verification.json");
        var screenshotPath = Path.Combine(outputDirectory, "codex-chat-complete.png");
        void Record(string stage, string? error = null)
        {
            File.WriteAllText(receiptPath, JsonSerializer.Serialize(new
            {
                utc = DateTimeOffset.UtcNow, stage, profile = _vm.ProfileDirectory,
                executable = CodexChatClient.FindExecutable(), transport = "local stdio",
                startupPassed, chatgptSignedIn, officialSignInOpened,
                model = SelectedChatModel, prompt, reply, turnCompleted,
                uiStatus = _chatStatus.Text, screenshot = File.Exists(screenshotPath) ? screenshotPath : null,
                error
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        try
        {
            ValidateCodexQaProfile(_vm.ProfileDirectory);
            Directory.CreateDirectory(outputDirectory);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            Title = "SI Desk · QA local Codex verification";
            await Task.Delay(400, timeout.Token);
            OpenChat();
            _chatProvider.SelectedIndex = 0;
            ResetChat();
            SetChatBusy(true);
            _codexChat = new CodexChatClient(Path.Combine(_vm.ProfileDirectory, "chat-workspace"), profile: _vm.ProfileDirectory);
            chatgptSignedIn = await _codexChat.IsSubscriptionAsync(timeout.Token);
            startupPassed = true;
            Record("local_startup_passed");
            if (!chatgptSignedIn)
            {
                if (!await StartChatSignInAsync()) throw new IOException(_chatStatus.Text);
                officialSignInOpened = true;
                _chatStatus.Text = "QA verification is waiting for official ChatGPT sign-in in your browser. The reply check will continue automatically.";
                Record("awaiting_official_sign_in");
                while (!chatgptSignedIn)
                {
                    await Task.Delay(2000, timeout.Token);
                    chatgptSignedIn = await _codexChat.IsSubscriptionAsync(timeout.Token);
                }
            }
            Record("chatgpt_sign_in_verified");
            await ConnectChatAsync();
            if (!_chatReady) throw new IOException(_chatStatus.Text);
            Record("models_loaded");
            _chatInput.Text = prompt;
            await SendChatAsync();
            reply = _currentChat.LastReply;
            turnCompleted = _chatStatus.Text == "Response complete.";
            if (!turnCompleted || reply.Trim() != expected)
                throw new IOException("Expected a completed visible verification reply. " + _chatStatus.Text);
            await Task.Delay(300, timeout.Token);
            if (_chatPage.Visibility != Visibility.Visible) throw new IOException("Chat page is not visible.");
            await CapturePng(screenshotPath);
            if (!File.Exists(screenshotPath)) throw new IOException("Rendered reply capture was not created.");
            Record("passed");
        }
        catch (Exception ex)
        {
            _chatStatus.Text = "Codex QA verification incomplete: " + ex.Message;
            if (Directory.Exists(outputDirectory)) Record("incomplete", ex.Message);
        }
        finally { SetChatBusy(false); }
    }
}
