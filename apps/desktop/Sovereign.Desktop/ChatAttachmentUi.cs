using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Sovereign.Desktop.Client;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using static Sovereign.Desktop.MainWindow;

namespace Sovereign.Desktop;

internal sealed partial class ChatConversation
{
    private readonly List<ChatAttachment> _draftFiles = [];
    private readonly StackPanel _attachmentChips = new() { Spacing = DeskTheme.Space4 };
    private readonly TextBlock _attachmentStatus = Note("");
    private readonly CancellationTokenSource _attachmentLifetime = new();
    private bool _attachmentBusy;
    private Button? _attachFiles;
    private ChatAttachmentStore Attachments => new(_profileDirectory, Id);

    private UIElement BuildAttachmentButton()
    {
        var menu = new MenuFlyout();
        var files = new MenuFlyoutItem { Text = "Attach files…" };
        files.Click += async (_, _) => await PickAttachmentsAsync(); menu.Items.Add(files);
        var paste = new MenuFlyoutItem { Text = "Paste screenshot or files" };
        paste.Click += async (_, _) =>
        {
            DataPackageView? data = null;
            try { data = await ChatClipboard.ReadAsync(_chatInput.DispatcherQueue); if (!_disposed && data is not null) await ImportDataAsync(data); }
            catch { if (!_disposed) _attachmentStatus.Text = "Clipboard unavailable. Try Attach files."; }
            finally { data = null; }
        }; menu.Items.Add(paste);
        _attachFiles = new Button { Content = "+", Flyout = menu, Width = 34, Height = 32, CornerRadius = new CornerRadius(DeskTheme.Radius10) };
        AutomationName(_attachFiles, "Add attachments"); ToolTipService.SetToolTip(_attachFiles, "Attach files or paste a screenshot");
        _chatInput.Paste += async (_, e) => {
            // Claim the event before returning to XAML; clipboard access must be
            // deferred, including the format check for an ordinary text paste.
            e.Handled = true;
            await PasteIntoComposerAsync();
        };
        _chatPage.AllowDrop = true;
        _chatPage.DragOver += (_, e) => { if (CursorComposerAvailable && !_attachmentBusy && e.DataView.Contains(StandardDataFormats.StorageItems)) { e.AcceptedOperation = DataPackageOperation.Copy; e.Handled = true; } };
        _chatPage.Drop += async (_, e) => { var deferral = e.GetDeferral(); try { e.Handled = true; await ImportDataAsync(e.DataView); } finally { deferral.Complete(); } };
        return _attachFiles;
    }
    private async Task PasteIntoComposerAsync()
    {
        var originalText = _chatInput.Text;
        var start = _chatInput.SelectionStart;
        var length = _chatInput.SelectionLength;
        bool Unchanged() => !_disposed && _chatInput.XamlRoot is not null && !_chatInput.IsReadOnly
            && _chatInput.Text == originalText && _chatInput.SelectionStart == start && _chatInput.SelectionLength == length;
        DataPackageView? data = null;
        try
        {
            data = await ChatClipboard.ReadAsync(_chatInput.DispatcherQueue);
            if (!Unchanged()) return;
            if (data.Contains(StandardDataFormats.Bitmap) || data.Contains(StandardDataFormats.StorageItems))
                await ImportDataAsync(data);
            else if (data.Contains(StandardDataFormats.Text))
            {
                var text = await data.GetTextAsync();
                // A slow clipboard owner must not overwrite edits made meanwhile.
                if (!Unchanged()) return;
                _chatInput.SelectedText = text;
                var insertedLength = _chatInput.Text.Length - (originalText.Length - length);
                _chatInput.Select(start + insertedLength, 0);
            }
        }
        catch { if (!_disposed) _attachmentStatus.Text = "Clipboard unavailable. Please try pasting again."; }
        finally { data = null; }
    }
    private async Task PickAttachmentsAsync()
    {
        if (!CursorComposerAvailable || _attachmentBusy) return;
        try
        {
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_owner));
            var files = await picker.PickMultipleFilesAsync();
            await ImportPathsAsync(files.Select(f => f.Path));
        }
        catch (Exception ex) { _attachmentStatus.Text = "Attachment unavailable: " + ex.Message; }
    }
    private async Task ImportDataAsync(DataPackageView data)
    {
        if (!CursorComposerAvailable || _attachmentBusy) return;
        try
        {
            if (data.Contains(StandardDataFormats.StorageItems))
            {
                var items = await data.GetStorageItemsAsync();
                await ImportPathsAsync(items.OfType<StorageFile>().Select(f => f.Path));
                if (items.Any(i => i is StorageFolder)) _attachmentStatus.Text += " Folders are not imported; select files instead.";
            }
            else if (data.Contains(StandardDataFormats.Bitmap))
            {
                if (_draftFiles.Count >= 10) throw new IOException("Maximum 10 attachments per draft.");
                SetAttachmentBusy(true); _attachmentStatus.Text = "Copying screenshot…";
                using var source = await (await data.GetBitmapAsync()).OpenReadAsync();
                var png = await EncodePngAsync(source);
                using var stream = new MemoryStream(png);
                var item = await Attachments.ImportAsync(stream, "Screenshot-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png", _attachmentLifetime.Token);
                _draftFiles.Add(item); Save(); RenderDraftFiles();
            }
            else _attachmentStatus.Text = "Copy an image or files first. Text can be pasted directly into the message.";
        }
        catch (Exception ex) { _attachmentStatus.Text = "Attachment unavailable: " + ex.Message; }
        finally { SetAttachmentBusy(false); if (_attachmentStatus.Text.StartsWith("Copying ")) RefreshAttachmentStatus(); }
    }
    private async Task ImportPathsAsync(IEnumerable<string> paths)
    {
        if (!CursorComposerAvailable || _attachmentBusy) return;
        SetAttachmentBusy(true);
        try
        {
            foreach (var path in paths)
            {
                if (_draftFiles.Count >= 10) { _attachmentStatus.Text = "Maximum 10 attachments per draft. Remaining files were not imported."; break; }
                _attachmentStatus.Text = "Copying " + Path.GetFileName(path) + "…";
                var item = await Task.Run(() => Attachments.ImportAsync(path, _attachmentLifetime.Token));
                if (_disposed) return;
                _draftFiles.Add(item); Save(); RenderDraftFiles();
            }
        }
        catch (Exception ex) { _attachmentStatus.Text = "Attachment unavailable: " + ex.Message; }
        finally { SetAttachmentBusy(false); if (_attachmentStatus.Text.StartsWith("Copying ")) RefreshAttachmentStatus(); }
    }
    private void SetAttachmentBusy(bool busy)
    {
        _attachmentBusy = busy;
        if (_attachFiles is not null) _attachFiles.IsEnabled = !busy && CursorComposerAvailable;
        if (_chatSend is not null) _chatSend.IsEnabled = !busy && !_acceptingCursorInstruction && CursorComposerAvailable && (_chatReady || _reconnectOnSend);
        RefreshComposerAction();
        foreach (var row in _attachmentChips.Children.OfType<StackPanel>())
            foreach (var button in row.Children.OfType<Button>()) button.IsEnabled = !busy && CursorComposerAvailable;
    }
    private void RenderDraftFiles()
    {
        _attachmentChips.Children.Clear();
        foreach (var item in _draftFiles)
        {
            var preview = Button((item.Kind == "text" ? "Readable · " : item.Kind == "image" ? "Image · " : "Preview only · ") + item.Name + " · " + (item.Bytes / 1024d).ToString("0.#") + " KB", () => PreviewAttachmentAsync(item));
            var remove = Button("Remove " + item.Name, () => {
                if (!CursorComposerAvailable || _attachmentBusy) return;
                _draftFiles.Remove(item); Save(); RenderDraftFiles();
                // Retain bytes until conversation deletion: avoids losing references on a failed save or retry.
            });
            remove.Content = "×"; ToolTipService.SetToolTip(remove, "Remove from this draft");
            ToolTipService.SetToolTip(preview, item.Note);
            _attachmentChips.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = DeskTheme.Space4, Children = { preview, remove } });
        }
        RefreshAttachmentStatus();
    }
    private void RefreshAttachmentStatus()
    {
        RefreshComposerAction();
        if (_attachmentBusy) return;
        var agentImages = CursorChat ? _cursorChat?.SupportsImages == true : GrokBuildChat && _grokBuild?.SupportsImages == true;
        _attachmentStatus.Text = ChatAttachmentReadiness.Describe(_draftFiles.Select(f => f.Kind),
            _chatReady, SelectedChatModel, PlanChat, CursorChat || GrokBuildChat, agentImages, LocalChat);
    }
    private UIElement AttachmentHistory(ChatAttachment[] items)
    {
        var panel = new StackPanel { Spacing = DeskTheme.Space4 };
        foreach (var item in items) panel.Children.Add(Button("Attachment · " + item.Name, () => PreviewAttachmentAsync(item)));
        if (items.Any(item => item.Kind == "image")) panel.Children.Add(Note("Saved image · open it and choose Use in next message to send it again."));
        return panel;
    }
    private async Task PreviewAttachmentAsync(ChatAttachment item)
    {
        await _dialogs.WaitAsync();
        Windows.Media.Playback.MediaPlayer? player = null;
        IRandomAccessStream? mediaStream = null;
        try
        {
            var data = await Attachments.ReadVerifiedAsync(item, _attachmentLifetime.Token);
            var panel = new StackPanel { Spacing = DeskTheme.Space8, MaxWidth = 460 };
            panel.Children.Add(Note(item.Note));
            if (item.Kind == "image")
            {
                using var memory = await ImageStreamAsync(data);
                var png = await EncodePngAsync(memory); using var view = await ImageStreamAsync(png);
                var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(view);
                panel.Children.Add(new Image { Source = bitmap, MaxHeight = 360, Stretch = Stretch.Uniform });
            }
            else if (item.Kind is "video" or "audio")
            {
                player = new Windows.Media.Playback.MediaPlayer { AutoPlay = false };
                mediaStream = await (await StorageFile.GetFileFromPathAsync(Attachments.PathFor(item))).OpenAsync(FileAccessMode.Read);
                player.Source = Windows.Media.Core.MediaSource.CreateFromStream(mediaStream, item.Mime);
                var media = new MediaPlayerElement { AreTransportControlsEnabled = true, Height = 300 }; media.SetMediaPlayer(player);
                panel.Children.Add(media); panel.Children.Add(Note("Playback depends on installed Windows codecs. No transcription or video analysis is sent."));
            }
            else panel.Children.Add(new ScrollViewer { Height = 250, Content = new TextBlock { Text = item.ExtractedText ?? "This file is retained locally. This format has no reader connected yet.", IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap } });
            panel.Children.Add(Note("SHA-256 · " + item.Sha256));
            var result = await new ContentDialog { XamlRoot = ((FrameworkElement)_owner.Content).XamlRoot, Title = item.Name, Content = new ScrollViewer { Content = panel, MaxHeight = 500 }, CloseButtonText = "Close", PrimaryButtonText = "Use in next message", IsPrimaryButtonEnabled = CursorComposerAvailable && !_attachmentBusy && _draftFiles.Count < 10 && !_draftFiles.Any(f => f.Id == item.Id) }.ShowAsync();
            if (result == ContentDialogResult.Primary && CursorComposerAvailable && !_attachmentBusy && !_draftFiles.Any(f => f.Id == item.Id)) { _draftFiles.Add(item); Save(); RenderDraftFiles(); }
        }
        catch (Exception ex) { _attachmentStatus.Text = "Preview unavailable: " + ex.GetType().Name + " (" + ex.HResult.ToString("X8") + "). " + ex.Message; }
        finally { player?.Dispose(); mediaStream?.Dispose(); _dialogs.Release(); }
    }
    private static async Task<InMemoryRandomAccessStream> ImageStreamAsync(byte[] data)
    {
        var stream = new InMemoryRandomAccessStream();
        using var writer = new DataWriter(stream);
        writer.WriteBytes(data); await writer.StoreAsync(); await writer.FlushAsync(); writer.DetachStream();
        stream.Seek(0); return stream;
    }
    private static async Task<byte[]> EncodePngAsync(IRandomAccessStream source)
    {
        var decoder = await BitmapDecoder.CreateAsync(source);
        if ((long)decoder.PixelWidth * decoder.PixelHeight > 24000000) throw new IOException("Image exceeds 24 megapixels. Resize it before attaching.");
        var scale = Math.Min(1d, 1600d / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var width = Math.Max(1u, (uint)(decoder.PixelWidth * scale)); var height = Math.Max(1u, (uint)(decoder.PixelHeight * scale));
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform { ScaledWidth = width, ScaledHeight = height }, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb);
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, width, height, 96, 96, pixels.DetachPixelData()); await encoder.FlushAsync(); output.Seek(0);
        using var result = new MemoryStream(); await output.AsStreamForRead().CopyToAsync(result); return result.ToArray();
    }
    private async Task<(string Text, List<ChatImage> Images)> PrepareAttachmentsAsync(string prompt, ChatAttachment[] items, CancellationToken? cancellation = null)
    {
        var token = cancellation ?? _attachmentLifetime.Token;
        for (var i = 0; i < items.Length; i++) items[i] = await Attachments.RefreshAsync(items[i], token);
        var unsupported = items.Where(f => f.Kind is not ("text" or "image")).ToArray();
        if (unsupported.Length > 0) throw new IOException("Cannot send: " + string.Join("; ", unsupported.Select(f => f.Name + " — " + f.Note)) + " Remove those files or attach a readable version.");
        var agentImages = CursorChat ? _cursorChat?.SupportsImages == true : !GrokBuildChat || _grokBuild?.SupportsImages == true;
        if (items.Any(f => f.Kind == "image") && (PlanChat || ((CursorChat || GrokBuildChat) && !agentImages)))
            throw new IOException("This connection has no verified image-input support. Remove the images or choose a connection that advertises them.");
        var images = new List<ChatImage>();
        foreach (var item in items)
        {
            var bytes = await Attachments.ReadVerifiedAsync(item, token);
            if (item.Kind != "image") continue;
            if (bytes.Length > 20 * 1024 * 1024) throw new IOException("Image exceeds 20 MB; resize before sending.");
            using var memory = await ImageStreamAsync(bytes);
            images.Add(new("image/png", Convert.ToBase64String(await EncodePngAsync(memory))));
        }
        return (ChatAttachmentStore.WithContext(prompt, items), images);
    }
}
