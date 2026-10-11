using Sovereign.Desktop.Connector;
using Xunit;

namespace XaiDesk.Tests;

public sealed class SwarmActivityFoldTests
{
    [Fact]
    public void Expanded_is_true_only_for_open()
    {
        Assert.True(SwarmActivityFold.Expanded("open"));
        Assert.True(SwarmActivityFold.Expanded("  open\r\n"));
        Assert.False(SwarmActivityFold.Expanded("closed"));
        Assert.False(SwarmActivityFold.Expanded("OPEN"));
        Assert.False(SwarmActivityFold.Expanded("opened"));
        Assert.False(SwarmActivityFold.Expanded(""));
        Assert.False(SwarmActivityFold.Expanded(" "));
        Assert.False(SwarmActivityFold.Expanded(null));
    }

    [Fact]
    public void Preview_is_the_last_non_empty_line()
    {
        Assert.Equal("", SwarmActivityFold.Preview(null));
        Assert.Equal("", SwarmActivityFold.Preview([]));
        Assert.Equal("", SwarmActivityFold.Preview(["", "   ", "\n"]));
        Assert.Equal("newest", SwarmActivityFold.Preview(["older", "newest"]));
        Assert.Equal("kept", SwarmActivityFold.Preview(["kept", "", "   "]));
        Assert.Equal("trimmed", SwarmActivityFold.Preview(["first", "  trimmed  "]));
    }

    [Fact]
    public void Remember_and_load_round_trip_and_missing_file_stays_collapsed()
    {
        var root = Temp();
        try
        {
            var id = Guid.NewGuid().ToString("N");
            var path = Path.Combine(root, "swarm-activity", id);
            Assert.False(SwarmActivityFold.Load(root, id));
            Assert.False(File.Exists(path));

            SwarmActivityFold.Remember(root, id, true);
            Assert.Equal("open", File.ReadAllText(path));
            Assert.True(SwarmActivityFold.Load(root, id));

            SwarmActivityFold.Remember(root, id, false);
            Assert.Equal("closed", File.ReadAllText(path));
            Assert.False(SwarmActivityFold.Load(root, id));

            var other = Guid.NewGuid().ToString("D");
            Assert.False(SwarmActivityFold.Load(root, other));
            SwarmActivityFold.Remember(root, other, true);
            Assert.Equal("open", File.ReadAllText(Path.Combine(root, "swarm-activity", other)));
            Assert.True(SwarmActivityFold.Load(root, other));
            Assert.False(SwarmActivityFold.Load(root, id));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void Remember_refuses_a_non_guid_conversation_id()
    {
        var root = Temp();
        try
        {
            var escapeName = "activity-fold-escape-" + Guid.NewGuid().ToString("N");
            var outside = Path.GetFullPath(Path.Combine(root, "..", escapeName));
            Assert.Throws<ArgumentException>(() => SwarmActivityFold.Remember(root, "..\\" + escapeName, true));
            Assert.Throws<ArgumentException>(() => SwarmActivityFold.Load(root, "..\\" + escapeName));
            Assert.Throws<ArgumentException>(() => SwarmActivityFold.Remember(root, "not-a-guid", true));
            Assert.Throws<ArgumentException>(() => SwarmActivityFold.Remember(root, "", true));
            Assert.False(File.Exists(outside));
            Assert.False(Directory.Exists(Path.Combine(root, "swarm-activity")));
        }
        finally { Delete(root); }
    }

    private static string Temp()
    {
        var path = Path.Combine(Path.GetTempPath(), "xai-desk-activity-fold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Delete(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }
}
