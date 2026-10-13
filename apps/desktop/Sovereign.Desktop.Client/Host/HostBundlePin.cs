using System.Text.Json;

namespace Sovereign.Desktop.Client.Host;

public sealed record HostBundlePin(
    string Schema,
    string ArchiveSha256,
    string BundleVersion,
    string ContractPacket,
    string SourceRevision,
    string EngineRelative,
    int FilesVerified,
    string StartScript,
    string StopScript,
    string? Archive = null,
    string? StageRoot = null,
    string? ProfileRoot = null,
    string? ProfileId = null,
    string? InstallDir = null);

public static class HostBundlePinFile
{
    public const string EngineRelativeDefault = "engine-bundle";

    public static HostBundlePin? TryRead(string path, out string reason)
    {
        reason = "";
        if (!File.Exists(path))
        {
            reason = "engine-bundle-pin.json not found";
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            string Req(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? ""
                : "";
            var engineRelative = Req("engine_relative");
            if (string.IsNullOrWhiteSpace(engineRelative))
            {
                engineRelative = EngineRelativeDefault;
            }

            var pin = new HostBundlePin(
                Req("schema"),
                Req("archive_sha256"),
                Req("bundle_version"),
                Req("contract_packet"),
                Req("source_revision"),
                engineRelative,
                root.TryGetProperty("files_verified", out var n) && n.TryGetInt32(out var count) ? count : 0,
                string.IsNullOrWhiteSpace(Req("start_script")) ? "Start-SovereignHost.ps1" : Req("start_script"),
                string.IsNullOrWhiteSpace(Req("stop_script")) ? "Stop-SovereignHost.ps1" : Req("stop_script"),
                Req("archive"),
                Req("stage_root"),
                Req("profile_root"),
                Req("profile_id"),
                Req("install_dir"));
            if (pin.ArchiveSha256.Length != 64)
            {
                reason = "engine-bundle-pin.json missing archive_sha256";
                return null;
            }

            reason = "ok";
            return pin;
        }
        catch (Exception ex)
        {
            reason = "engine-bundle-pin.json unreadable: " + ex.Message;
            return null;
        }
    }
}
