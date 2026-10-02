using Dashio.App.Services;
using Dashio.App.ViewModels;
using Dashio.Core.Journal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Dashio.App.Pages;

public sealed partial class AppDetailPage : Page
{
    private const string CopyGlyph = "\uE8C8";
    private const string DoneGlyph = "\uE73E";

    public AppDetailViewModel ViewModel { get; } = new();

    /// <summary>The page width, in effective pixels, from which "About this app" fits beside the items.</summary>
    private const double SidePanelMinWidth = 1280;

    public AppDetailPage()
    {
        InitializeComponent();
        SizeChanged += (_, e) => Ui.FitToPage(Shell, e.NewSize.Width);
        SizeChanged += (_, e) =>
        {
            var wide = e.NewSize.Width >= SidePanelMinWidth;
            AboutWide.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
            AboutNarrow.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        AppServices.State.Changed += State_Changed;
        AppServices.Pending.Changed += Pending_Changed;
        AppServices.Monitor.Updated += Monitor_Updated;
        AppServices.Inventory.Changed += Inventory_Changed;
        ViewModel.Load(e.Parameter as string ?? "");
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        AppServices.State.Changed -= State_Changed;
        AppServices.Pending.Changed -= Pending_Changed;
        AppServices.Monitor.Updated -= Monitor_Updated;
        AppServices.Inventory.Changed -= Inventory_Changed;
    }

    private void Inventory_Changed(object? sender, EventArgs e) => ViewModel.UpdateStorage();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: FolderRow row })
            ItemActions.OpenFolder(row.Path);
    }

    private void Monitor_Updated(object? sender, EventArgs e) => ViewModel.UpdateUsage();

    private void State_Changed(object? sender, EventArgs e)
    {
        ViewModel.Rebuild();
        // A rescan can regroup items; if this app is gone, go back to the list.
        if (!ViewModel.Exists && Frame.CanGoBack)
            Frame.GoBack();
    }

    private void Pending_Changed(object? sender, EventArgs e) => ViewModel.RefreshPending();

    private void TurnAll_Click(object sender, RoutedEventArgs e) => ViewModel.ToggleAll();

    private async void EndApp_Click(object sender, RoutedEventArgs e) =>
        await RunningActions.EndAppAsync(this, ViewModel.GroupId);

    private void ShowLocation_Click(object sender, RoutedEventArgs e) =>
        ItemActions.Reveal(RunningActions.LocationOf(ViewModel.GroupId));

    private async void EndProcess_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ProcessRowViewModel row })
            await RunningActions.EndProcessAsync(this, ViewModel.GroupId, row.Usage);
    }

    private void RevealProcess_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ProcessRowViewModel row })
            ItemActions.Reveal(row.Path);
    }

    private async void StopItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ItemRowViewModel row })
            await ChangeRunner.ApplyAsync([ChangePlanner.StopNow(row.Item, row.AppName)]);
    }

    private async void CopyDetail_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DetailRow row, Content: FontIcon icon })
            return;

        ItemActions.Copy(row.Value);

        // A tick for a moment, so the click visibly did something.
        icon.Glyph = DoneGlyph;
        await Task.Delay(1500);
        icon.Glyph = CopyGlyph;
    }

    private void RevealDetail_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DetailRow row })
            ItemActions.Reveal(row.RevealPath);
    }
}

/// <summary>Picks the heading or the item template for a row of the flat detail list.</summary>
public sealed class DetailRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? HeaderTemplate { get; set; }
    public DataTemplate? ItemTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item is SectionHeader ? HeaderTemplate : ItemTemplate;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}
