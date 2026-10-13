using Sovereign.Desktop.G3Harness;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class G3InterruptTests
{
    [Fact]
    public async Task G3_interrupted_dispatched_start_stays_unknown_until_operations_get()
    {
        var receipt = await G3InterruptProbe.RunAsync();
        Assert.True(receipt.Passed, receipt.Summary);
        Assert.Equal("Unknown", receipt.AfterDispatchStatus);
        Assert.Equal(1, receipt.StartCallsAtInterrupt);
        Assert.Null(receipt.RunIdAfterInterrupt);
        Assert.Contains("operations.get", receipt.ReconcileMethod, StringComparison.Ordinal);
        Assert.Equal("Completed", receipt.AfterReconcileStatus);
        Assert.Equal("run-g3-1", receipt.RunIdAfterReconcile);
        Assert.Equal("op-g3-1", receipt.OperationIdAfterReconcile);
        Assert.True(receipt.OperationsGetCalls >= 1);
        Assert.Equal(1, receipt.StartCallsAfterRetry);
        Assert.DoesNotContain("never-issued", receipt.ReconcileMethod, StringComparison.OrdinalIgnoreCase);
    }
}
