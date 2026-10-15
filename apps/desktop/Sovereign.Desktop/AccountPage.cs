using Sovereign.Desktop.Client;

namespace Sovereign.Desktop;

public sealed partial class MainWindow
{
    private bool _accountInitialized;

    private Task InitializeAccountAsync()
    {
        if (_accountInitialized) return Task.CompletedTask;
        _accountInitialized = true;
        // Public builds do not sign in. Missing or leftover account-service.json must not throw or open a dialog.
        _ = AccountSignInPolicy.Inspect(AppContext.BaseDirectory);
        return Task.CompletedTask;
    }
}
