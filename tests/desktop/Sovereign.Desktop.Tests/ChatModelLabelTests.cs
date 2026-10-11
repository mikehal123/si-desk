using Sovereign.Desktop.Client;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class ChatModelLabelTests
{
    private const string HighFast256 = "grok-4.7[context=256k,reasoning_effort=high,fast=true]";
    private const string HighFast300 = "grok-4.7[context=300k,reasoning_effort=high,fast=true]";
    private const string HighOnly = "grok-4.7[context=256k,reasoning_effort=high]";
    private const string MojibakeName = "Grok 4.7 \u00C2\u00B7 grok-4.7[context=300k,effort=high,fast=true]";

    [Fact]
    public void High_fast_keeps_the_known_compact_label_and_original_id()
    {
        var model = new CursorChatModel(HighFast256, "grok-4.7");
        Assert.Equal("Grok 4.7 High Fast", model.ToString());
        Assert.Equal(HighFast256, model.Id);
        Assert.Contains("Provider ID: " + HighFast256, model.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("Â", model.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("[", model.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_names_stay_distinct_and_restore_by_id()
    {
        var choices = ChatModelLabel.Assign([
            (HighFast256, "grok-4.7"),
            (HighFast300, MojibakeName)
        ]);
        Assert.Equal(HighFast256, choices[0].Id);
        Assert.Equal(HighFast300, choices[1].Id);
        Assert.Equal("Grok 4.7 High Fast 256k", choices[0].Label);
        Assert.Equal("Grok 4.7 High Fast 300k", choices[1].Label);
        Assert.DoesNotContain("Â", choices[1].Label, StringComparison.Ordinal);
        Assert.Equal(choices[1], choices.Single(choice => choice.Id == HighFast300));
    }

    [Fact]
    public void Effort_and_thinking_are_short_labels_only_when_advertised()
    {
        var high = ChatModelLabel.Display(HighOnly, "Grok 4.7");
        var thinking = ChatModelLabel.Display("composer-2[thinking=true]", "composer-2");
        var unknown = ChatModelLabel.Assign([("nonesuch-model-v2[context=8k]", null)])[0];
        Assert.Equal("Grok 4.7 High", high);
        Assert.Equal("Composer 2 Thinking", thinking);
        Assert.Equal("Nonesuch Model V2", unknown.Label);
        Assert.Equal("nonesuch-model-v2[context=8k]", unknown.Id);
        Assert.DoesNotContain("High", unknown.Label, StringComparison.Ordinal);
        Assert.DoesNotContain("Fast", unknown.Label, StringComparison.Ordinal);
        Assert.Contains("context: 8k", unknown.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void Mojibake_separator_is_repaired_at_the_source()
    {
        Assert.Equal("Grok 4.7 " + ChatModelLabel.MiddleDot + " extra", ChatModelLabel.RepairText("Grok 4.7 \u00C2\u00B7 extra"));
        var labeled = ChatModelLabel.Display(HighFast300, MojibakeName);
        Assert.Equal("Grok 4.7 High Fast", labeled);
        Assert.DoesNotContain('\u00C2', labeled);
    }

    [Fact]
    public void Assign_never_changes_routing_values()
    {
        var id = "vendor/unusual.id[effort=medium,fast=false,foo=bar]";
        var choice = ChatModelLabel.Assign([(id, id)])[0];
        Assert.Equal(id, choice.Id);
        Assert.Equal("Vendor Unusual.id Medium", choice.Label);
        Assert.DoesNotContain("Fast", choice.Label, StringComparison.Ordinal);
        Assert.Contains("foo: bar", choice.Details, StringComparison.Ordinal);
    }
}
