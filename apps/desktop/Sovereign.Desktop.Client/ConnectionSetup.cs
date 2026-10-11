namespace Sovereign.Desktop.Client;

/// <summary>Non-secret, per-Windows-user connector configuration, outside the install tree.</summary>
public static class ConnectionSetup
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "xAIDesk", "Connections");
    public static string Endpoint(string provider)
    {
        var allowed = CodingPlanChatClient.Endpoints(provider);
        try { var value = File.ReadAllText(Path.Combine(Root, provider + "-endpoint.txt")).Trim(); if (allowed.Contains(value)) return value; } catch (IOException) { }
        return allowed[0];
    }
    public static void SaveEndpoint(string provider, string endpoint)
    {
        if (!CodingPlanChatClient.Endpoints(provider).Contains(endpoint)) throw new ArgumentException("Unsupported endpoint.");
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, provider + "-endpoint.txt"), endpoint);
    }
}
