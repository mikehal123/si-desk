namespace Sovereign.Desktop.Client;

/// <summary>App-authored diagnostic: never includes raw provider response bodies.</summary>
public sealed class ProjectAgentException(string message) : IOException(message)
{
    public static string Describe(Exception error) => error switch
    {
        GrokBuildUsageException usage => usage.Message,
        ProjectAgentException known => known.Message,
        System.Text.Json.JsonException => "The provider returned malformed JSON or tool arguments. The current response could not be processed; review completed activity before continuing.",
        HttpRequestException http when http.StatusCode is { } status => $"Provider HTTP request failed ({(int)status}). Check the connection, model and account quota.",
        HttpRequestException => "The provider connection failed. Completed edits are kept; check the connection before continuing.",
        IOException io when io.Message.StartsWith("Provider returned HTTP ", StringComparison.Ordinal)
            => ParseHttpStatus(io.Message),
        IOException io when DeskConnectionErrors.IsAuthored(io.Message) => io.Message,
        _ => "The run failed locally. Completed edits are kept. Review activity and the current connection status before continuing."
    };

    private static string ParseHttpStatus(string message)
    {
        var digits = message["Provider returned HTTP ".Length..].Split('.')[0];
        return int.TryParse(digits, out var code) && code is >= 100 and <= 599
            ? $"Provider returned HTTP {code}. Check the selected endpoint, model, account access and quota. No alternate endpoint was used."
            : "The provider rejected the request. Check account access and the selected endpoint.";
    }
}
