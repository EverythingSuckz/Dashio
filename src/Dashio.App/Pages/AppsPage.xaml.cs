using Dashio.App.Services;
using Dashio.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace Dashio.App.Pages;

public sealed partial class AppsPage : Page, IFilterPage
{
    // Widths in effective pixels of the page itself, so the layout is right at any display scale.
    private const double SidePanelMinWidth = 1280;
    // These two are widths of the list, which is narrower than the page beside the side panel.
    private const double LastOpenedMinWidth = 860;
    private const double SizeMinWidth = 700;
    private const double FilterBesideTitleMinWidth = 700;
    private const double TabSlide = 48;

    private bool _wide;

    public AppsViewModel ViewModel { get; } = new();
    public ScanState State => AppServices.State;

    public AppsPage()
    {
        InitializeComponent();

        // The page is cached, so these subscriptions live as long as the window.
        AppServices.State.Changed += (_, _) => ViewModel.Rebuild(resort: true);
        AppServices.Inventory.Changed += (_, _) => ViewModel.Rebuild(resort: false);
        AppServices.Monitor.Updated += (_, _) => ViewModel.UpdateUsage();
        AppServices.Settings.Changed += (_, _) =>
        {
            ShowWindowsItem.IsChecked = AppServices.Settings.ShowWindowsComponents;
            ShowComponentsItem.IsChecked = AppServices.Settings.ShowComponents;
            ViewModel.Rebuild(resort: true);
        };

        ShowWindowsItem.IsChecked = AppServices.Settings.ShowWindowsComponents;
        ShowComponentsItem.IsChecked = AppServices.Settings.ShowComponents;
        ViewModel.Rebuild(resort: true);
        ApplyViewMode();
        UpdateHeaders();

        SizeChanged += (_, e) =>
        {
            Ui.FitToPage(Shell, e.NewSize.Width);
            ApplyLayout(e.NewSize.Width);
        };
    }

    /// <summary>Overview's tiles open this page on a particular tab.</summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        // An app that started or closed since the last visit is listed or dropped now.
        ViewModel.Rebuild(resort: false);
        // Storage and Overview open this page with the largest apps first.
        if (e.Parameter is AppColumn column && e.NavigationMode == NavigationMode.New)
        {
            ViewModel.SetSort(column);
            UpdateHeaders();
        }
        if (e.Parameter is not AppFilter filter || e.NavigationMode != NavigationMode.New)
            return;

        // The tab bar ignores a selection made before it has been laid out, so this waits its turn.
        void Select() => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            var tab = filter switch
            {
                AppFilter.Running => FilterRunning,
                AppFilter.AtStartup => FilterAtStartup,
                AppFilter.NotInTaskManager => FilterNotInTaskManager,
                AppFilter.NotOpenedLately => FilterUnused,
                _ => FilterAll,
            };
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

    public void FocusFilter() => FilterBox.Focus(FocusState.Keyboard);

    /// <summary>The side panel on wide pages, and only the columns there is room for.</summary>
    private void ApplyLayout(double width)
    {
        _wide = width >= SidePanelMinWidth;
        SidePanel.Visibility = _wide ? Visibility.Visible : Visibility.Collapsed;
        var span = _wide ? 1 : 2;
        Grid.SetColumnSpan(ColumnHeader, span);
        Grid.SetColumnSpan(AppList, span);
        Grid.SetColumnSpan(GridScroller, span);
        Grid.SetColumnSpan(EvidenceBar, span);

        var right = _wide ? 24 : 36;
        ColumnHeader.Margin = new Thickness(36, 0, right, 0);
        EvidenceBar.Margin = new Thickness(36, 0, right, 8);
        AppList.Padding = new Thickness(36, 4, right, 24);
        AppGrid.Margin = new Thickness(36, 0, right, 24);

        // The list itself is narrower than the page when the side panel is showing.
        var listWidth = _wide ? Math.Min(width, Ui.ShellMaxWidth) - SidePanel.Width : width;
        ViewModel.Columns.ShowLastOpened = listWidth >= LastOpenedMinWidth;
        ViewModel.Columns.ShowSize = listWidth >= SizeMinWidth;

        var beside = width >= FilterBesideTitleMinWidth;
        Grid.SetRow(FilterBox, beside ? 0 : 1);
        Grid.SetColumn(FilterBox, beside ? 1 : 0);
        FilterBox.HorizontalAlignment = beside ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    }

    // ---- Rows or tiles ----

    /// <summary>Shows rows or tiles, and makes the toggle offer the other one.</summary>
    private void ApplyViewMode()
    {
        var grid = AppServices.Settings.AppsAsGrid;
        GridScroller.Visibility = grid ? Visibility.Visible : Visibility.Collapsed;
        AppList.Visibility = ColumnHeader.Visibility = grid ? Visibility.Collapsed : Visibility.Visible;

        GridIcon.Visibility = grid ? Visibility.Collapsed : Visibility.Visible;
        ListIcon.Visibility = grid ? Visibility.Visible : Visibility.Collapsed;
        var offer = grid ? "Show as a list" : "Show as a grid";
        ToolTipService.SetToolTip(LayoutToggle, offer);
        AutomationProperties.SetName(LayoutToggle, offer);
    }

    private UIElement ListHost => AppServices.Settings.AppsAsGrid ? GridScroller : AppList;

    private void LayoutToggle_Click(object sender, RoutedEventArgs e)
    {
        AppServices.Settings.AppsAsGrid = !AppServices.Settings.AppsAsGrid;
        AppServices.Settings.Save();
        ApplyViewMode();
        Motion.Enter(ListHost);
    }

    // ---- Filter and sort ----

    private void FilterBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        var text = sender.Text.Trim();
        if (text == ViewModel.Search)
            return;
        ViewModel.Search = text;
        ViewModel.Rebuild(resort: true);
    }

    /// <summary>The list comes in from the side the chosen tab is on: from the right going right, from the left going back.</summary>
    private void FilterBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var filter = sender.SelectedItem == FilterRunning ? AppFilter.Running
            : sender.SelectedItem == FilterAtStartup ? AppFilter.AtStartup
            : sender.SelectedItem == FilterNotInTaskManager ? AppFilter.NotInTaskManager
            : sender.SelectedItem == FilterUnused ? AppFilter.NotOpenedLately
            : sender.SelectedItem == FilterAdminScan ? AppFilter.AdminScan
            : AppFilter.All;
        if (filter == ViewModel.Filter)
            return;

        var toTheRight = filter > ViewModel.Filter;
        ViewModel.Filter = filter;
        ViewModel.Rebuild(resort: true);
        Motion.EnterSideways(ListHost, toTheRight ? TabSlide : -TabSlide);
    }

    private void Header_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag && Enum.TryParse<AppColumn>(tag, out var column))
        {
            ViewModel.SortBy(column);
            UpdateHeaders();
            Motion.Enter(ListHost);
        }
    }

    private void SortItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag && Enum.TryParse<AppColumn>(tag, out var column))
        {
            ViewModel.SetSort(column);
            UpdateHeaders();
            Motion.Enter(ListHost);
        }
    }

    /// <summary>Shows an arrow on the column the list is sorted by, and ticks the same order in the View menu.</summary>
    private void UpdateHeaders()
    {
        // Names start from A; the other columns start with the most.
        var startsDescending = ViewModel.SortColumn != AppColumn.Name;
        var arrow = startsDescending != ViewModel.SortReversed ? "↓" : "↑";
        foreach (var (button, item, column, label) in new[]
                 {
                     (NameHeader, SortNameItem, AppColumn.Name, "App"),
                     (RunningHeader, SortRunningItem, AppColumn.Running, "Running"),
                     (StartsHeader, SortStartsItem, AppColumn.Starts, "Starts"),
                     (SizeHeader, SortSizeItem, AppColumn.Size, "Size"),
                     (LastOpenedHeader, SortLastOpenedItem, AppColumn.LastOpened, "Last opened"),
                 })
        {
            var sorted = ViewModel.SortColumn == column;
            button.Content = sorted ? $"{label}  {arrow}" : label;
            item.IsChecked = sorted;
        }
    }

    private void ShowWindows_Click(object sender, RoutedEventArgs e)
    {
        AppServices.Settings.ShowWindowsComponents = ShowWindowsItem.IsChecked;
        AppServices.Settings.Save();
    }

    private void ShowComponents_Click(object sender, RoutedEventArgs e) => ShowComponents(ShowComponentsItem.IsChecked);

    private void ComponentsLink_Click(Microsoft.UI.Xaml.Documents.Hyperlink sender, Microsoft.UI.Xaml.Documents.HyperlinkClickEventArgs args) =>
        ShowComponents(!AppServices.Settings.ShowComponents);

    private void ShowComponents(bool show)
    {
        AppServices.Settings.ShowComponents = show;
        AppServices.Settings.Save();
        Motion.Enter(ListHost);
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

    // ---- Opening an app ----

    /// <summary>The app's page slides in from the right, and back out the same way.</summary>
    private void Open(AppRowViewModel row) => Frame.Navigate(
        typeof(AppDetailPage), row.Id,
        new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight });

    private void AppRow_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AppRowViewModel row)
            Open(row);
    }

    private void AppTile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AppRowViewModel row)
            Open(row);
    }

    // ---- Right-click menu ----

    private AppRowViewModel? _menuRow;

    /// <summary>Right-click, Shift+F10 or press-and-hold anywhere on a row of the list.</summary>
    private void AppList_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        var source = args.OriginalSource as DependencyObject;
        while (source is not null and not ListViewItem)
            source = VisualTreeHelper.GetParent(source);
        if (source is ListViewItem container && AppList.ItemFromContainer(container) is AppRowViewModel row)
            ShowMenu(container, row, args);
    }

    private void AppTile_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: AppRowViewModel row } element)
            ShowMenu(element, row, args);
    }

    /// <summary>One menu serves every app, so it is pointed at this one and then shown where the pointer is.</summary>
    private void ShowMenu(FrameworkElement element, AppRowViewModel row, ContextRequestedEventArgs args)
    {
        if (Resources["AppMenu"] is not MenuFlyout menu)
            return;

        _menuRow = row;
        foreach (var item in menu.Items.OfType<MenuFlyoutItem>())
        {
            switch (item.Tag)
            {
                case "toggle":
                    item.Text = row.ToggleAllLabel;
                    item.IsEnabled = row.CanToggleAll;
                    break;
                case "end":
                    item.IsEnabled = RunningActions.CanEnd(AppServices.Monitor.UsageOf(row.Id));
                    break;
                case "file":
                    item.IsEnabled = RunningActions.LocationOf(row.Id) is not null;
                    break;
                case "uninstall":
                    item.IsEnabled = UninstallActions.CanUninstall(row.Id) && !UninstallActions.IsUnderway(row.Id);
                    break;
            }
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
        if (_menuRow?.Entry.Group is { } group)
            ItemActions.ToggleAll(group);
    }

    private async void AppEnd_Click(object sender, RoutedEventArgs e)
    {
        if (_menuRow is { } row)
            await RunningActions.EndAppAsync(this, row.Id);
    }

    private void AppReveal_Click(object sender, RoutedEventArgs e)
    {
        if (_menuRow is { } row)
            ItemActions.Reveal(RunningActions.LocationOf(row.Id));
    }

    private async void AppUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (_menuRow is { } row)
            await UninstallActions.UninstallAsync(this, row.Id);
    }

    private void AppCopyName_Click(object sender, RoutedEventArgs e) => ItemActions.Copy(_menuRow?.Name);
}
