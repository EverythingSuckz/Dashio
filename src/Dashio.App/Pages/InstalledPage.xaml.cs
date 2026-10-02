using System.Diagnostics;
using Dashio.App.Services;
using Dashio.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace Dashio.App.Pages;

public sealed partial class InstalledPage : Page
{
    // The page width, in effective pixels, from which the side panels fit beside the list.
    private const double SidePanelMinWidth = 1280;
    private const double TabSlide = 48;

    public InstalledViewModel ViewModel { get; } = new();

    public InstalledPage()
    {
        InitializeComponent();

        // The page is cached, so these subscriptions live as long as the window.
        AppServices.Inventory.Changed += (_, _) => ViewModel.Rebuild(resort: false);
        AppServices.Shell.SearchChanged += (_, _) => ViewModel.Rebuild(resort: true);
        ViewModel.Rebuild(resort: true);

        SizeChanged += (_, e) =>
        {
            Ui.FitToPage(Shell, e.NewSize.Width);
            var wide = e.NewSize.Width >= SidePanelMinWidth;
            SidePanel.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetColumnSpan(AppList, wide ? 1 : 2);
            Grid.SetColumnSpan(EvidenceBar, wide ? 1 : 2);
            AppList.Padding = wide ? new Thickness(36, 0, 24, 24) : new Thickness(36, 0, 36, 24);
            EvidenceBar.Margin = wide ? new Thickness(36, 0, 24, 8) : new Thickness(36, 0, 36, 8);
        };
    }

    /// <summary>Overview opens this page on the "Not opened lately" tab.</summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not InstalledFilter filter || e.NavigationMode != NavigationMode.New)
            return;

        // The tab bar ignores a selection made before it has been laid out, so this waits its turn.
        void Select() => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            var tab = filter == InstalledFilter.NotOpenedLately ? FilterUnused : FilterAll;
            tab.IsSelected = true;
            FilterBar.SelectedItem = tab;
        });

        if (FilterBar.IsLoaded)
        {
            Select();
            return;
        }

        void OnLoaded(object sender, RoutedEventArgs args)
        {
            FilterBar.Loaded -= OnLoaded;
            Select();
        }
        FilterBar.Loaded += OnLoaded;
    }

    private void FilterBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var filter = sender.SelectedItem == FilterUnused ? InstalledFilter.NotOpenedLately : InstalledFilter.All;
        if (filter == ViewModel.Filter)
            return;
        ViewModel.Filter = filter;
        ViewModel.Rebuild(resort: true);
        Motion.EnterSideways(AppList, filter == InstalledFilter.NotOpenedLately ? TabSlide : -TabSlide);
    }

    private void SortSize_Click(object sender, RoutedEventArgs e) => SetSort(InstalledSort.Size);

    private void SortName_Click(object sender, RoutedEventArgs e) => SetSort(InstalledSort.Name);

    private void SortLastOpened_Click(object sender, RoutedEventArgs e) => SetSort(InstalledSort.LastOpened);

    private void SetSort(InstalledSort sort)
    {
        ViewModel.Sort = sort;
        ViewModel.Rebuild(resort: true);
        Motion.Enter(AppList);
    }

    private void Open(InstalledRowViewModel row) => Frame.Navigate(
        typeof(AppDetailPage), row.Id,
        new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight });

    private void Row_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is InstalledRowViewModel row)
            Open(row);
    }

    private async void AdminCheck_Click(object sender, RoutedEventArgs e)
    {
        AdminCheckButton.IsEnabled = false;
        try
        {
            var (message, isError) = await AppServices.Inventory.RunAdminCheckAsync();
            AppServices.Shell.Notify(new Notice(
                isError ? InfoBarSeverity.Error : InfoBarSeverity.Informational, "Last opened", message));
        }
        finally
        {
            AdminCheckButton.IsEnabled = true;
        }
    }

    /// <summary>Uninstalling is left to Windows; this only opens its page.</summary>
    private void OpenWindowsSettings_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true })?.Dispose();

    // ---- Right-click menu ----

    private InstalledRowViewModel? _menuRow;

    private void RowMenu_Opening(object? sender, object e)
    {
        if (sender is not MenuFlyout menu)
            return;
        _menuRow = (menu.Target as ContentControl)?.Content as InstalledRowViewModel;
        if (_menuRow is not { } row)
        {
            menu.Hide();
            return;
        }
        foreach (var item in menu.Items.OfType<MenuFlyoutItem>().Where(i => i.Tag is "file"))
            item.IsEnabled = Location(row) is not null;
    }

    private static string? Location(InstalledRowViewModel row) =>
        AppServices.Inventory.Find(row.Id)?.App.Folders.Select(f => f.Path).FirstOrDefault(Directory.Exists);

    private void OpenRow_Click(object sender, RoutedEventArgs e)
    {
        if (_menuRow is { } row)
            Open(row);
    }

    private void RevealRow_Click(object sender, RoutedEventArgs e)
    {
        if (_menuRow is { } row)
            ItemActions.OpenFolder(Location(row));
    }

    private void CopyRowName_Click(object sender, RoutedEventArgs e) => ItemActions.Copy(_menuRow?.Name);
}
