using Sovereign.Desktop.Client;
using Xunit;

namespace XaiDesk.Tests;

public sealed class GrokbotComposerKeyTests
{
    [Fact]
    public void Grokbot_enter_sends_and_shift_enter_is_a_newline()
    {
        Assert.Equal(GrokTuiKeyAction.Send, GrokTuiPresentation.Key("Enter", false, false));
        Assert.Equal(GrokTuiKeyAction.NewLine, GrokTuiPresentation.Key("Enter", true, false));
        Assert.NotEqual(GrokTuiKeyAction.Send, GrokTuiPresentation.Key("Escape", false, false));
    }
}
