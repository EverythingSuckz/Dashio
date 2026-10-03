using Dashio.App.Services;
using Dashio.App.ViewModels;
using Dashio.Core.Journal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Dashio.App.Pages;

public sealed partial class HistoryPage : Page
{
    public HistoryViewModel ViewModel { get; } = new();

    public HistoryPage()
    {
        InitializeComponent();
        SizeChanged += (_, e) => Ui.FitToPage(Shell, e.NewSize.Width);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        AppServices.State.Changed += State_Changed;
        ViewModel.Rebuild();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => AppServices.State.Changed -= State_Changed;

    private void State_Changed(object? sender, EventArgs e) => ViewModel.Rebuild();

    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is HistoryRow row)
            await UndoAsync([row.Entry]);
    }

    private async void UndoBatch_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is HistoryBatch batch)
            await UndoAsync(batch.Entries.Where(entry => entry.UndoOf is null).ToList());
    }

    private async Task UndoAsync(IReadOnlyList<JournalEntry> entries)
    {
        var (changes, drifted, missing) = ChangeRunner.PlanUndo(entries);

        if (changes.Count == 0)
        {
            AppServices.Shell.Notify(new Notice(
                InfoBarSeverity.Warning,
                "Nothing to undo",
                missing.Count > 0
                    ? "The item no longer exists on this PC, so there is nothing to put back."
                    : "This change has already been undone."));
            return;
        }

        if (drifted.Count > 0)
        {
            var names = string.Join(", ", drifted.Select(d => d.ItemName).Distinct().Take(5));
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme,
                Title = "Changed since then",
                Content = new TextBlock
                {
                    Text = $"Something other than Dashio has changed {names} since this was recorded. " +
                           "Undo will still put it back the way it was before Dashio's change.",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "Undo anyway",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            ConfirmDialog.WithIcons(dialog, "\uE7A7");
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;
        }

        await ChangeRunner.ApplyAsync(changes);
    }
}

/// <summary>Picks the batch heading or the entry template for a row of the flat history list.</summary>
public sealed class HistoryRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? BatchTemplate { get; set; }
    public DataTemplate? EntryTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item is HistoryBatch ? BatchTemplate : EntryTemplate;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}
