using Sovereign.Desktop.Core;
using Sovereign.Desktop.Phone;
using Xunit;

namespace XaiDesk.Tests;

public sealed class IdentityTests
{
    [Fact]
    public void Names_and_credential_resources_are_xai_desk()
    {
        Assert.Equal("SI Desk", XaiDeskIdentity.AppName);
        Assert.Equal("SIDesk", XaiDeskIdentity.ExeName);
        Assert.Equal("SIDesk", XaiDeskIdentity.AppUserModelId);
        Assert.Equal("SIDesk", XaiDeskIdentity.DataFolderName);
        Assert.Equal("xAIDesk", XaiDeskIdentity.LegacyDataFolderName);
        Assert.Equal("SIDesk", XaiDeskIdentity.CredentialPrefix);
        Assert.Equal("SIDesk.XaiApi", XaiDeskIdentity.XaiApiCredentialResource);
        Assert.Equal("xAIDesk.XaiApi", XaiDeskIdentity.LegacyXaiApiCredentialResource);
        Assert.Equal(["SIDesk.XaiApi", "xAIDesk.XaiApi"], XaiDeskIdentity.CredentialResourceNames(XaiDeskIdentity.XaiApiCredentialResource));
        Assert.False(XaiDeskIdentity.UpdaterEnabled);
    }

    [Fact]
    public void Data_directory_is_xai_desk_and_not_sovereign()
    {
        var directory = XaiDeskIdentity.DataDirectory();
        Assert.EndsWith("SIDesk", directory);
        Assert.DoesNotContain("Sovereign" + "AI", directory);
    }

    [Fact]
    public void Phone_port_is_8789()
    {
        Assert.Equal(8789, XaiDeskIdentity.PhonePort);
        Assert.Equal(8789, PhoneBindAddress.Port);
    }

    [Fact]
    public void Navigation_is_chat_room_and_settings()
    {
        Assert.Equal(["chat", "room", "grokbot", "settings"], XaiDeskIdentity.Navigation);
        Assert.Equal("Grok Bot", XaiDeskIdentity.NavigationLabel("grokbot"));
        foreach (var excluded in new[] { "home", "charting", "re" + "search", "tools", "lab", "later" })
            Assert.DoesNotContain(excluded, XaiDeskIdentity.Navigation);
    }

    [Fact]
    public void Single_instance_mutex_is_xai_desk()
    {
        Assert.Contains("SIDesk", XaiDeskIdentity.SingleInstanceMutex);
        Assert.DoesNotContain("Sovereign" + "AI", XaiDeskIdentity.SingleInstanceMutex);
    }

    [Fact]
    public void Grok_model_ids_are_recognized()
    {
        Assert.True(XaiDeskIdentity.IsGrokModel("grok-4.7"));
        Assert.False(XaiDeskIdentity.IsGrokModel("gpt-4o"));
        Assert.False(XaiDeskIdentity.IsGrokModel("gemini-2"));
    }
}
