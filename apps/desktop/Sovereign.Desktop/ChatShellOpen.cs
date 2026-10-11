using Microsoft.UI.Dispatching;

namespace Sovereign.Desktop;

internal sealed partial class ChatConversation
{
    private readonly Queue<string> _shellOpenQueue = new();
    private DispatcherQueueTimer? _shellOpenTimer;

    internal void EnqueueShellFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || _disposed) return;
        void Start()
        {
            if (_disposed) return;
            _shellOpenQueue.Enqueue(path);
            if (_shellOpenTimer is null)
            {
                _shellOpenTimer = _owner.DispatcherQueue.CreateTimer();
                _shellOpenTimer.Interval = TimeSpan.FromMilliseconds(200);
                _shellOpenTimer.IsRepeating = true;
                _shellOpenTimer.Tick += DrainShellFiles;
            }
            _shellOpenTimer.Start();
            DrainShellFiles(_shellOpenTimer, this);
        }

        if (_owner.DispatcherQueue.HasThreadAccess) Start();
        else _owner.DispatcherQueue.TryEnqueue(Start);
    }

    private void DrainShellFiles(DispatcherQueueTimer sender, object args)
    {
        if (_disposed || _shellOpenQueue.Count == 0)
        {
            _shellOpenTimer?.Stop();
            return;
        }

        if (!CursorComposerAvailable || _attachmentBusy) return;
        var present = new List<string>();
        var missing = new List<string>();
        while (_shellOpenQueue.Count > 0 && present.Count < 10)
        {
            var path = _shellOpenQueue.Dequeue();
            if (File.Exists(path)) present.Add(path);
            else missing.Add(Path.GetFileName(path));
        }

        if (missing.Count > 0)
            _attachmentStatus.Text = "Could not open " + string.Join(", ", missing) + ".";
        if (present.Count > 0)
            _ = ImportPathsAsync(present);
        if (_shellOpenQueue.Count == 0)
            _shellOpenTimer?.Stop();
    }
}
