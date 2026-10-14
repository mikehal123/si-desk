using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Sovereign.Desktop.Connector;
using Sovereign.Desktop.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Security.Credentials;

namespace Sovereign.Desktop;

public sealed partial class MainWindow
{
    private readonly ToggleSwitch _connectorEnabled = new() { OnContent = "On", OffContent = "Off" };
    private readonly ComboBox _connectorBind = new() { Width = 220 };
    private readonly TextBlock _connectorStatus = Note("Off. Nothing is listening.");
    private readonly TextBlock _connectorAddress = Note("http://127.0.0.1:8790/mcp");
    private readonly TextBox _connectorToken = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly ToggleSwitch _grokbotTab = new() { OnContent = "On", OffContent = "Off", IsOn = true };
    private readonly ToggleSwitch _grokbotAllow = new() { OnContent = "On", OffContent = "Off" };
    private readonly ToggleSwitch _grokbotShots = new() { OnContent = "On", OffContent = "Off" };
    private readonly TextBox _wakeUrl = new() { Header = "Wake webhook URL", IsSpellCheckEnabled = false };
    private readonly PasswordBox _wakeKey = new() { Header = "Webhook sender key" };
    private readonly SemaphoreSlim _connectorGate = new(1, 1);
    private readonly List<Button> _grokbotAvatarButtons = [];
    private bool _connectorUi;
    private ConnectorHost? _connectorHost;
    private readonly VaultConnectorSecrets _connectorSecrets = new();

    private StackPanel ConnectorSection()
    {
        AutomationName(_connectorEnabled, "Grok Bot connector");
        AutomationName(_connectorBind, "Grok Bot connector bind mode");
        AutomationName(_connectorToken, "Grok Bot connector token");
        AutomationId(_connectorToken, "grokbot-blocked-token");
        AutomationName(_grokbotTab, "Grokbot tab");
        AutomationName(_grokbotAllow, "Allow Grokbot control");
        AutomationName(_grokbotShots, "Screenshots");
        AutomationName(_wakeUrl, "Wake webhook URL");
        AutomationName(_wakeKey, "Webhook sender key");
        if (_connectorBind.Items.Count == 0)
        {
            _connectorBind.Items.Add("This PC only");
            _connectorBind.Items.Add("Tailscale only");
        }
        _connectorEnabled.Toggled += async (_, _) => await ApplyConnectorSettingsAsync();
        _connectorBind.SelectionChanged += async (_, _) => { if (_connectorBind.SelectedIndex >= 0) await ApplyConnectorSettingsAsync(); };
        Closed += async (_, _) => await StopConnectorAsync();
        var avatarPicker = BuildGrokbotAvatarPicker();
        LoadConnectorControls();
        var copy = Button("Copy", CopyConnectorToken);
        AutomationName(copy, "Copy Grok Bot connector token");
        var regenerate = Button("Regenerate", RegenerateConnectorTokenAsync);
        AutomationName(regenerate, "Regenerate Grok Bot connector token");
        var revoke = Button("Revoke", RevokeConnectorTokenAsync);
        AutomationName(revoke, "Revoke Grok Bot connector token");
        var test = Button("Test connection", TestConnectorAsync);
        AutomationName(test, "Test Grok Bot connector");
        var ping = Button("Test inbox", () =>
        {
            QueueGrokbotPing();
            _connectorStatus.Text = "Queued a ping in the Grokbot tab.";
        });
        AutomationName(ping, "Test inbox");
        _grokbotTab.Toggled += (_, _) => SaveGrokbotSettings(_grokbotTab.IsOn, _grokbotAllow.IsOn, _grokbotShots.IsOn);
        _grokbotAllow.Toggled += (_, _) => SaveGrokbotSettings(_grokbotTab.IsOn, _grokbotAllow.IsOn, _grokbotShots.IsOn);
        _grokbotShots.Toggled += (_, _) => SaveGrokbotSettings(_grokbotTab.IsOn, _grokbotAllow.IsOn, _grokbotShots.IsOn);
        var saveWake = Button("Save wake", SaveGrokbotWake);
        AutomationName(saveWake, "Save wake");
        var testWake = Button("Test wake", TestGrokbotWakeAsync);
        AutomationName(testWake, "Test wake");
        var clearWake = Button("Clear wake key", ClearGrokbotWakeKey);
        AutomationName(clearWake, "Clear wake key");
        return new StackPanel { Spacing = DeskTheme.Space8, Children = {
            Head("Grok Bot connector"),
            Note("Lets Grok Bot read and post in this app, and pick up the Grokbot tab. Off until you turn it on. This PC only binds 127.0.0.1:8790. Tailscale only runs tailscale ip -4 and binds that address on 8790. It never listens on a public address."),
            _connectorEnabled,
            _connectorBind,
            _connectorAddress,
            _connectorStatus,
            _connectorToken,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = DeskTheme.Space8, Children = { copy, regenerate, revoke, test, ping } },
            Note("Grokbot tab"),
            _grokbotTab,
            Note("Allow Grokbot control"),
            _grokbotAllow,
            Note("Screenshots"),
            _grokbotShots,
            Note("Grokbot avatar"),
            avatarPicker,
            _wakeUrl,
            _wakeKey,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = DeskTheme.Space8, Children = { saveWake, testWake, clearWake } } } };
    }

    private void LoadConnectorControls()
    {
        var settings = ConnectorSettings.Load(_vm.ProfileDirectory);
        _connectorUi = true;
        _connectorEnabled.IsOn = settings.Enabled;
        _connectorBind.SelectedIndex = settings.Bind == ConnectorBindMode.Tailscale ? 1 : 0;
        _connectorToken.Text = "";
        _connectorToken.Visibility = Visibility.Collapsed;
        LoadGrokbotSettings(_grokbotTab, _grokbotAllow, _grokbotShots);
        var avatar = _grokbotMail.PreferencesCopy();
        ShowGrokbotAvatarSelection(avatar.AvatarColor, avatar.AvatarShape);
        RefreshGrokbotAvatarMarks();
        DispatcherQueue.TryEnqueue(RefreshGrokbotAvatarMarks);
        _wakeUrl.Text = _grokbotMail.PreferencesCopy().WakeUrl ?? "";
        ShowWakeKeyMask();
        _connectorUi = false;
        _connectorStatus.Text = settings.Enabled ? "Starting…" : "Off. Nothing is listening.";
        _connectorAddress.Text = "http://127.0.0.1:" + XaiDeskIdentity.GrokBotPort + "/mcp";
    }

    private ScrollViewer BuildGrokbotAvatarPicker()
    {
        _grokbotAvatarButtons.Clear();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DeskTheme.Space8 };
        foreach (var color in GrokbotAvatarChoice.Colors)
        {
            foreach (var shape in GrokbotAvatarChoice.Shapes)
            {
                var pickColor = color;
                var pickShape = shape;
                var button = new Button
                {
                    Content = GrokbotAvatar.Mark(pickColor, pickShape, 28),
                    Tag = pickColor + " " + pickShape,
                    Padding = new Thickness(DeskTheme.Space4),
                    Background = DeskTheme.Transparent,
                    BorderBrush = DeskTheme.Text,
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(DeskTheme.Radius6),
                    MinWidth = 0,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center
                };
                AutomationName(button, "Grokbot avatar " + pickColor + " " + pickShape);
                button.Click += (_, _) => ChooseGrokbotAvatar(pickColor, pickShape);
                row.Children.Add(button);
                _grokbotAvatarButtons.Add(button);
            }
        }
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Disabled,
            Content = row
        };
    }

    private void ChooseGrokbotAvatar(string color, string shape)
    {
        if (_connectorUi) return;
        var prefs = _grokbotMail.PreferencesCopy();
        prefs.AvatarColor = color;
        prefs.AvatarShape = shape;
        _grokbotMail.SavePreferences(prefs);
        var saved = _grokbotMail.PreferencesCopy();
        ShowGrokbotAvatarSelection(saved.AvatarColor, saved.AvatarShape);
        RefreshGrokbotAvatarMarks();
    }

    private void ShowGrokbotAvatarSelection(string? color, string? shape)
    {
        var selected = GrokbotAvatarChoice.NormalizeColor(color) + " " + GrokbotAvatarChoice.NormalizeShape(shape);
        foreach (var button in _grokbotAvatarButtons)
        {
            var on = button.Tag as string == selected;
            button.BorderThickness = new Thickness(on ? 2 : 0);
        }
    }

    internal void RefreshGrokbotAvatarMarks()
    {
        var prefs = _grokbotMail.PreferencesCopy();
        foreach (ListViewItem item in _nav.Items)
        {
            if (item.Tag is not string tag || tag != "grokbot") continue;
            if (item.Content is not StackPanel row || row.Orientation != Orientation.Horizontal || row.Children.Count == 0) continue;
            var mark = GrokbotAvatar.Mark(prefs.AvatarColor, prefs.AvatarShape, 28);
            // The mark grid is named Grokbot. The nav host is not, so only its avatar child is replaced and the unread badge stays.
            if (row.Children[0] is Grid host && AutomationProperties.GetName(host) != "Grokbot")
                ReplaceGrokbotAvatarMark(host, mark);
            else
                ReplaceChild(row.Children, 0, mark);
        }
        if (_grokbotPage.Children.Count == 0) return;
        if (_grokbotPage.Children[0] is not StackPanel header || header.Orientation != Orientation.Horizontal || header.Children.Count == 0) return;
        ReplaceChild(header.Children, 0, GrokbotAvatar.Mark(prefs.AvatarColor, prefs.AvatarShape, 36));
    }

    private static void ReplaceGrokbotAvatarMark(Grid host, UIElement mark)
    {
        var markIndex = -1;
        for (var i = 0; i < host.Children.Count; i++)
        {
            if (host.Children[i] is FrameworkElement element && AutomationProperties.GetName(element) == "Grokbot unread")
                continue;
            markIndex = i;
            break;
        }
        if (markIndex < 0) host.Children.Insert(0, mark);
        else ReplaceChild(host.Children, markIndex, mark);
    }

    private static void ReplaceChild(UIElementCollection children, int index, UIElement next)
    {
        children.RemoveAt(index);
        children.Insert(index, next);
    }

    internal async Task StartConnectorIfEnabledAsync()
    {
        if (Environment.GetCommandLineArgs().Any(arg => arg.Contains("-verify", StringComparison.Ordinal))) return;
        if (!ConnectorSettings.Load(_vm.ProfileDirectory).Enabled) return;
        LoadConnectorControls();
        await ApplyConnectorSettingsAsync();
    }

    private async Task ApplyConnectorSettingsAsync()
    {
        if (_connectorUi) return;
        await _connectorGate.WaitAsync();
        try
        {
            var settings = new ConnectorSettings(_connectorEnabled.IsOn, _connectorBind.SelectedIndex == 1 ? ConnectorBindMode.Tailscale : ConnectorBindMode.Loopback);
            settings.Save(_vm.ProfileDirectory);
            await StopConnectorAsync();
            if (!settings.Enabled)
            {
                _connectorStatus.Text = "Off. Nothing is listening.";
                _connectorAddress.Text = "http://127.0.0.1:" + XaiDeskIdentity.GrokBotPort + "/mcp";
                return;
            }
            if (_connectorSecrets.Current() is null)
            {
                _connectorStatus.Text = "Generate a token first. Nothing is listening.";
                ConnectorSettings.AfterFailedStart(settings).Save(_vm.ProfileDirectory);
                return;
            }
            _connectorStatus.Text = "Starting…";
            _connectorHost = new ConnectorHost(_connectorSecrets, new DeskBridge(this), new ConnectorAudit(ConnectorAudit.DefaultPath()));
            var error = await _connectorHost.StartAsync(true, settings.Bind);
            if (error is not null || !_connectorHost.IsBound || string.IsNullOrWhiteSpace(_connectorHost.Url))
            {
                _connectorStatus.Text = error ?? "Grok Bot connector did not bind. Nothing is listening.";
                await StopConnectorAsync();
                ConnectorSettings.AfterFailedStart(settings).Save(_vm.ProfileDirectory);
                return;
            }
            _connectorAddress.Text = _connectorHost.Url;
            _connectorStatus.Text = settings.Bind == ConnectorBindMode.Tailscale ? "On. Tailscale only." : "On. This PC only.";
        }
        catch (Exception ex)
        {
            _connectorStatus.Text = "Grok Bot connector could not start. " + ConnectorBind.OneLine(ex.Message);
            try { await StopConnectorAsync(); } catch (Exception) { }
        }
        finally { _connectorGate.Release(); }
    }

    private void CopyConnectorToken()
    {
        if (string.IsNullOrWhiteSpace(_connectorToken.Text))
        {
            _connectorStatus.Text = "Generate a token to copy it. A saved token is not shown again.";
            return;
        }
        var package = new DataPackage();
        package.SetText(_connectorToken.Text);
        Clipboard.SetContent(package);
        _connectorStatus.Text = "Token copied. It will not be shown again after you leave this screen.";
    }

    private async Task RegenerateConnectorTokenAsync()
    {
        var token = _connectorSecrets.Generate();
        _connectorToken.Text = token;
        _connectorToken.Visibility = Visibility.Visible;
        _connectorStatus.Text = "New token. Copy it now. It is not shown again.";
        if (_connectorEnabled.IsOn) await ApplyConnectorSettingsAsync();
    }

    private async Task RevokeConnectorTokenAsync()
    {
        _connectorSecrets.Revoke();
        _connectorToken.Text = "";
        _connectorToken.Visibility = Visibility.Collapsed;
        _connectorUi = true;
        _connectorEnabled.IsOn = false;
        _connectorUi = false;
        new ConnectorSettings(false, _connectorBind.SelectedIndex == 1 ? ConnectorBindMode.Tailscale : ConnectorBindMode.Loopback).Save(_vm.ProfileDirectory);
        await StopConnectorAsync();
        _connectorStatus.Text = "Token revoked. Off. Nothing is listening.";
    }

    private async Task TestConnectorAsync()
    {
        var url = _connectorHost is { IsBound: true } ? _connectorHost.Url : null;
        if (string.IsNullOrWhiteSpace(url))
        {
            _connectorStatus.Text = "Off. Nothing is listening.";
            return;
        }
        var token = _connectorSecrets.Current();
        if (string.IsNullOrEmpty(token))
        {
            _connectorStatus.Text = "Generate a token first. Nothing was sent.";
            return;
        }
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var payload = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "status", arguments = new { } } });
            using var body = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(url, body);
            var text = await response.Content.ReadAsStringAsync();
            if (text.Contains(token, StringComparison.Ordinal)) text = "";
            if (!response.IsSuccessStatusCode)
            {
                _connectorStatus.Text = "Test failed. HTTP " + (int)response.StatusCode + (text.Length == 0 ? "." : ". " + ConnectorBind.OneLine(text));
                return;
            }
            var shown = text;
            try
            {
                using var json = JsonDocument.Parse(text);
                shown = json.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString() ?? "";
            }
            catch (Exception) { /* Show the raw reply when it is not the status tool shape. */ }
            if (shown.Contains(token, StringComparison.Ordinal)) shown = "";
            _connectorStatus.Text = "Connected. " + ConnectorBind.OneLine(shown.Replace('\n', ' '));
        }
        catch (Exception ex)
        {
            var message = ConnectorBind.OneLine(ex.Message);
            if (message.Contains(token, StringComparison.Ordinal)) message = "The connection failed.";
            _connectorStatus.Text = "Test failed. " + message;
        }
    }

    private void SaveGrokbotWake()
    {
        var url = (_wakeUrl.Text ?? "").Trim();
        var prefs = _grokbotMail.PreferencesCopy();
        prefs.WakeUrl = url;
        _grokbotMail.SavePreferences(prefs);
        _wakeUrl.Text = url;
        var key = (_wakeKey.Password ?? "").Trim();
        if (key.Length == 0)
        {
            ShowWakeKeyMask();
            _connectorStatus.Text = "Wake URL saved";
            return;
        }
        try
        {
            GrokbotWakeKey.Save(key);
            ShowWakeKeyMask();
            _connectorStatus.Text = "Wake key saved";
        }
        catch { _connectorStatus.Text = "Wake key could not be saved."; }
    }

    private void ClearGrokbotWakeKey()
    {
        try { GrokbotWakeKey.Clear(); }
        catch { /* Read below reports whether the stored key is still there. */ }
        ShowWakeKeyMask();
        _connectorStatus.Text = GrokbotWakeKey.Read() is null ? "Key not set" : "Wake key could not be cleared.";
    }

    private void ShowWakeKeyMask()
    {
        _wakeKey.Password = "";
        _wakeKey.PlaceholderText = GrokbotWakeKey.Read() is null ? "" : "••••••••";
    }

    private static string? CurrentGrokbotWakeKey() => GrokbotWakeKey.Read();

    private async Task TestGrokbotWakeAsync()
    {
        var url = (_wakeUrl.Text ?? "").Trim();
        if (url.Length == 0)
        {
            _connectorStatus.Text = "Wake webhook URL is not set.";
            return;
        }
        var typed = (_wakeKey.Password ?? "").Trim();
        var key = typed.Length > 0 ? typed : CurrentGrokbotWakeKey();
        if (string.IsNullOrEmpty(key))
        {
            _connectorStatus.Text = "Webhook sender key is not set.";
            return;
        }
        string status;
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            var receipt = await GrokbotWake.DeliverAsync(handler, url, key, "test", false, "test wake", CancellationToken.None);
            status = receipt.StatusCode is int code ? "HTTP " + code : receipt.Note;
        }
        catch { status = "Wake failed."; }
        if (status.Contains(key, StringComparison.Ordinal)) status = "Wake failed.";
        _connectorStatus.Text = status;
    }

    private async Task StopConnectorAsync()
    {
        if (_connectorHost is not { } host) return;
        _connectorHost = null;
        await host.DisposeAsync();
    }

    private sealed class VaultConnectorSecrets : IConnectorSecrets
    {
        public string? Current()
        {
            try
            {
                var credential = new PasswordVault().Retrieve(XaiDeskIdentity.GrokBotCredentialResource, XaiDeskIdentity.GrokBotCredentialAccount);
                credential.RetrievePassword();
                return string.IsNullOrEmpty(credential.Password) ? null : credential.Password;
            }
            catch { return null; }
        }

        public string Generate()
        {
            var token = ConnectorToken.Create();
            var vault = new PasswordVault();
            try { vault.Remove(vault.Retrieve(XaiDeskIdentity.GrokBotCredentialResource, XaiDeskIdentity.GrokBotCredentialAccount)); }
            catch { /* No token stored yet. */ }
            vault.Add(new PasswordCredential(XaiDeskIdentity.GrokBotCredentialResource, XaiDeskIdentity.GrokBotCredentialAccount, token));
            return token;
        }

        public void Revoke()
        {
            try
            {
                var vault = new PasswordVault();
                vault.Remove(vault.Retrieve(XaiDeskIdentity.GrokBotCredentialResource, XaiDeskIdentity.GrokBotCredentialAccount));
            }
            catch { /* Already revoked. */ }
        }
    }

    private sealed class DeskBridge(MainWindow window) : IGrokBotDesk
    {
        public GrokBotStatus Status() => OnWindow(() =>
        {
            var version = typeof(MainWindow).Assembly.GetName().Version;
            var current = window._currentChat;
            return new GrokBotStatus(
                version is { Build: >= 0 } ? version.ToString(3) : "unknown",
                current?.PhoneProviderLabel,
                current?.PhoneModel,
                window._conversations.Any(chat => chat.IsWorking));
        });

        public IReadOnlyList<GrokBotChat> ListChats() => OnWindow(() =>
            (IReadOnlyList<GrokBotChat>)window._conversations.Select(chat => new GrokBotChat(chat.Id, chat.Title)).ToArray());

        public IReadOnlyList<GrokBotMessage> ReadChat(string? conversation, int limit) => OnWindow(() =>
        {
            var chat = string.IsNullOrWhiteSpace(conversation) ? window._currentChat : window._conversations.FirstOrDefault(item => item.Id == conversation);
            if (chat is null) throw new KeyNotFoundException("That chat is not open.");
            return (IReadOnlyList<GrokBotMessage>)chat.ConnectorTranscript().TakeLast(Math.Clamp(limit, 1, 50))
                .Select(line => new GrokBotMessage(line.Author, line.Text, line.Model)).ToArray();
        });

        public void SendToChat(string conversation, string text) => UiDelivery.Invoke(window.DispatcherQueue.HasThreadAccess, () =>
        {
            var chat = window._conversations.FirstOrDefault(item => item.Id == conversation) ?? throw new KeyNotFoundException("That chat is not open.");
            chat.AcceptGrokBotMessage(text);
        }, post =>
        {
            if (!window.DispatcherQueue.TryEnqueue(() => post())) throw new IOException("The desktop window is closed.");
        }, TimeSpan.FromSeconds(2));

        public IReadOnlyList<GrokBotMessage> ReadSharedRoom(int limit) => OnWindow(() =>
            (IReadOnlyList<GrokBotMessage>)window._roomHistory.TakeLast(Math.Clamp(limit, 1, 50))
                .Select(message => new GrokBotMessage(message.Name, message.Text, string.IsNullOrEmpty(message.Model) ? null : message.Model)).ToArray());

        public void PostSharedRoom(string text) => OnWindow(() =>
        {
            if (window._roomHost is null) throw new InvalidOperationException("Shared room is not open on this PC.");
            var message = window._roomHost.AppendNamed(text);
            if (window._roomHistory.All(item => item.Sequence != message.Sequence))
            {
                window._roomHistory.Add(message);
                window._roomSequence = message.Sequence;
                window.ShowRoomMessage(message);
            }
            return 0;
        });

        public IReadOnlyList<GrokBotSubagent> ListSubagents(string conversation) => OnWindow(() =>
        {
            var chat = window._conversations.FirstOrDefault(item => item.Id == conversation) ?? throw new KeyNotFoundException("That chat is not open.");
            return (IReadOnlyList<GrokBotSubagent>)chat.ConnectorSubagents().Select(Map).ToArray();
        });

        public GrokBotSubagent? ReadSubagent(string id) => OnWindow(() =>
            window._conversations.SelectMany(chat => chat.ConnectorSubagents()).Select(Map).FirstOrDefault(item => item.Id == id));

        private static GrokBotSubagent Map(Sovereign.Desktop.Client.CursorSubagent child) =>
            new(child.SessionId, child.Name, child.Task, child.State, child.Output, child.ModelName ?? child.ModelId);

        public void NoteGrokbotPoll() => window.GrokbotMail.NotePoll();

        public IReadOnlyList<GrokbotMail> ListGrokbotInbox(string? since)
        {
            var rows = window.GrokbotMail.ListSince(since);
            window.DispatcherQueue.TryEnqueue(window.RefreshGrokbot);
            return rows;
        }

        public GrokbotMail ReplyGrokbotInbox(string messageId, string text)
        {
            var mail = window.GrokbotMail.Reply(messageId, text);
            window.DispatcherQueue.TryEnqueue(window.RefreshGrokbot);
            return mail;
        }

        public GrokbotPng CaptureGrokbotScreenshot()
        {
            window.GrokbotMail.EnsureScreenshot();
            ShowControlBanner();
            try
            {
                return OnWindowAsync(async () =>
                {
                    var bytes = await window.CaptureGrokbotPngAsync();
                    return new GrokbotPng("PNG " + bytes.Length + " bytes", bytes);
                });
            }
            finally { HideControlBanner(); }
        }

        public string GrokbotUiTree() => Act(() =>
        {
            var rows = window.ReadGrokbotControls();
            return string.Join("\n", rows.Select(item => string.Join('\t', item.Id, item.Type, item.Label, item.X, item.Y, item.Width, item.Height)));
        });

        public string GrokbotClick(string? controlId, int? x, int? y) => Act(() => window.PerformGrokbotClick(controlId, x, y));

        public string GrokbotType(string controlId, string text) => Act(() => window.PerformGrokbotType(controlId, text));

        public string GrokbotKey(string keys) => Act(() => window.PerformGrokbotKeys(keys));

        public string GrokbotNavigate(string page) => Act(() =>
        {
            window.OpenNamedPage(page);
            return "Opened " + page.Trim() + ".";
        });

        public void RecordGrokbotTool(string tool, string args, string result)
        {
            try { window.GrokbotMail.Record(tool, args, result); }
            catch (Exception) { }
            window.DispatcherQueue.TryEnqueue(window.RefreshGrokbot);
        }

        public string ReadSwarmBoard(string? swarmId, string? runId = null) =>
            SwarmBoard.ReadText(string.IsNullOrWhiteSpace(swarmId) ? null : swarmId, null, string.IsNullOrWhiteSpace(runId) ? null : runId);

        public string SubmitSwarmResult(string taskId, string result, string proof, string? swarmId = null, string? runId = null)
        {
            var board = SwarmBoard.RequireTask(taskId, null, out _, string.IsNullOrWhiteSpace(swarmId) ? null : swarmId, string.IsNullOrWhiteSpace(runId) ? null : runId);
            var task = board.Submit(taskId, result, proof);
            return task.Id + "\t" + task.Status;
        }

        public bool GrokbotReviewsHandoffs(string conversationId) => OnWindow(() => ReviewsOn(conversationId));

        public bool ConversationOpen(string conversationId) => OnWindow(() =>
            !string.IsNullOrWhiteSpace(conversationId) && window._conversations.Any(item => item.Id == conversationId.Trim()));

        public void DeliverSwarmReview(string conversationId, string text, bool resume) => UiDelivery.Invoke(window.DispatcherQueue.HasThreadAccess, () =>
        {
            var chat = window._conversations.FirstOrDefault(item => item.Id == conversationId) ?? throw new KeyNotFoundException("That chat is not open.");
            chat.AcceptGrokbotReview(text, resume);
        }, post =>
        {
            if (!window.DispatcherQueue.TryEnqueue(() => post())) throw new IOException("The desktop window is closed.");
        }, TimeSpan.FromSeconds(2));

        public string? SwarmBoardRoot() => null;

        private bool ReviewsOn(string conversationId)
        {
            if (string.IsNullOrWhiteSpace(conversationId)) return false;
            var id = conversationId.Trim();
            var open = window._conversations.FirstOrDefault(item => item.Id == id);
            if (open is not null) return open.GrokbotReviewsHandoffs;
            if (!Guid.TryParseExact(id, "N", out _)) return false;
            try
            {
                var path = Path.Combine(window._vm.ProfileDirectory, "chats", id + ".json");
                if (!File.Exists(path)) return false;
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                return json.RootElement.TryGetProperty("GrokbotReviewsHandoffs", out var flag) && flag.ValueKind == JsonValueKind.True;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
            {
                return false;
            }
        }

        private string Act(Func<string> work)
        {
            window.GrokbotMail.EnsureAction("action");
            ShowControlBanner();
            try { return OnWindow(work); }
            finally { HideControlBanner(); }
        }

        private void ShowControlBanner()
        {
            var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!window.DispatcherQueue.TryEnqueue(() =>
            {
                window.GrokbotMail.BeginControl();
                window.SetGrokbotControlling(true);
                shown.TrySetResult();
            })) throw new IOException("The desktop window is closed.");
            shown.Task.GetAwaiter().GetResult();
            Thread.Sleep(40);
        }

        private void HideControlBanner() => OnWindow(() =>
        {
            window.GrokbotMail.EndControl();
            window.SetGrokbotControlling(false);
            return 0;
        });

        private T OnWindowAsync<T>(Func<Task<T>> work)
        {
            if (window.DispatcherQueue.HasThreadAccess) return work().GetAwaiter().GetResult();
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!window.DispatcherQueue.TryEnqueue(async () =>
            {
                try { done.TrySetResult(await work()); }
                catch (Exception ex) { done.TrySetException(ex); }
            })) throw new IOException("The desktop window is closed.");
            return done.Task.GetAwaiter().GetResult();
        }

        private T OnWindow<T>(Func<T> work)
        {
            if (window.DispatcherQueue.HasThreadAccess) return work();
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!window.DispatcherQueue.TryEnqueue(() =>
            {
                try { done.TrySetResult(work()); }
                catch (Exception ex) { done.TrySetException(ex); }
            })) throw new IOException("The desktop window is closed.");
            return done.Task.GetAwaiter().GetResult();
        }
    }
}
