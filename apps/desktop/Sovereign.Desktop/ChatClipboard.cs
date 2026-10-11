using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;

namespace Sovereign.Desktop;

internal static class ChatClipboard
{
    // Clipboard/OLE can pump window messages. Never enter it from a XAML Paste
    // or Click event stack, even on the UI thread (microsoft-ui-xaml#6754).
    internal static Task<T> AfterInputAsync<T>(DispatcherQueue dispatcher, Func<Task<T>> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(async () =>
        {
            try { completion.TrySetResult(await action()); }
            catch (Exception ex) { completion.TrySetException(ex); }
        })) completion.TrySetException(new InvalidOperationException("The chat window is closing."));
        return completion.Task;
    }

    // DataPackageView is a runtime-managed, possibly shared WinRT projection.
    // It is not IDisposable. Releasing its private COM reference poisons cached
    // projections returned by a later GetContent (or another active reader).
    internal static Task<DataPackageView> ReadAsync(DispatcherQueue dispatcher) =>
        AfterInputAsync(dispatcher, () => Task.FromResult(Clipboard.GetContent()));

    internal static Task<bool> CopyAsync(DispatcherQueue dispatcher, string text) =>
        AfterInputAsync(dispatcher, async () =>
        {
            // A DataPackage can be placed on the clipboard only once, so each attempt needs its own.
            for (var attempt = 0; ; attempt++)
            {
                var data = new DataPackage();
                data.SetText(text);
                try
                {
                    Clipboard.SetContent(data);
                    try { Clipboard.Flush(); }
                    catch (System.Runtime.InteropServices.COMException flush) when (flush.HResult == unchecked((int)0x800401D0))
                    {
                        if (await Clipboard.GetContent().GetTextAsync() != text) throw;
                    }
                    return true;
                }
                catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == unchecked((int)0x800401D0) && attempt < 4)
                { await Task.Delay(80); } // Another owner still has the clipboard open.
            }
        });

}
