using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sovereign.Desktop.Client;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Sovereign.Desktop;

public sealed partial class MainWindow
{
    private SkillLibrary? _skills;
    private StackPanel? _skillCards;
    private TextBlock? _skillStatus;

    internal SkillLibrary Skills => _skills ??= SkillLibrary.Open(_vm.ProfileDirectory,
        new Chroma384Embedder(Path.Combine(AppContext.BaseDirectory, "skill-chroma", "embed384.py")));

    private StackPanel SkillLibrarySection()
    {
        var enabled = new CheckBox { Content = "Subagent skill library", IsChecked = Skills.Enabled };
        AutomationName(enabled, "Subagent skill library");
        ToolTipService.SetToolTip(enabled, "Off by default. When on, a successful helper can be saved and later helpers receive up to three past approaches. Nothing is trained.");
        _skillStatus = Note(Skills.Enabled ? "On. New helper runs can save and retrieve cards." : "Off. Helper runs do not save, retrieve, or prune cards.");
        _skillCards = new StackPanel { Spacing = DeskTheme.Space8 };
        enabled.Click += (_, _) =>
        {
            Skills.Enabled = enabled.IsChecked == true;
            if (_skillStatus is not null) _skillStatus.Text = Skills.Enabled
                ? "On. New helper runs can save and retrieve cards."
                : "Off. Helper runs do not save, retrieve, or prune cards.";
        };
        RenderSkillCards();
        return new StackPanel
        {
            Spacing = DeskTheme.Space8,
            Children =
            {
                Head("Subagent skill library"),
                enabled,
                Note("Cards stay on this PC. Similarity uses the existing Chroma 384-d model. The library holds 50 cards and drops the lowest unpinned score and use count when it is full. Pinned cards stay."),
                _skillStatus,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = DeskTheme.Space8,
                    Children = { Button("Export library", ExportSkillLibraryAsync), Button("Import", ImportSkillLibraryAsync) }
                },
                new ScrollViewer { MaxHeight = 420, Content = _skillCards }
            }
        };
    }

    private void RenderSkillCards()
    {
        if (_skillCards is null) return;
        _skillCards.Children.Clear();
        var cards = Skills.Cards;
        if (cards.Count == 0)
        {
            _skillCards.Children.Add(Note("No skill cards yet."));
            return;
        }
        foreach (var card in cards.OrderByDescending(item => item.SavedAt))
        {
            var id = card.Id;
            var name = new TextBox { Header = "Name", Text = card.Name };
            var when = new TextBox { Header = "When to use it", Text = card.WhenToUse, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true };
            var steps = new TextBox { Header = "Steps that worked", Text = card.Steps, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MinHeight = 72 };
            var tools = new TextBox { Header = "Tools", Text = string.Join(", ", card.Tools) };
            var pin = new CheckBox { Content = "Pin (never pruned)", IsChecked = card.Pinned };
            AutomationName(pin, "Pin " + card.Name);
            pin.Click += (_, _) => { Skills.Pin(id, pin.IsChecked == true); if (_skillStatus is not null) _skillStatus.Text = (pin.IsChecked == true ? "Pinned " : "Unpinned ") + name.Text; };
            var save = Button("Save card", () =>
            {
                var toolNames = tools.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (!Skills.Update(id, name.Text, when.Text, steps.Text, toolNames))
                {
                    if (_skillStatus is not null) _skillStatus.Text = "That card was not changed. The 384-d embedding was unavailable, or it matches another card too closely.";
                    return;
                }
                if (_skillStatus is not null) _skillStatus.Text = "Saved " + name.Text;
                RenderSkillCards();
            });
            var delete = Button("Delete", () => { Skills.Delete(id); if (_skillStatus is not null) _skillStatus.Text = "Deleted " + name.Text; RenderSkillCards(); });
            AutomationName(delete, "Delete " + card.Name);
            _skillCards.Children.Add(new StackPanel
            {
                Spacing = DeskTheme.Space4,
                Children =
                {
                    Note(card.SavedAt.ToLocalTime().ToString("yyyy-MM-dd") + " · score " + card.Score.ToString("0.00") + " · used " + card.UseCount + " · run " + card.SourceRunId),
                    name, when, steps, tools, pin,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = DeskTheme.Space8, Children = { save, delete } }
                }
            });
        }
    }

    private async Task ExportSkillLibraryAsync()
    {
        var picker = new FileSavePicker { SuggestedFileName = "sovereign-skill-library", SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        await FileIO.WriteTextAsync(file, Skills.ExportJson());
        if (_skillStatus is not null) _skillStatus.Text = "Exported " + Skills.Cards.Count + " cards.";
    }

    private async Task ImportSkillLibraryAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".json");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        try
        {
            Skills.ImportJson(await FileIO.ReadTextAsync(file));
            RenderSkillCards();
            if (_skillStatus is not null) _skillStatus.Text = "Imported " + Skills.Cards.Count + " cards.";
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        { if (_skillStatus is not null) _skillStatus.Text = "Import failed: " + ex.Message; }
    }
}
