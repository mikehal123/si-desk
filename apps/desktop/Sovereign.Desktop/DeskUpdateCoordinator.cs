using Microsoft.UI.Xaml.Controls;
using Sovereign.Desktop.Updates;

namespace Sovereign.Desktop;

public sealed partial class MainWindow
{
    private bool _updateStartupFailed;
    private IDisposable? _deskUpdatePause;

    private bool DeskWorkBlocksUpdate => false;

    private async Task WithUpdateBarrierAsync(string message, Func<Task> action)
    {
        await _chatDialogs.WaitAsync(_updateLifetime.Token);
        var canClose = false;
        var dialog = new ContentDialog { XamlRoot = Content.XamlRoot, Title = "Preparing the app update",
            Content = new StackPanel { Spacing = DeskTheme.Space12, Children = { new ProgressRing { IsActive = true }, Note(message) } } };
        dialog.Closing += (_, args) => { if (!canClose) args.Cancel = true; };
        try
        {
            var showing = dialog.ShowAsync();
            try { await action(); }
            finally { canClose = true; dialog.Hide(); await showing; }
        }
        finally { _chatDialogs.Release(); }
    }

    private Task<DeskUpdateSnapshot> PrepareDeskUpdateAsync(string target)
    {
        _ = target;
        throw new IOException("This build does not prepare a saved-work backup.");
    }
}
