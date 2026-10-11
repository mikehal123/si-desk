using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text;

namespace Sovereign.Desktop;

internal sealed partial class ChatConversation
{
    private async Task ShowChatTextAsync()
    {
        DrainSavedTurn();
        var text = new StringBuilder();
        foreach (var turn in _saved.Turns)
        {
            text.AppendLine("You").AppendLine(turn.Prompt).AppendLine();
            text.AppendLine("Assistant · " + turn.Outcome).AppendLine(turn.Reply).AppendLine();
        }
        await _dialogs.WaitAsync();
        try
        {
            var editor = new TextBox { IsReadOnly = true, AcceptsReturn = true, Text = text.ToString().TrimEnd(),
                TextWrapping = TextWrapping.Wrap, MinWidth = 500, Height = 420 };
            var dialog = new ContentDialog { XamlRoot = _owner.Content.XamlRoot, Title = "Conversation text · Ctrl+A / Ctrl+C",
                Content = editor, PrimaryButtonText = "Copy all", CloseButtonText = "Close" };
            dialog.Opened += (_, _) => { editor.Focus(FocusState.Programmatic); editor.SelectAll(); };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            { await ChatClipboard.CopyAsync(editor.DispatcherQueue, editor.Text); }
        }
        catch (Exception) { _chatStatus.Text = "Could not open or copy conversation text. Please try again."; }
        finally { _dialogs.Release(); }
    }
}
