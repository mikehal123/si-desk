using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sovereign.Desktop.Client;
using Sovereign.Desktop.Core;
using Windows.Security.Credentials;

namespace Sovereign.Desktop;

public sealed partial class MainWindow
{
    private readonly TextBlock _accountName = new() { Text="Not signed in",FontSize = DeskTheme.SizeCode,VerticalAlignment=VerticalAlignment.Center };
    private readonly TextBlock _accountInitial = new() { Text="?",HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center };
    private readonly TextBlock _signInStatus = Note("Sign in to use your identity in Shared room. AI-provider sign-ins are separate.");
    private SovereignAccountClient? _accountClient;
    private AccountSession? _accountSession;
    private AccountIdentity? _signedInIdentity;
    private CancellationTokenSource? _signInCancel;
    private bool _accountBusy;
    private bool _accountInitialized;
    private string AccountVaultResource => "SovereignAI.Account." + ProfileHash(_vm.ProfileDirectory);

    private IEnumerable<string> AccountVaultResources()
    {
        yield return AccountVaultResource;
        var legacy = XaiDeskIdentity.LegacyDefaultProfile(_vm.ProfileDirectory);
        if (legacy is null) yield break;
        yield return "SovereignAI.Account." + ProfileHash(legacy);
    }

    private static string ProfileHash(string profile) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(profile).ToUpperInvariant())))[..24];

    private async Task InitializeAccountAsync()
    {
        if(_accountInitialized)return; _accountInitialized=true;
        try {
            var path=Path.Combine(AppContext.BaseDirectory,"account-service.json");
            if(!File.Exists(path)) { _signInStatus.Text="Account service is not configured in this build. Local tools remain available."; return; }
            var configuration=JsonSerializer.Deserialize<AccountConfiguration>(await File.ReadAllTextAsync(path),new JsonSerializerOptions { PropertyNameCaseInsensitive=true }) ?? throw new IOException();
            _accountClient=new SovereignAccountClient(configuration);
            var saved=ReadAccountSession();
            if(saved is not null) {
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(25));
                var result=await _accountClient.RefreshAsync(saved,timeout.Token);
                StoreAccountSession(result.Session); SetAccount(result.Session,result.Identity);
            } else { _signInStatus.Text="Welcome. Sign in with your own Sovereign account; your local files stay on this Windows profile."; ShowAccountDialog(); }
        } catch { _signInStatus.Text="Could not restore Sovereign sign-in. Check your connection and sign in again. Local data is unchanged."; }
    }
    private AccountSession? ReadAccountSession()
    {
        foreach (var resource in AccountVaultResources())
        {
            try
            {
                var vault = new PasswordVault();
                string Read(string name) { var c = vault.Retrieve(resource, name); c.RetrievePassword(); return c.Password; }
                return new(Read("access"), Read("refresh"), Read("identity"), DateTimeOffset.Parse(Read("expiry"), System.Globalization.CultureInfo.InvariantCulture));
            }
            catch { /* Try the previous profile hash. */ }
        }
        return null;
    }
    private void ClearAccountSession()
    {
        var vault = new PasswordVault();
        foreach (var resource in AccountVaultResources())
            foreach (var name in new[] { "access", "refresh", "identity", "expiry" })
            {
                try { vault.Remove(vault.Retrieve(resource, name)); } catch { }
            }
    }
    private void StoreAccountSession(AccountSession session)
    {
        var vault=new PasswordVault();
        try {
            foreach(var (name,value) in new[]{("access",session.AccessToken),("refresh",session.RefreshToken),("identity",session.IdToken),("expiry",session.ExpiresAt.ToString("O"))})
                vault.Add(new PasswordCredential(AccountVaultResource,name,value));
        } catch { ClearAccountSession(); throw new IOException("Windows could not securely save your session. Sign-in was not retained."); }
    }
    private void SetAccount(AccountSession session,AccountIdentity identity)
    {
        _accountSession=session; _signedInIdentity=identity;
        _accountName.Text=identity.Name; _accountInitial.Text=System.Globalization.StringInfo.GetNextTextElement(identity.Name).ToUpperInvariant();
        _roomName.Text=identity.Name; _roomName.IsReadOnly=true;
        _signInStatus.Text="Signed in as "+identity.Name+". Local chats and files stay on this Windows profile; they are not cloud-synced.";
        RefreshConnectionStatuses();
    }
    private async void ShowAccountDialog()
    {
        var status=new TextBlock { Text=_signInStatus.Text,TextWrapping=TextWrapping.Wrap,MaxWidth=420 };
        var dialog=new ContentDialog { XamlRoot=Content.XamlRoot,Title="Your Sovereign account",Content=new StackPanel { Spacing = DeskTheme.Space12,Children={
            status,Note("Sign-in opens your browser. Enter your password or email code there. Sovereign never asks you to paste a secret key."),
            Note("Partner beta · existing Clerk development service. Provider subscriptions remain separate.") } },
            PrimaryButtonText=_accountBusy?"Cancel current sign-in":_signedInIdentity is null?"Sign in with browser":"Sign out",CloseButtonText="Close",IsPrimaryButtonEnabled=_accountClient is not null };
        try { if(await dialog.ShowAsync()!=ContentDialogResult.Primary) return; }
        catch(InvalidOperationException) { return; }
        if(_accountBusy) { _signInCancel?.Cancel(); return; }
        if(_signedInIdentity is not null) await SignOutAccountAsync(); else await SignInAccountAsync();
    }
    private async Task SignInAccountAsync()
    {
        if(_accountBusy || _accountClient is null) return;
        _accountBusy=true; _signInCancel=new CancellationTokenSource(); SyncSignInCancel(); _signInStatus.Text="Finish sign-in in your browser. Waiting up to five minutes…";
        var failed=false;
        try {
            var result=await _accountClient.SignInAsync(async uri=>{ if(!await Windows.System.Launcher.LaunchUriAsync(uri)) throw new IOException("Browser could not open."); },_signInCancel.Token);
            StoreAccountSession(result.Session); SetAccount(result.Session,result.Identity);
        } catch(OperationCanceledException) { _signInStatus.Text="Sign-in canceled or timed out. You can try again."; }
        catch(Exception ex) {
            failed=true;
            // Only fixed stage labels and exception type names are recorded, never tokens or exception messages.
            var stage=_accountClient.SignInStage;
            _signInStatus.Text="Sign-in failed at: "+stage+" ("+ex.GetType().Name+"). Open your account to try again.";
            try { File.AppendAllText(Path.Combine(_vm.ProfileDirectory,"account-signin-status.log"),DateTimeOffset.UtcNow.ToString("O")+" | "+stage+" | "+ex.GetType().Name+Environment.NewLine); } catch { }
        }
        finally { _signInCancel.Dispose(); _signInCancel=null; _accountBusy=false; SyncSignInCancel(); }
        if(failed) ShowAccountDialog();
    }
    private async Task SignOutAccountAsync()
    {
        if(_accountBusy)return; _accountBusy=true;
        var old=_accountSession;
        try {
            await StopRoomAsync(); ClearAccountSession(); _accountSession=null; _signedInIdentity=null;
            _accountName.Text="Not signed in";_accountInitial.Text="?";_roomName.Text="Sign in first";
            _signInStatus.Text="Signed out. Local chats and files are still on this Windows profile.";
            RefreshConnectionStatuses();
            if(old is not null && _accountClient is not null) { using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15)); await _accountClient.RevokeAsync(old,timeout.Token); }
        } catch { _signInStatus.Text="Signed out locally. Server revocation could not be confirmed; local files are unchanged."; }
        finally { _accountBusy=false; }
    }
    private async Task<AccountIdentity> RequireAccountAsync(CancellationToken ct)
    {
        if(_accountClient is null || _accountSession is null) throw new IOException("Sign in to Sovereign from Settings or your sidebar profile before joining a room.");
        var result=await _accountClient.RefreshAsync(_accountSession,ct);StoreAccountSession(result.Session);SetAccount(result.Session,result.Identity);return result.Identity;
    }
}
