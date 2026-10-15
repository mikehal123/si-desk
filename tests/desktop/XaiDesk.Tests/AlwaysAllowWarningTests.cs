using Sovereign.Desktop.Client;
using Xunit;

namespace XaiDesk.Tests;

public sealed class AlwaysAllowWarningTests
{
    private const string Warning =
        "Always allow lets the agent run commands and edit files anywhere your Windows account can reach, not just this project folder, without asking. Only use it on projects you trust.";

    [Fact]
    public void Message_is_the_confirmation_copy() =>
        Assert.Equal(Warning, AlwaysAllowWarningPreference.Message);

    [Fact]
    public void Missing_or_unreadable_settings_still_warn()
    {
        var profile = NewProfile();
        try
        {
            Assert.False(File.Exists(Path.Combine(profile, AlwaysAllowWarningPreference.FileName)));
            Assert.False(AlwaysAllowWarningPreference.IsDismissed(profile));
            Assert.False(AlwaysAllowWarningPreference.IsDismissed(Path.Combine(profile, "missing")));

            File.WriteAllText(Path.Combine(profile, AlwaysAllowWarningPreference.FileName), "{");
            Assert.False(AlwaysAllowWarningPreference.IsDismissed(profile));
            File.WriteAllText(Path.Combine(profile, AlwaysAllowWarningPreference.FileName), "[]");
            Assert.False(AlwaysAllowWarningPreference.IsDismissed(profile));
            File.WriteAllText(Path.Combine(profile, AlwaysAllowWarningPreference.FileName), "{}");
            Assert.False(AlwaysAllowWarningPreference.IsDismissed(profile));
            File.WriteAllText(Path.Combine(profile, AlwaysAllowWarningPreference.FileName), "{\"dismissed\":\"yes\"}");
            Assert.False(AlwaysAllowWarningPreference.IsDismissed(profile));
        }
        finally
        {
            Directory.Delete(profile, true);
        }
    }

    [Fact]
    public void Dismissed_flag_round_trips_without_changing_permission_defaults()
    {
        var profile = NewProfile();
        try
        {
            AlwaysAllowWarningPreference.SaveDismissed(profile, true);
            var path = Path.Combine(profile, AlwaysAllowWarningPreference.FileName);
            Assert.Equal("{\"dismissed\":true}", File.ReadAllText(path));
            Assert.True(AlwaysAllowWarningPreference.IsDismissed(profile));
            Assert.Empty(Directory.GetFiles(profile, "*.tmp"));

            File.WriteAllText(path, "{\"Dismissed\":true}");
            Assert.True(AlwaysAllowWarningPreference.IsDismissed(profile));

            AlwaysAllowWarningPreference.SaveDismissed(profile, false);
            Assert.Equal("{\"dismissed\":false}", File.ReadAllText(path));
            Assert.False(AlwaysAllowWarningPreference.IsDismissed(profile));

            var defaults = new CursorProjectOptions();
            Assert.Equal(CursorProjectMode.Agent, defaults.Mode);
            Assert.Equal(CursorPermissionPolicy.AlwaysAsk, defaults.Permissions);
        }
        finally
        {
            Directory.Delete(profile, true);
        }
    }

    [Fact]
    public void Save_creates_a_missing_profile_directory()
    {
        var root = NewProfile();
        var profile = Path.Combine(root, "nested");
        Directory.Delete(root);
        try
        {
            Assert.False(Directory.Exists(profile));
            AlwaysAllowWarningPreference.SaveDismissed(profile, true);
            Assert.True(AlwaysAllowWarningPreference.IsDismissed(profile));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(CursorProjectMode.Agent, CursorPermissionPolicy.AlwaysAsk, CursorProjectMode.Agent, CursorPermissionPolicy.AlwaysAllow, false, true)]
    [InlineData(CursorProjectMode.Plan, CursorPermissionPolicy.AlwaysAsk, CursorProjectMode.Agent, CursorPermissionPolicy.AlwaysAllow, false, true)]
    [InlineData(CursorProjectMode.Plan, CursorPermissionPolicy.AlwaysAllow, CursorProjectMode.Agent, CursorPermissionPolicy.AlwaysAllow, false, true)]
    [InlineData(CursorProjectMode.Agent, CursorPermissionPolicy.AlwaysAllow, CursorProjectMode.Agent, CursorPermissionPolicy.AlwaysAllow, false, false)]
    [InlineData(CursorProjectMode.Agent, CursorPermissionPolicy.AlwaysAsk, CursorProjectMode.Agent, CursorPermissionPolicy.AlwaysAllow, true, false)]
    [InlineData(CursorProjectMode.Plan, CursorPermissionPolicy.AlwaysAsk, CursorProjectMode.Agent, CursorPermissionPolicy.AlwaysAllow, true, false)]
    [InlineData(CursorProjectMode.Agent, CursorPermissionPolicy.AlwaysAllow, CursorProjectMode.Agent, CursorPermissionPolicy.AlwaysAsk, false, false)]
    [InlineData(CursorProjectMode.Agent, CursorPermissionPolicy.AlwaysAllow, CursorProjectMode.Plan, CursorPermissionPolicy.AlwaysAsk, false, false)]
    [InlineData(CursorProjectMode.Plan, CursorPermissionPolicy.AlwaysAsk, CursorProjectMode.Agent, CursorPermissionPolicy.AlwaysAsk, false, false)]
    [InlineData(CursorProjectMode.Agent, CursorPermissionPolicy.AlwaysAsk, CursorProjectMode.Plan, CursorPermissionPolicy.AlwaysAsk, false, false)]
    public void ShouldWarn_only_when_turning_always_allow_on(
        CursorProjectMode currentMode,
        CursorPermissionPolicy currentPermissions,
        CursorProjectMode nextMode,
        CursorPermissionPolicy nextPermissions,
        bool dismissed,
        bool warn) =>
        Assert.Equal(warn, AlwaysAllowWarningPreference.ShouldWarn(currentMode, currentPermissions, nextMode, nextPermissions, dismissed));

    private static string NewProfile()
    {
        var profile = Path.Combine(Path.GetTempPath(), "si-desk-always-allow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        return profile;
    }
}
