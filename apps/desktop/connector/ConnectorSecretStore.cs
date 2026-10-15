namespace Sovereign.Desktop.Connector;

/// <summary>One credential store, addressed by exact name. Missing names read as null.</summary>
public interface ISecretSlot
{
    string? Read(string name);
    void Write(string name, string value);
    void Remove(string name);
}

/// <summary>The connector token lives under one name. Reads and removals never touch any other name.</summary>
public sealed class StoredConnectorSecrets(ISecretSlot slot, string name) : IConnectorSecrets
{
    public string? Current()
    {
        try
        {
            var token = slot.Read(name);
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        catch { return null; }
    }

    /// <summary>The saved token when there is one. A new token is made only when none is saved.</summary>
    public string LoadOrCreate() => Current() ?? Generate();

    public string Generate()
    {
        var token = ConnectorToken.Create();
        try { slot.Remove(name); }
        catch { /* Nothing was saved under this name. */ }
        slot.Write(name, token);
        return token;
    }

    public void Revoke()
    {
        try { slot.Remove(name); }
        catch { /* Already revoked. */ }
    }
}

public sealed record LegacySecret(ISecretSlot Slot, string LegacyName, string Name);

/// <summary>
/// Runs once per profile. Copies each legacy secret to its new name when the new name is empty.
/// Legacy entries stay in place. A marker file stops a later Revoke from being undone by another copy.
/// </summary>
public static class LegacySecretMigration
{
    public const string MarkerFileName = "legacy-secrets-migrated";

    /// <summary>Returns the new names that were filled. Never returns secret values.</summary>
    public static IReadOnlyList<string> RunOnce(string profile, IEnumerable<LegacySecret> secrets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);
        var marker = Path.Combine(profile, MarkerFileName);
        if (File.Exists(marker)) return [];
        var copied = new List<string>();
        var failed = false;
        foreach (var secret in secrets)
        {
            try
            {
                if (CopyIfMissing(secret)) copied.Add(secret.Name);
            }
            catch { failed = true; }
        }
        if (!failed)
        {
            Directory.CreateDirectory(profile);
            File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("o"));
        }
        return copied;
    }

    public static bool CopyIfMissing(LegacySecret secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (!string.IsNullOrWhiteSpace(secret.Slot.Read(secret.Name))) return false;
        var old = secret.Slot.Read(secret.LegacyName);
        if (string.IsNullOrWhiteSpace(old)) return false;
        secret.Slot.Write(secret.Name, old);
        return true;
    }
}

/// <summary>Generic Windows credentials used by the webhook sender key.</summary>
public sealed class WindowsCredentialSlot : ISecretSlot
{
    public string? Read(string name) => GrokbotWakeKey.Read(name);
    public void Write(string name, string value) => GrokbotWakeKey.Save(name, value);
    public void Remove(string name) => GrokbotWakeKey.Clear(name);
}
