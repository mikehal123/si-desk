using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Sovereign.Desktop.Client;
using static Sovereign.Desktop.MainWindow;

namespace Sovereign.Desktop;

internal sealed partial class ChatConversation
{
    private bool _lineReviewOn = true;
    private Border? _lineReviewLayer;
    private StackPanel? _lineReviewBody;
    private LineReviewDraft? _reviewDraft;
    private bool _reviewReadOnly;
    private bool _reviewSending;
    private string? _reviewFocus;
    private string? _selectedDocument;
    private int _selectedLine;
    private Button? _reviewSend;
    private Button? _reviewApprove;

    internal bool LineReviewOn => _lineReviewOn;

    internal void ApplyLineReviewSetting()
    {
        _lineReviewOn = LineReviewPreference.Load(_profileDirectory);
        foreach (var turn in _chatTurns) turn.RefreshLineReviewOffer();
        RenderChangeFiles();
        foreach (var panel in _openSubagentPanels.Values) panel.ApplyLineReview();
        if (!_lineReviewOn && _lineReviewLayer is not null) _lineReviewLayer.Visibility = Visibility.Collapsed;
    }

    internal void ApplyLineReviewChrome(bool classic)
    {
        if (_lineReviewLayer is null) return;
        ApplyReviewFont(_lineReviewLayer, classic ? DeskTheme.UiFont : DeskTheme.MonoFont);
        if (classic)
        {
            _lineReviewLayer.Background = DeskTheme.Surface;
            _lineReviewLayer.BorderBrush = DeskTheme.Divider;
            _lineReviewLayer.BorderThickness = new Thickness(1);
            _lineReviewLayer.CornerRadius = new CornerRadius(0);
            return;
        }
        _lineReviewLayer.Background = DeskTheme.Background;
        _lineReviewLayer.CornerRadius = new CornerRadius(0);
        _lineReviewLayer.BorderThickness = new Thickness(1);
    }

    private static void ApplyReviewFont(DependencyObject node, FontFamily font)
    {
        if (node is TextBlock text) text.FontFamily = font;
        else if (node is Control control) control.FontFamily = font;
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++) ApplyReviewFont(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i), font);
        if (node is Panel panel)
            foreach (var child in panel.Children) ApplyReviewFont(child, font);
        else if (node is Border border && border.Child is not null) ApplyReviewFont(border.Child, font);
    }

    internal void OpenLineReview(ChatActivityView? turn, string sourceId, int turnIndex, string? focusFile)
    {
        if (!_lineReviewOn || turn is null) return;
        var documents = LineReview.Documents(turn.Plan, turn.Changes).ToList();
        if (focusFile is not null && !documents.Any(item => item.Kind == "file" && string.Equals(item.Title, focusFile, StringComparison.OrdinalIgnoreCase)))
            documents.Add(new ReviewDocument(focusFile, "file", focusFile, [new ReviewLine(1, "context", "No before/after snapshot was retained for this file.", null)]));
        if (documents.Count == 0)
            documents.Add(new ReviewDocument("files", "file", "Files", [new ReviewLine(1, "context", "No line snapshot was retained for this edit.", null)]));
        if (focusFile is not null)
            documents = documents.OrderBy(item => item.Kind == "file" && string.Equals(item.Title, focusFile, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
        var saved = LineReview.Recall(_saved, sourceId, turnIndex);
        _reviewReadOnly = saved?.ReadOnly == true;
        _reviewDraft = saved is null
            ? new LineReviewDraft(sourceId, turnIndex, documents)
            : LineReviewDraft.FromSaved(saved, documents);
        _reviewFocus = focusFile;
        _selectedDocument = null;
        RenderLineReview();
    }

    private void EnsureLineReviewLayer()
    {
        if (_lineReviewLayer is not null) return;
        _lineReviewBody = new StackPanel { Spacing = DeskTheme.Space8 };
        var scroll = new ScrollViewer
        {
            Content = _lineReviewBody,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var root = new Grid { RowSpacing = DeskTheme.Space8 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new StackPanel
        {
            Spacing = DeskTheme.Space4,
            Children =
            {
                Head("Review"),
                Note("Comment on a line, or mark it change this or drop this. Send review asks for those lines only. Nothing here is applied or committed.")
            }
        });
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DeskTheme.Space8 };
        var send = Button("Send review", () => SendLineReviewAsync(false));
        var approve = Button("Approve all", () => SendLineReviewAsync(true));
        var close = Button("Close review", CloseLineReview);
        _reviewSend = send;
        _reviewApprove = approve;
        AutomationName(send, "Send review");
        AutomationName(approve, "Approve all");
        actions.Children.Add(send);
        actions.Children.Add(approve);
        actions.Children.Add(close);
        Grid.SetRow(actions, 2);
        root.Children.Add(actions);
        _lineReviewLayer = new Border
        {
            Child = root,
            Background = DeskTheme.Surface,
            BorderBrush = DeskTheme.Divider,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(DeskTheme.Space12),
            Margin = new Thickness(0, DeskTheme.Space8, 0, DeskTheme.Space8),
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        AutomationName(_lineReviewLayer, "Line review");
        ApplyLineReviewChrome(_classicChat);
        Grid.SetRow(_lineReviewLayer, 2);
        Grid.SetColumn(_lineReviewLayer, 0);
        _chatPage.Children.Add(_lineReviewLayer);
    }

    private void CloseLineReview()
    {
        if (_lineReviewLayer is not null) _lineReviewLayer.Visibility = Visibility.Collapsed;
    }

    private void RenderLineReview()
    {
        if (_reviewDraft is null) return;
        EnsureLineReviewLayer();
        if (_lineReviewBody is null || _lineReviewLayer is null) return;
        _chatPage.Children.Remove(_lineReviewLayer);
        Grid.SetRow(_lineReviewLayer, 2);
        Grid.SetColumn(_lineReviewLayer, 0);
        _chatPage.Children.Add(_lineReviewLayer);
        _lineReviewLayer.Visibility = Visibility.Visible;
        if (_reviewSend is not null) _reviewSend.Visibility = _reviewReadOnly ? Visibility.Collapsed : Visibility.Visible;
        if (_reviewApprove is not null) _reviewApprove.Visibility = _reviewReadOnly ? Visibility.Collapsed : Visibility.Visible;
        _lineReviewBody.Children.Clear();
        if (_reviewReadOnly)
            _lineReviewBody.Children.Add(Note("Saved with this conversation. Reopened read-only."));
        if (_reviewFocus is not null)
            _lineReviewBody.Children.Add(Note("Opened from " + _reviewFocus + "."));
        var saved = LineReview.Recall(_saved, _reviewDraft.SourceId, _reviewDraft.TurnIndex);
        if (_reviewReadOnly && saved is { SentMessage.Length: > 0 })
            _lineReviewBody.Children.Add(new TextBlock { Text = saved.SentMessage, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontFamily = DeskTheme.MonoFont, FontSize = DeskTheme.SizeCaption });
        foreach (var document in _reviewDraft.Documents)
        {
            _lineReviewBody.Children.Add(Head(document.Kind == "plan" ? "Plan" : document.Title));
            if (document.Kind == "file") RenderFileDecision(document);
            foreach (var line in document.Lines) RenderReviewLine(document, line);
        }
    }

    private void RenderFileDecision(ReviewDocument document)
    {
        if (_reviewDraft is null || _lineReviewBody is null) return;
        var decision = _reviewDraft.Files.LastOrDefault(item => string.Equals(item.File, document.Title, StringComparison.OrdinalIgnoreCase))?.Decision;
        if (_reviewReadOnly)
        {
            if (decision is not null) _lineReviewBody.Children.Add(Note(decision == "approve" ? "Approved" : "Rejected"));
            return;
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DeskTheme.Space8 };
        var approve = Button("Approve", () =>
        {
            _reviewDraft.Decide(document.Title, "approve");
            StoreOpenReview(false);
            RenderLineReview();
        });
        var reject = Button("Reject", () =>
        {
            _reviewDraft.Decide(document.Title, "reject");
            StoreOpenReview(false);
            RenderLineReview();
        });
        AutomationName(approve, "Approve " + document.Title);
        AutomationName(reject, "Reject " + document.Title);
        if (decision == "approve") approve.Background = DeskTheme.Success;
        if (decision == "reject") reject.Background = DeskTheme.Error;
        row.Children.Add(approve);
        row.Children.Add(reject);
        _lineReviewBody.Children.Add(row);
    }

    private void RenderReviewLine(ReviewDocument document, ReviewLine line)
    {
        if (_reviewDraft is null || _lineReviewBody is null) return;
        var prefix = line.Kind switch { "added" => "+ ", "removed" => "- ", _ => "  " };
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(DeskTheme.Space4, DeskTheme.Space4, DeskTheme.Space4, DeskTheme.Space4),
            Content = new TextBlock
            {
                Text = line.Number.ToString().PadLeft(4) + "  " + prefix + line.Text,
                FontFamily = DeskTheme.MonoFont,
                FontSize = DeskTheme.SizeCaption,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = false,
                Foreground = line.Kind switch
                {
                    "added" => DeskTheme.Success,
                    "removed" => DeskTheme.Error,
                    _ => DeskTheme.Text
                }
            }
        };
        AutomationName(button, "Review line " + line.Number + " " + document.Title);
        var documentId = document.Id;
        var number = line.Number;
        button.Click += (_, _) =>
        {
            if (_reviewReadOnly) return;
            _selectedDocument = documentId;
            _selectedLine = number;
            RenderLineReview();
        };
        _lineReviewBody.Children.Add(button);
        foreach (var note in _reviewDraft.Notes.Where(item => item.DocumentId == document.Id && item.Line == line.Number))
            _lineReviewBody.Children.Add(Note(note.Action + (note.Note.Length == 0 ? "" : " — " + note.Note)));
        if (_reviewReadOnly || _selectedDocument != document.Id || _selectedLine != line.Number) return;
        var editor = new TextBox { PlaceholderText = "Note for this line", TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, FontSize = DeskTheme.SizeCode };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DeskTheme.Space8 };
        void Mark(string action, bool hunk)
        {
            _reviewDraft.Capture(documentId, number, action, editor.Text, hunk);
            StoreOpenReview(false);
            RenderLineReview();
        }
        actions.Children.Add(Button("Comment", () => Mark("comment", false)));
        actions.Children.Add(Button("Change this", () => Mark("change this", false)));
        actions.Children.Add(Button("Drop this", () => Mark("drop this", false)));
        if (line.Hunk is not null)
        {
            actions.Children.Add(Button("Change this hunk", () => Mark("change this", true)));
            actions.Children.Add(Button("Drop this hunk", () => Mark("drop this", true)));
        }
        _lineReviewBody.Children.Add(editor);
        _lineReviewBody.Children.Add(actions);
    }

    private void StoreOpenReview(bool readOnly)
    {
        if (_reviewDraft is null) return;
        var message = readOnly ? LineReview.Message(_reviewDraft, false) : _reviewDraft.Format();
        _saved = LineReview.Remember(_saved, _reviewDraft.ToSaved(message, readOnly)) with { Updated = DateTimeOffset.UtcNow };
        Save();
    }

    private async Task SendLineReviewAsync(bool approveAll)
    {
        if (_reviewDraft is null || _reviewReadOnly || _reviewSending) return;
        if (!string.IsNullOrWhiteSpace(_chatInput.Text) || _draftFiles.Count > 0 || !ReviewComposerReady())
        {
            StoreOpenReview(false);
            _chatStatus.Text = "Review saved. Keep the composer empty and use Send review when this chat can accept an instruction. Nothing was applied or committed.";
            return;
        }
        if (approveAll) _reviewDraft.ApproveAllFiles();
        var draft = _reviewDraft;
        var message = LineReview.Message(draft, approveAll);
        var oldTurns = _saved.Turns.Length;
        var known = _steeringQueue.Snapshot.Select(instruction => instruction.Id)
            .Concat(_saved.Turns.SelectMany(turn => turn.Instructions ?? []).Select(instruction => instruction.Id)).ToHashSet();
        static string Normalized(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var normalizedMessage = Normalized(message);
        bool Accepted() => _steeringQueue.Snapshot.Any(instruction => !known.Contains(instruction.Id) && Normalized(instruction.Text) == normalizedMessage)
            || _saved.Turns.Skip(oldTurns).Any(turn => Normalized(turn.Prompt) == normalizedMessage)
            || _saved.Turns.SelectMany(turn => turn.Instructions ?? []).Any(instruction => !known.Contains(instruction.Id) && Normalized(instruction.Text) == normalizedMessage);
        _reviewSending = true;
        try
        {
            StoreOpenReview(false);
            _chatInput.Text = message;
            var send = SendChatAsync();
            await Task.Yield();
            if (!Accepted()) await send;
            var accepted = Accepted();
            _saved = LineReview.Remember(_saved, draft.ToSaved(message, accepted));
            Save();
            if (ReferenceEquals(_reviewDraft, draft)) { _reviewReadOnly = accepted; RenderLineReview(); }
            if (!accepted) _chatStatus.Text = "Review saved but not submitted. Connect a model and use Send review again; the feedback stays in your draft.";
            if (accepted) _ = ObserveReviewSendAsync(send);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        { _chatStatus.Text = "Review could not be submitted: " + ex.Message; }
        finally { _reviewSending = false; }
    }

    private async Task ObserveReviewSendAsync(Task send)
    {
        try { await send; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        { if (!_disposed) _chatStatus.Text = "Review response stopped or failed: " + ex.Message; }
    }

    private bool ReviewComposerReady()
    {
        if (CursorChat || GrokBuildChat || CodexChat) return CursorComposerAvailable;
        return !_chatBusy && !_attachmentBusy;
    }
}
