using Sovereign.Desktop.Core;
using Xunit;

namespace XaiDesk.Tests;

public sealed class ProfileMigrationTests
{
    [Fact]
    public void Copies_the_old_profile_when_the_new_folder_is_missing()
    {
        var root = Path.Combine(Path.GetTempPath(), "si-desk-migrate-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "xAIDesk");
        var destination = Path.Combine(root, "SIDesk");
        Directory.CreateDirectory(Path.Combine(source, "chats"));
        File.WriteAllText(Path.Combine(source, "chats", "one.json"), "kept");
        File.WriteAllText(Path.Combine(source, "settings.json"), "yes");
        try
        {
            XaiDeskIdentity.CopyProfileIfMissing(source, destination);
            Assert.Equal("kept", File.ReadAllText(Path.Combine(destination, "chats", "one.json")));
            Assert.Equal("yes", File.ReadAllText(Path.Combine(destination, "settings.json")));
            Assert.Equal("kept", File.ReadAllText(Path.Combine(source, "chats", "one.json")));
            Assert.True(Directory.Exists(source));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Leaves_both_profiles_alone_when_the_new_folder_already_exists()
    {
        var root = Path.Combine(Path.GetTempPath(), "si-desk-migrate-both-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "xAIDesk");
        var destination = Path.Combine(root, "SIDesk");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(source, "old.txt"), "old");
        File.WriteAllText(Path.Combine(source, "extra.txt"), "from-old");
        File.WriteAllText(Path.Combine(destination, "old.txt"), "new");
        try
        {
            XaiDeskIdentity.CopyProfileIfMissing(source, destination);
            Assert.Equal("new", File.ReadAllText(Path.Combine(destination, "old.txt")));
            Assert.False(File.Exists(Path.Combine(destination, "extra.txt")));
            Assert.Equal("old", File.ReadAllText(Path.Combine(source, "old.txt")));
            Assert.True(File.Exists(Path.Combine(source, "extra.txt")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
