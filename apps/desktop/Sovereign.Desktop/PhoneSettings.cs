using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Sovereign.Desktop.Phone;
using Windows.Storage.Streams;

namespace Sovereign.Desktop;

public sealed partial class MainWindow
{
    private PhoneDeviceStore? _phoneStore;
    private PhoneAccessHost? _phoneHost;
    private readonly PhoneApprovalGate _phoneGate = new();
    private bool _phoneUi;
    private readonly SemaphoreSlim _phoneSettingsGate = new(1, 1);
    private readonly ToggleSwitch _phoneEnabled = new() { OnContent = "On", OffContent = "Off" };
    private readonly ComboBox _phoneBind = new() { Width = 220 };
    private readonly ToggleSwitch _phoneNotify = new() { OnContent = "On", OffContent = "Off" };
    private readonly TextBlock _phoneStatus = Note("Off. Nothing is listening.");
    private readonly TextBlock _phoneAddress = Note("");
    private readonly TextBlock _phoneCode = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = DeskTheme.SizeCode };
    private readonly Image _phoneQr = new() { Width = 220, Height = 220, Visibility = Visibility.Collapsed };
    private readonly StackPanel _phoneDevices = new() { Spacing = DeskTheme.Space4 };
    private PhoneDeviceStore PhoneStore => _phoneStore ??= new PhoneDeviceStore(_vm.ProfileDirectory);
    internal PhoneApprovalGate PhoneGate => _phoneGate;

    private StackPanel PhoneSection()
    {
        AutomationName(_phoneEnabled, "Phone access");
        AutomationName(_phoneBind, "Phone access bind mode");
        AutomationName(_phoneNotify, "Phone notifications");
        EnsurePhoneBindOptions();
        _phoneEnabled.Toggled += async (_, _) => await ApplyPhoneSettingsAsync();
        _phoneNotify.Toggled += async (_, _) => await ApplyPhoneSettingsAsync();
        _phoneBind.SelectionChanged += async (_, _) => { if (_phoneBind.SelectedIndex >= 0) await ApplyPhoneSettingsAsync(); };
        Closed += async (_, _) => await StopPhoneAsync();
        LoadPhoneControls();
        return new StackPanel { Spacing = DeskTheme.Space8, Children = {
            Head("Phone access"),
            Note("Same chats as this app. Off until you turn it on. This PC only is 127.0.0.1:8789. Your phone uses Tailscale only, on that machine’s 100.x address, with HTTPS."),
            _phoneEnabled,
            _phoneBind,
            _phoneNotify,
            Note("Notify this phone when a task finishes or needs approval."),
            _phoneStatus,
            _phoneAddress,
            _phoneQr,
            _phoneCode,
            Button("New pairing code", RefreshPhonePairingAsync),
            _phoneDevices } };
    }

    private void EnsurePhoneBindOptions()
    {
        if (_phoneBind.Items.Count > 0) return;
        _phoneBind.Items.Add("This PC only"); _phoneBind.Items.Add("Tailscale only");
    }

    private void LoadPhoneControls()
    {
        EnsurePhoneBindOptions();
        var settings = PhoneStore.Settings;
        _phoneUi = true;
        _phoneEnabled.IsOn = settings.Enabled;
        _phoneBind.SelectedIndex = settings.Bind == PhoneBindMode.Tailscale ? 1 : 0;
        _phoneNotify.IsOn = settings.Notify;
        _phoneUi = false;
        _phoneStatus.Text = settings.Enabled ? "Saved as on. It starts with the app." : "Off. Nothing is listening.";
    }

    internal async Task StartPhoneIfEnabledAsync()
    {
        if (Environment.GetCommandLineArgs().Any(arg => arg.Contains("-verify", StringComparison.Ordinal))) return;
        if (!PhoneStore.Settings.Enabled) return;
        LoadPhoneControls();
        await ApplyPhoneSettingsAsync();
    }

    private async Task ApplyPhoneSettingsAsync()
    {
        if (_phoneUi) return;
        await _phoneSettingsGate.WaitAsync();
        try { await ApplyPhoneSettingsCoreAsync(); }
        catch (Exception)
        {
            _phoneStatus.Text = "Phone access could not start. It remains off; check the address and certificate.";
            _phoneUi = true; _phoneEnabled.IsOn = false; _phoneUi = false;
            try { PhoneStore.SaveSettings(PhoneStore.Settings with { Enabled = false }); }
            catch (Exception) { _phoneStatus.Text += " Settings could not be saved."; }
            try { await StopPhoneAsync(); }
            catch (Exception) { _phoneStatus.Text += " Listener shutdown could not be confirmed."; }
        }
        finally { _phoneSettingsGate.Release(); }
    }

    private async Task ApplyPhoneSettingsCoreAsync()
    {
        var settings = new PhoneSettings(_phoneEnabled.IsOn, _phoneBind.SelectedIndex == 1 ? PhoneBindMode.Tailscale : PhoneBindMode.Loopback, _phoneNotify.IsOn);
        PhoneStore.SaveSettings(settings);
        await StopPhoneAsync();
        if (!settings.Enabled)
        {
            _phoneStatus.Text = "Off. Nothing is listening.";
            _phoneAddress.Text = "";
            _phoneCode.Text = "";
            _phoneQr.Visibility = Visibility.Collapsed;
            RefreshPhoneDevices();
            return;
        }
        _phoneStatus.Text = "Starting…";
        _phoneHost = new PhoneAccessHost(PhoneStore, new DesktopPhoneBridge(this), _phoneGate, PhoneAccessHost.FindWebRoot());
        var error = await _phoneHost.StartAsync();
        if (error is not null)
        {
            _phoneStatus.Text = error;
            _phoneUi = true;
            _phoneEnabled.IsOn = false;
            _phoneUi = false;
            PhoneStore.SaveSettings(settings with { Enabled = false });
            await StopPhoneAsync();
            RefreshPhoneDevices();
            return;
        }
        _phoneStatus.Text = settings.Bind == PhoneBindMode.Tailscale ? "On. Tailscale only." : "On. This PC only.";
        await RefreshPhonePairingAsync();
        RefreshPhoneDevices();
    }

    private async Task RefreshPhonePairingAsync()
    {
        if (_phoneHost is not { Running: true, Origin: { } origin })
        {
            _phoneAddress.Text = "";
            _phoneCode.Text = "";
            _phoneQr.Visibility = Visibility.Collapsed;
            return;
        }
        var code = _phoneHost.IssuePairingCode();
        if (code is null) return;
        var url = origin + "/#" + code;
        _phoneAddress.Text = origin;
        _phoneCode.Text = "Pairing code " + code;
        try
        {
            var bytes = QrCode.Png(url);
            var image = new BitmapImage();
            using var stream = new InMemoryRandomAccessStream();
            var writer = new DataWriter(stream);
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            writer.DetachStream();
            stream.Seek(0);
            await image.SetSourceAsync(stream);
            _phoneQr.Source = image;
            _phoneQr.Visibility = Visibility.Visible;
        }
        catch (Exception ex) { _phoneCode.Text = "Pairing code " + code + ". QR unavailable: " + ex.Message; }
    }

    private void RefreshPhoneDevices()
    {
        _phoneDevices.Children.Clear();
        foreach (var device in PhoneStore.Devices)
        {
            var id = device.Id;
            var row = new Grid { ColumnSpacing = DeskTheme.Space8 };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = Note(device.Name + " · " + device.LastSeen.ToLocalTime().ToString("g"));
            row.Children.Add(label);
            var revoke = Button("Revoke", () => { PhoneStore.Revoke(id); RefreshPhoneDevices(); });
            AutomationName(revoke, "Revoke " + device.Name);
            Grid.SetColumn(revoke, 1);
            row.Children.Add(revoke);
            _phoneDevices.Children.Add(row);
        }
    }

    private async Task StopPhoneAsync()
    {
        var host = _phoneHost;
        _phoneHost = null;
        if (host is not null) await host.DisposeAsync();
    }
}
