using Dashio.App.Services;
using Dashio.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;

namespace Dashio.App.Pages;

public sealed partial class AppsPage : Page
{
    // Widths in effective pixels of the page itself, so the layout is right at any display scale.
    private const double SidePanelMinWidth = 1280;
    private const double StatsBesideTitleMinWidth = 900;
    private const double StatsInARowMinWidth = 660;

    public AppsViewModel ViewModel { get; } = new();
    public ScanState State => AppServices.State;

    public AppsPage()
    {
        InitializeComponent();

        // The page is cached, so these subscriptions live as long as the window.
        AppServices.State.Changed += (_, _) => ViewModel.Rebuild();
        AppServices.Shell.SearchChanged += (_, _) => ViewModel.Rebuild();
        AppServices.Settings.Changed += (_, _) =>
        {
            ShowWindowsItem.IsChecked = AppServices.Settings.ShowWindowsComponents;
            ViewModel.Rebuild();
        };

        ShowWindowsItem.IsChecked = AppServices.Settings.ShowWindowsComponents;
        ViewModel.Rebuild();
        ApplyViewMode();

        SizeChanged += (_, e) =>
        {
            Ui.FitToPage(Shell, e.NewSize.Width);
            ApplyLayout(e.NewSize.Width);
        };
    }

    /// <summary>Side panels on wide pages; headline numbers beside the title, under it, or stacked.</summary>
    private void ApplyLayout(double width)
    {
        var wide = width >= SidePanelMinWidth;
        SidePanel.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        ListHost.Margin = wide ? new Thickness(36, 0, 24, 24) : new Thickness(36, 0, 36, 24);

        var beside = width >= StatsBesideTitleMinWidth;
        Grid.SetRow(StatsPanel, beside ? 0 : 1);
        Grid.SetColumn(StatsPanel, beside ? 1 : 0);
        StatsPanel.Margin = beside ? new Thickness(24, 0, 0, 0) : new Thickness(0, 16, 0, 0);

        var inARow = width >= StatsInARowMinWidth;
        StatsPanel.Orientation = inARow ? Orientation.Horizontal : Orientation.Vertical;
        StatsPanel.Spacing = inARow ? 32 : 12;
    }

    // ---- Rows or tiles ----

    /// <summary>Shows rows or tiles, and makes the toggle offer the other one.</summary>
    private void ApplyViewMode()
    {
        var grid = AppServices.Settings.AppsAsGrid;
        AppGrid.Visibility = grid ? Visibility.Visible : Visibility.Collapsed;
        AppList.Visibility = grid ? Visibility.Collapsed : Visibility.Visible;

        GridIcon.Visibility = grid ? Visibility.Collapsed : Visibility.Visible;
        ListIcon.Visibility = grid ? Visibility.Visible : Visibility.Collapsed;
        var offer = grid ? "Show as a list" : "Show as a grid";
        ToolTipService.SetToolTip(LayoutToggle, offer);
        AutomationProperties.SetName(LayoutToggle, offer);
    }

    private void LayoutToggle_Click(object sender, RoutedEventArgs e)
    {
        AppServices.Settings.AppsAsGrid = !AppServices.Settings.AppsAsGrid;
        AppServices.Settings.Save();
        ApplyViewMode();
        Motion.Enter(ListHost);
    }

    // ---- Filter and sort ----

    /// <summary>Rebuilds the list and lets it settle in, so a filter or sort change does not just flicker.</summary>
    private void RebuildWithMotion()
    {
        ViewModel.Rebuild();
        Motion.Enter(ListHost);
    }

    private const double TabSlide = 48;

    /// <summary>The list comes in from the side the chosen tab is on: from the right going right, from the left going back.</summary>
    private void FilterBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var filter = sender.SelectedItem == FilterAtStartup ? AppFilter.AtStartup
            : sender.SelectedItem == FilterNotInTaskManager ? AppFilter.NotInTaskManager
            : AppFilter.All;
        if (filter == ViewModel.Filter)
            return;

        var toTheRight = filter > ViewModel.Filter;
        ViewModel.Filter = filter;
        ViewModel.Rebuild();
        Motion.EnterSideways(ListHost, toTheRight ? TabSlide : -TabSlide);
    }

    private void SortMostAtStartup_Click(object sender, RoutedEventArgs e) => SetSort(AppSort.MostAtStartup);

    private void SortName_Click(object sender, RoutedEventArgs e) => SetSort(AppSort.Name);

    private void SetSort(AppSort sort)
    {
        ViewModel.Sort = sort;
        RebuildWithMotion();
    }

    private void ShowWindows_Click(object sender, RoutedEventArgs e)
    {
        AppServices.Settings.ShowWindowsComponents = ShowWindowsItem.IsChecked;
        AppServices.Settings.Save();
    }

    // ---- Opening an app ----

    /// <summary>The app's page slides in from the right, and back out the same way.</summary>
    private void Open(AppRowViewModel row) => Frame.Navigate(
        typeof(AppDetailPage), row.Group.Id,
        new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight });

    private void AppCard_Activated(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AppRowViewModel row)
            Open(row);
    }

    private void AppTile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AppRowViewModel row)
            Open(row);
    }

    // ---- Right-click menu ----

    private AppRowViewModel? _menuRow;

    /// <summary>
    /// Right-click, Shift+F10 or press-and-hold on an app. One menu serves every app, so it is
    /// pointed at this one and then shown where the pointer is.
    /// </summary>
    private void App_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement element ||
            (element.Tag ?? element.DataContext) is not AppRowViewModel row ||
            Resources["AppMenu"] is not MenuFlyout menu)
        {
            return;
        }

        _menuRow = row;
        foreach (var item in menu.Items.OfType<MenuFlyoutItem>().Where(i => i.Tag is "toggle"))
        {
            item.Text = row.ToggleAllLabel;
            item.IsEnabled = row.CanToggleAll;
        }

        if (args.TryGetPosition(element, out var point))
            menu.ShowAt(element, new FlyoutShowOptions { Position = point });
        else
            menu.ShowAt(element);
        args.Handled = true;
    }

    private void AppOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_menuRow is { } row)
            Open(row);
    }

    private void AppToggleAll_Click(object sender, RoutedEventArgs e)
    {
        if (_menuRow is { } row)
            ItemActions.ToggleAll(row.Group);
    }

    private void AppCopyName_Click(object sender, RoutedEventArgs e) => ItemActions.Copy(_menuRow?.Name);
}
