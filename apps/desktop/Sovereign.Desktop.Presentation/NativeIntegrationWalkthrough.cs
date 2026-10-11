namespace Sovereign.Desktop.Presentation;

public static class NativeIntegrationWalkthrough
{
    public static Task<WalkthroughReceipt> RunAsync(ShellViewModel vm, CancellationToken cancellationToken = default)
    {
        _ = vm;
        _ = cancellationToken;
        var step = new WalkthroughStep("engine-host", "Offline engine host", false, "The engine host is not part of this build.");
        return Task.FromResult(new WalkthroughReceipt("public-beta", [step], false, step.Detail));
    }
}
