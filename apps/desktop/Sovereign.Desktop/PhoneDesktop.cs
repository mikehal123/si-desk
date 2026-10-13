using Sovereign.Desktop.Phone;

namespace Sovereign.Desktop;

public sealed partial class MainWindow
{
    private sealed class DesktopPhoneBridge(MainWindow window) : IPhoneChatBridge
    {
        public Task<IReadOnlyList<PhoneChatSummary>> ListAsync(CancellationToken ct) =>
            window.PhoneOnWindow(() => (IReadOnlyList<PhoneChatSummary>)window._conversations.Select(chat =>
                new PhoneChatSummary(chat.Id, chat.Title, chat.PhoneProviderLabel, chat.PhoneModel, chat.IsWorking, chat.Updated, chat.ProjectId)).ToArray(), ct);

        public Task<PhoneChatDetail?> GetAsync(string id, CancellationToken ct) =>
            window.PhoneOnWindow(() => window._conversations.FirstOrDefault(chat => chat.Id == id)?.PhoneSnapshot(), ct);

        public Task<PhoneSendResult> SendAsync(string id, string text, CancellationToken ct) =>
            window.PhoneOnWindow(async () =>
            {
                var chat = window._conversations.FirstOrDefault(item => item.Id == id);
                if (chat is null) return new PhoneSendResult(false, "That chat is not open in the desktop app.");
                var error = await chat.SendFromPhoneAsync(text);
                return error is null ? new PhoneSendResult(true, null) : new PhoneSendResult(false, error);
            }, ct);

        public Task<bool> StopAsync(string id, CancellationToken ct) =>
            window.PhoneOnWindow(() => window._conversations.FirstOrDefault(chat => chat.Id == id)?.StopFromPhone() ?? false, ct);
    }

    private Task<T> PhoneOnWindow<T>(Func<T> work, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (ct.IsCancellationRequested) { completion.TrySetCanceled(ct); return completion.Task; }
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            if (ct.IsCancellationRequested) { completion.TrySetCanceled(ct); return; }
            try { completion.TrySetResult(work()); }
            catch (Exception ex) { completion.TrySetException(ex); }
        })) completion.TrySetException(new IOException("The desktop window is closed."));
        return completion.Task;
    }

    private Task<T> PhoneOnWindow<T>(Func<Task<T>> work, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (ct.IsCancellationRequested) { completion.TrySetCanceled(ct); return completion.Task; }
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            if (ct.IsCancellationRequested) { completion.TrySetCanceled(ct); return; }
            try { completion.TrySetResult(await work()); }
            catch (Exception ex) { completion.TrySetException(ex); }
        })) completion.TrySetException(new IOException("The desktop window is closed."));
        return completion.Task;
    }
}
