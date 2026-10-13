using Sovereign.Desktop.Client.Host;
using Sovereign.Desktop.Core;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class HostProjectionTests
{
    [Fact]
    public void Account_without_type_is_unknown_not_coerced()
    {
        var result = HostJson.Obj(("cash_cents", HostJson.IntValue(91100)), ("run_id", HostJson.Str("r1")));
        var account = HostProjection.MapAccount(result);
        Assert.True(account.IsUnknown);
        Assert.Contains("type discriminator missing", account.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_result_type_is_not_a_healthy_account()
    {
        var result = HostJson.Obj(
            ("type", HostJson.Str("mystery")),
            ("run_id", HostJson.Str("r1")),
            ("cash_cents", HostJson.IntValue(1)),
            ("ending_equity_cents", HostJson.IntValue(101000)));
        var account = HostProjection.MapAccount(result);
        Assert.True(account.IsUnknown);
        Assert.Contains("unsupported result type mystery", account.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Account_equity_is_typed_integer_cents()
    {
        var result = HostJson.Obj(
            ("type", HostJson.Str("account")),
            ("run_id", HostJson.Str("run-three-cycle-1")),
            ("cash_cents", HostJson.IntValue(91100)),
            ("positions", HostJson.Obj(("AAA", HostJson.IntValue(11)))),
            ("reserved_cash_cents", HostJson.IntValue(0)),
            ("ending_equity_cents", HostJson.IntValue(101000)),
            ("trading_authority", HostJson.Str("NONE")));
        var account = HostProjection.MapAccount(result);
        Assert.True(account.IsKnown);
        Assert.Equal(101000, account.Value!.EndingEquity.Value!.MinorUnits);
        Assert.Equal(91100, account.Value.Cash.Value!.MinorUnits);
        Assert.Equal("NONE", account.Value.TradingAuthority);
        Assert.DoesNotContain("101000", account.Value.SourceLabel, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluation_decimal_strings_are_not_parsed_as_binary_floats()
    {
        var result = HostJson.Obj(
            ("type", HostJson.Str("evaluation")),
            ("run_id", HostJson.Str("r1")),
            ("value", HostJson.Obj(
                ("ratio", HostJson.Str("0.1")),
                ("actual_account", HostJson.Obj(("cash", HostJson.IntValue(91100)))))));
        var ev = HostProjection.MapEvaluation(result);
        Assert.Contains("0.1", ev.PlotAbsenceReason, StringComparison.Ordinal);
        Assert.Contains("decimal strings", ev.PlotAbsenceReason, StringComparison.Ordinal);
        Assert.Contains("integer cents=91100", ev.PerformanceBasis, StringComparison.Ordinal);
    }

    [Fact]
    public void Schema_health_is_not_disconnected_and_not_ready()
    {
        var health = HostProjection.MapHealth(HostJson.Obj(("type", HostJson.Str("account"))));
        Assert.Equal(EngineState.Unknown, health.Engine.Value);
        Assert.NotEqual(ConnectivityState.Disconnected, health.Provider.Value);
        Assert.Contains("unsupported result type", health.Engine.Reason, StringComparison.Ordinal);
    }
}

public sealed class EventInboxResyncTests
{
    [Fact]
    public void ResetForResync_clears_cursor_then_new_stream_is_not_mixed()
    {
        var inbox = new Sovereign.Desktop.Client.Adapters.EventInbox();
        inbox.Accept(new ClientEvent("e1", "1", DateTimeOffset.UtcNow, "a", "old", null, null, false));
        inbox.ResetForResync();
        Assert.Null(inbox.LastCursor);
        Assert.Empty(inbox.Snapshot());
        inbox.Accept(new ClientEvent("e2", "2", DateTimeOffset.UtcNow, "a", "new", null, null, false));
        Assert.Equal("2", inbox.LastCursor);
        Assert.Single(inbox.Snapshot());
        Assert.Equal("e2", inbox.Snapshot()[0].Id);
    }
}
