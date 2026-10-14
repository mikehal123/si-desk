using Sovereign.Desktop.Core;
using Windows.Security.Credentials;

namespace Sovereign.Desktop;

internal static class DeskVault
{
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
