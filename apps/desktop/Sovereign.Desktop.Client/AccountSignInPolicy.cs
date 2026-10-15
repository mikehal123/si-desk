namespace Sovereign.Desktop.Client;

/// <summary>
/// Public builds do not sign in to an account service.
/// A missing or leftover account-service.json must not enable sign-in and must not throw.
/// </summary>
public static class AccountSignInPolicy
{
    public const string ConfigFileName = "account-service.json";

    public static AccountSignInStartup Inspect(string? baseDirectory)
    {
        var present = false;
        try
        {
            if (!string.IsNullOrWhiteSpace(baseDirectory))
                present = File.Exists(Path.Combine(baseDirectory, ConfigFileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            present = false;
        }
        return new AccountSignInStartup(Enabled: false, ConfigPresent: present);
    }
}

public readonly record struct AccountSignInStartup(bool Enabled, bool ConfigPresent);
