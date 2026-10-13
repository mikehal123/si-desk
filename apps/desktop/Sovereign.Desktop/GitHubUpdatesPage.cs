using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sovereign.Desktop.Updates;
using Windows.Security.Credentials;

namespace Sovereign.Desktop;

public sealed partial class MainWindow
{
    private readonly TextBlock _githubUpdateStatus = Note("Connect GitHub for private updates. Your account must have access to xai-desk.");
    private readonly TextBox _githubDeviceCode = new() { IsReadOnly = true, Visibility = Visibility.Collapsed, Header = "Enter this code at github.com/login/device" };
    private CancellationTokenSource? _githubUpdateSignIn;
    private Button? _cancelGitHubButton;
    private GitHubSession? _githubUpdateSession;
    private readonly SemaphoreSlim _githubTokenLock = new(1, 1);
    private string GitHubUpdateVault => "xAIDesk.GitHubUpdates." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(_vm.ProfileDirectory).ToUpperInvariant())))[..24];

    private void BuildGitHubUpdates()
    {
        _cancelGitHubButton = Button("Cancel GitHub sign-in", () => _githubUpdateSignIn?.Cancel());
        _cancelGitHubButton.Visibility = Visibility.Collapsed;
        _githubWait.Children.Add(_githubUpdateStatus);
        _githubWait.Children.Add(_githubDeviceCode);
        _githubWait.Children.Add(_cancelGitHubButton);
        Closed += (_, _) => _githubUpdateSignIn?.Cancel();
        try
        {
            var credential = new PasswordVault().Retrieve(GitHubUpdateVault, "session"); credential.RetrievePassword();
            using var json = JsonDocument.Parse(credential.Password);
            var root = json.RootElement;
            _githubUpdateSession = new(root.GetProperty("AccessToken").GetString()!, root.GetProperty("RefreshToken").GetString()!, root.GetProperty("ExpiresAt").GetDateTimeOffset());
            _githubUpdateStatus.Text = "GitHub connected for private updates.";
        }
        catch { _githubUpdateSession = null; }
    }
    private void StoreGitHubUpdateSession(GitHubSession session)
    {
        try { new PasswordVault().Add(new PasswordCredential(GitHubUpdateVault, "session", JsonSerializer.Serialize(session))); }
        catch { throw new IOException("Windows could not securely save GitHub access. Connect again to retry."); }
        _githubUpdateSession = session;
    }
    private async Task<string> GitHubUpdateTokenAsync(CancellationToken ct)
    {
        await _githubTokenLock.WaitAsync(ct);
        try
        {
            var session = _githubUpdateSession ?? throw new IOException("Connect GitHub in Settings to receive private updates.");
            if (session.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(2))
            {
                using var auth = new GitHubSignIn(UpdateTrust.GitHubClientId);
                session = await auth.RefreshAsync(session, ct);
                StoreGitHubUpdateSession(session);
            }
            return session.AccessToken;
        }
        finally { _githubTokenLock.Release(); }
    }
    private async Task ConnectGitHubUpdatesAsync()
    {
        if (_githubUpdateSignIn is not null || _updateBusy) return;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_updateLifetime.Token);
        _githubUpdateSignIn = cancel;
        SyncGitHubCancel();
        try
        {
            using var auth = new GitHubSignIn(UpdateTrust.GitHubClientId);
            _githubUpdateStatus.Text = "Requesting a GitHub sign-in code…";
            var code = await auth.BeginAsync(cancel.Token);
            _githubDeviceCode.Text = code.UserCode; _githubDeviceCode.Visibility = Visibility.Visible;
            _githubUpdateStatus.Text = "Enter the code in your browser and authorize xAI Desk private updates. Waiting for GitHub…";
            Process.Start(new ProcessStartInfo(GitHubSignIn.VerificationUrl) { UseShellExecute = true });
            var session = await auth.CompleteAsync(code, cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            StoreGitHubUpdateSession(session);
            _githubUpdateStatus.Text = "GitHub connected. Checking access to private releases…";
            await CheckUpdatesAsync(false);
        }
        catch (OperationCanceledException) { _githubUpdateStatus.Text = "GitHub sign-in canceled."; }
        catch (Exception ex) { _githubUpdateStatus.Text = ex.Message; }
        finally { _githubUpdateSignIn = null; _githubDeviceCode.Text = ""; _githubDeviceCode.Visibility = Visibility.Collapsed; SyncGitHubCancel(); RefreshConnectionStatuses(); }
    }
    private void DisconnectGitHubUpdates()
    {
        if (_updateBusy) { _githubUpdateStatus.Text = "Wait for the update operation to finish before disconnecting."; return; }
        _githubUpdateSignIn?.Cancel();
        _githubUpdateSession = null;
        try { var vault = new PasswordVault(); vault.Remove(vault.Retrieve(GitHubUpdateVault, "session")); } catch { }
        _githubUpdateStatus.Text = "GitHub disconnected on this PC. Revoke the app in GitHub Settings if you also want to remove its authorization.";
        SyncGitHubCancel();
    }
}
