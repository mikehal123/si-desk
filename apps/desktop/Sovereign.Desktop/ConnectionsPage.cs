using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sovereign.Desktop.Client;
using Windows.Security.Credentials;

namespace Sovereign.Desktop;

public sealed partial class MainWindow
{
    private async Task ShowConnectionsAsync()
    {
        using var lifetime = new CancellationTokenSource();
        var page = new StackPanel { Spacing = DeskTheme.Space16, Width = 620 };
        page.Children.Add(Note("Set up once on this Windows account. Updates keep your profile and saved credentials. Subscriptions use the account you sign in with on this PC."));
        var dialog = new ContentDialog { RequestedTheme = ElementTheme.Dark, XamlRoot = ((FrameworkElement)Content).XamlRoot, Title = "Connections", CloseButtonText = "Done",
            Content = new ScrollViewer { Content = page, MaxHeight = 540, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Enabled } };
        dialog.Resources["ContentDialogMaxWidth"] = 720d;
        dialog.Closing += (_, _) => lifetime.Cancel();
        string? Key(string resource, string user) => DeskVault.ReadPassword(resource, user);
        StackPanel Section(string name, string initial, out TextBlock state)
        {
            var section = new StackPanel { Spacing = DeskTheme.Space8 }; section.Children.Add(Head(name));
            state = Note(initial); section.Children.Add(state); page.Children.Add(section); return section;
        }
        void Action(StackPanel section, string label, TextBlock status, Func<CancellationToken, Task<string>> work)
        {
            var button = new Button { Content = label };
            button.Click += async (_, _) => {
                button.IsEnabled = false; status.Text = "Checking…";
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromMinutes(3));
                try { status.Text = await work(timeout.Token); }
                catch (OperationCanceledException) { status.Text = "Canceled or timed out. Saved credentials were kept."; }
                catch (FileNotFoundException) { status.Text = "Connector missing. Install it or locate its executable, then check again."; }
                catch { status.Text = "Could not complete this step. Check installation, sign-in, network and plan access. No credentials were removed."; }
                finally { button.IsEnabled = true; }
            };
            section.Children.Add(button);
        }
        async Task<string> Open(string url) => await Windows.System.Launcher.LaunchUriAsync(new Uri(url)) ? "Official setup page opened. Return here after installation." : "Windows could not open the setup page.";
        string codexState;
        try { codexState = "Installed: " + ConnectionSetup.FindCodex() + "\nAccount not checked yet."; }
        catch { codexState = "Codex executable not found. Install the official CLI, or locate your existing codex.exe."; }
        var codex = Section("Codex · ChatGPT subscription", codexState, out var cs);
        Action(codex, "Official Codex installation guide", cs, _ => Open("https://developers.openai.com/codex/cli"));
        Action(codex, "Locate installed codex.exe…", cs, async _ => {
            var picker = new Windows.Storage.Pickers.FileOpenPicker(); picker.FileTypeFilter.Add(".exe");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync(); if (file is null) return "Selection canceled.";
            ConnectionSetup.SaveCodexPath(file.Path); return "Executable location saved. Use Check Codex below.";
        });
        var chatWorkspace = Path.Combine(_vm.ProfileDirectory, "chat-workspace");
        Action(codex, "Check Codex account and models", cs, async ct => {
            using var client = new CodexChatClient(chatWorkspace);
            if (!await client.IsSubscriptionAsync(ct)) return "Installed · sign in with ChatGPT required.";
            var models = await client.ModelsAsync(ct); return $"Signed in with ChatGPT · {models.Length} models returned. Message delivery is checked when you send a chat.";
        });
        Action(codex, "Sign in with ChatGPT", cs, async ct => {
            using var client = new CodexChatClient(chatWorkspace);
            var uri = new Uri(await client.LoginAsync(ct));
            if (uri.Scheme != "https" || !(uri.Host == "auth.openai.com" || uri.Host == "chatgpt.com" || uri.Host.EndsWith(".openai.com", StringComparison.OrdinalIgnoreCase))) throw new IOException();
            await Windows.System.Launcher.LaunchUriAsync(uri); cs.Text = "Finish sign-in in your browser. Keep this window open.";
            while (!await client.IsSubscriptionAsync(ct)) await Task.Delay(1500, ct);
            return "Signed in with ChatGPT. In chat, choose Codex and Connect / refresh models.";
        });
        bool CursorInstalled() => File.Exists(Path.Combine(CursorChatClient.RuntimeDirectory, "node.exe")) && File.Exists(Path.Combine(CursorChatClient.RuntimeDirectory, "index.js"));
        var cursor = Section("Cursor · Grok and other advertised models", CursorInstalled() ? "Connector installed · account not checked." : "Cursor connector not installed.", out var crs);
        Action(cursor, "Install Cursor connector", crs, async ct => {
            if (CursorInstalled()) return "Connector already installed. Sign in or check models below.";
            var script = Path.Combine(AppContext.BaseDirectory, "setup", "Install-CursorConnection.ps1");
            if (!File.Exists(script)) throw new FileNotFoundException();
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new IOException();
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(ct); await Task.WhenAll(output, error); }
            finally { if (!process.HasExited) process.Kill(true); }
            if (process.ExitCode != 0 || !CursorInstalled()) throw new IOException();
            return "Verified Cursor connector installed. Sign in with your Cursor account.";
        });
        cursor.Children.Add(Note("Downloads the pinned official Cursor package and checks its SHA-256. Your partner's subscription session is not included."));
        Action(cursor, "Sign in with Cursor", crs, async ct => {
            using var client = new CursorChatClient(Path.Combine(_vm.ProfileDirectory, "cursor-chat-workspace"));
            await client.LoginAsync(ct); return "Cursor sign-in completed. Check advertised models, then connect your chat.";
        });
        Action(cursor, "Check Cursor models", crs, async ct => {
            using var client = new CursorChatClient(Path.Combine(_vm.ProfileDirectory, "cursor-chat-workspace"));
            var models = await client.ModelsAsync(ct);
            return $"Connector responded · {models.Length} models advertised. This is not proof of subscription entitlement; a chat request checks model access.";
        });
        foreach (var provider in new[] { "glm", "kimi", "moonshot" })
        {
            var section = Section(provider == "moonshot" ? "Kimi Platform API · usage billed" : provider.ToUpperInvariant() + " · coding plan", Key("SIDesk.Unused", provider) is null ? "No saved key." : "Key saved · not tested yet.", out var status);
            var secret = new PasswordBox { PlaceholderText = provider == "moonshot" ? "Your platform.kimi.ai API key" : "Your coding-plan key" }; section.Children.Add(secret);
            if (provider == "moonshot") section.Children.Add(Note("Uses your Kimi Platform API balance. Separate from Kimi Code subscription credentials."));
            var endpoint = new ComboBox { ItemsSource = CodingPlanChatClient.Endpoints(provider), SelectedItem = ConnectionSetup.Endpoint(provider), HorizontalAlignment = HorizontalAlignment.Stretch }; section.Children.Add(endpoint);
            Action(section, "Save " + provider.ToUpperInvariant() + " connection", status, _ => {
                var key = string.IsNullOrWhiteSpace(secret.Password) ? Key("SIDesk.Unused", provider) : secret.Password.Trim();
                if (key is null) return Task.FromResult("Enter your key first.");
                ConnectionSetup.SaveEndpoint(provider, (string)endpoint.SelectedItem);
                DeskVault.Remove("SIDesk.Unused", provider);
                new PasswordVault().Add(new PasswordCredential("SIDesk.Unused", provider, key)); secret.Password = "";
                return Task.FromResult("Key and endpoint saved. Reconnect existing chats to apply the change.");
            });
            Action(section, "Test " + provider.ToUpperInvariant() + " model listing", status, async ct => {
                var key = Key("SIDesk.Unused", provider); if (key is null) return "Save the connection first.";
                using var client = new CodingPlanChatClient(provider, ConnectionSetup.Endpoint(provider), key);
                var models = await client.ModelsAsync(ct);
                return models.Length == 0 ? "Model listing unavailable. Credentials and chat access are not yet verified." : $"Connected · {models.Length} models returned. A chat request verifies message delivery.";
            });
        }
        var ollama = Section("Ollama · local models", "Requires Ollama running and models downloaded on this PC.", out var os);
        Action(ollama, "Official Ollama installer", os, _ => Open("https://ollama.com/download/windows"));
        Action(ollama, "Check installed Ollama models", os, async ct => { using var client = new OllamaChatClient(); var models = await client.ModelsAsync(ct); return models.Length == 0 ? "Ollama responded · no models installed." : $"Ollama responded · {models.Length} local models available."; });
        page.Children.Add(Note("Checks run only when clicked. Keys stay in Windows Credential Manager; connector paths and endpoints stay outside the install folder. Never send passwords, tokens or .env files to your partner."));
        await dialog.ShowAsync();
        foreach (var chat in _conversations) chat.ReloadSavedConnections();
    }
}
