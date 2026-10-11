using Sovereign.Desktop.Core;
using Xunit;

namespace XaiDesk.Tests;

public sealed class IdentityTests
{
    [Fact]
    public void Names_and_credential_resources_are_xai_desk()
    {
        Assert.Equal("xAI Desk", XaiDeskIdentity.AppName);
        Assert.Equal("xAIDesk", XaiDeskIdentity.ExeName);
        Assert.Equal("xAI.Desk", XaiDeskIdentity.AppUserModelId);
        Assert.Equal("xAIDesk", XaiDeskIdentity.DataFolderName);
        Assert.Equal("xAIDesk", XaiDeskIdentity.CredentialPrefix);
        Assert.Equal("xAIDesk.XaiApi", XaiDeskIdentity.XaiApiCredentialResource);
        Assert.False(XaiDeskIdentity.UpdaterEnabled);
    }

    [Fact]
    public void Data_directory_is_xai_desk_and_not_sovereign()
    {
        var directory = XaiDeskIdentity.DataDirectory();
        Assert.EndsWith("xAIDesk", directory);
        Assert.DoesNotContain("SovereignAI", directory);
    }

    [Fact]
    public void Phone_port_is_8789()
    {
        Assert.Equal(8789, XaiDeskIdentity.PhonePort);
    }

    [Fact]
    public void Navigation_is_chat_room_and_settings()
    {
        Assert.Equal(["chat", "room", "grokbot", "settings"], XaiDeskIdentity.Navigation);
        Assert.Equal("Grokbot", XaiDeskIdentity.NavigationLabel("grokbot"));
        foreach (var excluded in new[] { "home", "charting", "research", "tools", "lab", "later" })
            Assert.DoesNotContain(excluded, XaiDeskIdentity.Navigation);
    }

    [Fact]
    public void Single_instance_mutex_is_xai_desk()
    {
        Assert.Contains("xAIDesk", XaiDeskIdentity.SingleInstanceMutex);
        Assert.DoesNotContain("SovereignAI", XaiDeskIdentity.SingleInstanceMutex);
    }

    [Fact]
    public void Grok_model_ids_are_recognized()
    {
        Assert.True(XaiDeskIdentity.IsGrokModel("grok-4.7"));
        Assert.False(XaiDeskIdentity.IsGrokModel("gpt-4o"));
        Assert.False(XaiDeskIdentity.IsGrokModel("gemini-2"));
    }
}
