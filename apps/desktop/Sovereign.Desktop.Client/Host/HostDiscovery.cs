using System.Text.Json;
using Sovereign.Desktop.Core;

namespace Sovereign.Desktop.Client.Host;

public sealed record HostEndpoint(string PipeName, string InstanceId, string ProfileId, string? BundleId, int? Pid);

public static class HostDiscovery
{
    public static HostEndpoint? TryRead(string hostProfileDirectory, out string reason, string? expectedProfileId = null)
    {
        reason = "";
        if (!IsAuthorizedProfile(hostProfileDirectory, out reason))
        {
            return null;
        }

        var path = Path.Combine(hostProfileDirectory, "endpoint-v2.json");
        if (!File.Exists(path))
        {
            reason = "No endpoint-v2.json in the host profile. The desktop does not guess a pipe name.";
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var pipe = GetString(root, "pipe_name");
            var instance = GetString(root, "host_instance_id") ?? GetString(root, "instance_id");
            var profile = GetString(root, "profile_id");
            if (pipe is null || instance is null || profile is null)
            {
                reason = "endpoint-v2.json missing pipe_name, host_instance_id/instance_id or profile_id.";
                return null;
            }

            string? bundle = GetString(root, "bundle_version") ?? GetString(root, "bundle_id");
            int? pid = null;
            if (root.TryGetProperty("pid", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n))
            {
                pid = n;
            }

            if (!string.IsNullOrWhiteSpace(expectedProfileId))
            {
                if (!string.Equals(profile, expectedProfileId, StringComparison.Ordinal))
                {
                    reason = "ENDPOINT_PROFILE_MISMATCH: endpoint profile_id is not this profile's host identity.";
                    return null;
                }

                var expectedPipe = InstalledHostSupervisor.ExpectedPipeName(expectedProfileId);
                if (!string.Equals(pipe, expectedPipe, StringComparison.OrdinalIgnoreCase))
                {
                    reason = "ENDPOINT_PIPE_MISMATCH: pipe is not bound to this profile's host identity.";
                    return null;
                }
            }

            reason = "ok";
            return new HostEndpoint(pipe, instance, profile, bundle, pid);
        }
        catch (Exception ex)
        {
            reason = "endpoint-v2.json unreadable: " + ex.Message;
            return null;
        }
    }

    public static bool IsAuthorizedProfile(string path, out string reason)
    {
        var full = Path.GetFullPath(path).TrimEnd('\\') + "\\";
        string[] allowed =
        [
            Path.GetFullPath(Path.GetTempPath()),
            Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), XaiDeskIdentity.DataFolderName)),
            Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), XaiDeskIdentity.LegacyDataFolderName))
        ];
        string[] forbidden = [];

        foreach (var root in forbidden)
        {
            var prefix = root.TrimEnd('\\') + "\\";
            if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && !EngineBundleVerifier.Under(full, EngineBundleVerifier.IndependentQaInstalls))
            {
                reason = "Host profile is under a forbidden tree: " + root + ". Builder/Grok/QA staging hosts are not used.";
                return false;
            }
        }

        foreach (var root in allowed)
        {
            var prefix = root.TrimEnd('\\') + "\\";
            if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                reason = "ok";
                return true;
            }
        }

        reason = "Host profile is outside the authorized writable roots (TEMP and LocalAppData\\SIDesk).";
        return false;
    }

    public static bool IsForbiddenStagingProfile(string path)
    {
        return !IsAuthorizedProfile(path, out _);
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
