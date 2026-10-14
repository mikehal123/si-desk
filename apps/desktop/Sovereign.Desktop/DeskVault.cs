using Sovereign.Desktop.Connector;
using Sovereign.Desktop.Core;
using Windows.Security.Credentials;

namespace Sovereign.Desktop;

/// <summary>Credential Manager web credentials under one fixed account.</summary>
internal sealed class PasswordVaultSlot(string account) : ISecretSlot
{
    public string? Read(string name)
    {
        try
        {
            var credential = new PasswordVault().Retrieve(name, account);
            credential.RetrievePassword();
            return string.IsNullOrWhiteSpace(credential.Password) ? null : credential.Password;
        }
        catch { return null; }
    }

    public void Write(string name, string value) => new PasswordVault().Add(new PasswordCredential(name, account, value));

    public void Remove(string name)
    {
        var vault = new PasswordVault();
        try { vault.Remove(vault.Retrieve(name, account)); }
        catch { /* That name was not stored. */ }
    }
}

internal static class DeskVault
{
    public static StoredConnectorSecrets ConnectorSecrets() =>
        new(new PasswordVaultSlot(XaiDeskIdentity.GrokBotCredentialAccount), XaiDeskIdentity.GrokBotCredentialResource);

    /// <summary>Copies the xAIDesk connector token and wake key to the SIDesk names once. Old entries stay. Logs names only.</summary>
    public static void MigrateLegacySecrets(string profile)
    {
        try
        {
            var copied = LegacySecretMigration.RunOnce(profile, [
                new LegacySecret(new PasswordVaultSlot(XaiDeskIdentity.GrokBotCredentialAccount), XaiDeskIdentity.LegacyGrokBotCredentialResource, XaiDeskIdentity.GrokBotCredentialResource),
                new LegacySecret(new WindowsCredentialSlot(), GrokbotWakeKey.LegacyTarget, GrokbotWakeKey.Target)]);
            if (copied.Count > 0) App.CrashLog("Copied saved secrets from xAIDesk: " + string.Join(", ", copied));
        }
        catch (Exception ex) { App.CrashLog("Secret migration skipped. " + ex.GetType().Name); }
    }

    public static string? ReadPassword(string resource, string account)
    {
        foreach (var name in XaiDeskIdentity.CredentialResourceNames(resource))
        {
            try
            {
                var credential = new PasswordVault().Retrieve(name, account);
                credential.RetrievePassword();
                if (!string.IsNullOrWhiteSpace(credential.Password)) return credential.Password;
            }
            catch
            {
                // Missing resources throw. Try the previous name next.
            }
        }

        return null;
    }

    public static bool Exists(string resource, string account) => ReadPassword(resource, account) is not null;

    public static void Remove(string resource, string account)
    {
        var vault = new PasswordVault();
        foreach (var name in XaiDeskIdentity.CredentialResourceNames(resource))
        {
            try { vault.Remove(vault.Retrieve(name, account)); }
            catch { /* That name was not stored. */ }
        }
    }
}
