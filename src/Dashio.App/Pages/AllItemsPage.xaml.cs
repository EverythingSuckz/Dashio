using Dashio.App.Services;
using Dashio.App.ViewModels;
using Dashio.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace Dashio.App.Pages;

public sealed partial class AllItemsPage : Page
{
    public AllItemsViewModel ViewModel { get; } = new();

    public AllItemsPage()
    {
        InitializeComponent();
        SizeChanged += (_, e) => Ui.FitToPage(Shell, e.NewSize.Width);
        UpdateHeaders();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        AppServices.State.Changed += Rebuild;
        AppServices.Shell.SearchChanged += Rebuild;
        AppServices.Settings.Changed += Rebuild;
        AppServices.Pending.Changed += Pending_Changed;
        ViewModel.Rebuild();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        AppServices.State.Changed -= Rebuild;
        AppServices.Shell.SearchChanged -= Rebuild;
        AppServices.Settings.Changed -= Rebuild;
        AppServices.Pending.Changed -= Pending_Changed;
    }

    private void Rebuild(object? sender, EventArgs e) => ViewModel.Rebuild();

    private void Pending_Changed(object? sender, EventArgs e) => ViewModel.RefreshPending();

    private void KindBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var tag = (KindBox.SelectedItem as ComboBoxItem)?.Tag as string;
        ViewModel.Kind = Enum.TryParse<AutostartKind>(tag, out var kind) ? kind : null;
        ViewModel.Rebuild();
    }

    private void Header_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag && Enum.TryParse<ItemColumn>(tag, out var column))
        {
            ViewModel.SortBy(column);
            UpdateHeaders();
        }
    }

    /// <summary>Shows an arrow on the column the table is sorted by.</summary>
    private void UpdateHeaders()
    {
        var arrow = ViewModel.SortDescending ? "  ↓" : "  ↑";
        foreach (var (button, column, label) in new[]
                 {
                     (NameHeader, ItemColumn.Name, "Name"),
                     (TypeHeader, ItemColumn.Type, "Type"),
                     (AppHeader, ItemColumn.App, "App"),
                     (StartsHeader, ItemColumn.Starts, "Starts"),
                     (StateHeader, ItemColumn.State, "State"),
                 })
        {
            button.Content = ViewModel.SortColumn == column ? label + arrow : label;
        }
    }

    private void Row_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ItemRowViewModel row)
            Open(row);
    }

    /// <summary>The app's page slides in from the right, and back out the same way.</summary>
    private void Open(ItemRowViewModel row) => Frame.Navigate(
        typeof(AppDetailPage), row.Group.Id,
        new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight });

    // ---- Right-click menu ----

    private ItemRowViewModel? _menuRow;

    /// <summary>Points the shared menu at the row it was opened on.</summary>
    private void RowMenu_Opening(object? sender, object e)
    {
        if (sender is not MenuFlyout menu)
            return;
        _menuRow = (menu.Target as ContentControl)?.Content as ItemRowViewModel;
        if (_menuRow is not { } row)
        {
            menu.Hide();
            return;
        }

        foreach (var item in menu.Items.OfType<MenuFlyoutItem>())
        {
            switch (item.Tag)
            {
                case "toggle":
                    item.Text = row.ToggleLabel;
                    item.IsEnabled = row.CanChange;
                    break;
                case "command":
                    item.IsEnabled = row.HasCommand;
                    break;
                case "file":
                    item.IsEnabled = row.HasFile;
                    break;
            }
        }
    }

    private void OpenApp_Click(object sender, RoutedEventArgs e)
    {
        if (_menuRow is { } row)
            Open(row);
    }

    private void ToggleItem_Click(object sender, RoutedEventArgs e)
    {
        if (_menuRow is { } row)
            row.IsOn = !row.IsOn;
    }

    private void CopyName_Click(object sender, RoutedEventArgs e) => ItemActions.Copy(_menuRow?.Name);

    private void CopyCommand_Click(object sender, RoutedEventArgs e) => ItemActions.Copy(_menuRow?.Item.Command);

    private void RevealFile_Click(object sender, RoutedEventArgs e) => ItemActions.Reveal(_menuRow?.Item.TargetPath);
}
