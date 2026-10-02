using Dashio.App.Controls;
using Dashio.App.Services;
using Dashio.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace Dashio.App.Pages;

public sealed partial class OverviewPage : Page
{
    // Widths in effective pixels of the page itself, so the layout is right at any display scale.
    private const double SidePanelMinWidth = 1280;
    private const double TwoColumnMinWidth = 820;
    private const double TilesInARowMinWidth = 760;

    public OverviewViewModel ViewModel { get; } = new();

    public OverviewPage()
    {
        InitializeComponent();

        // The page is cached, so these subscriptions live as long as the window.
        AppServices.State.Changed += (_, _) => ViewModel.RebuildStartup();
        AppServices.Settings.Changed += (_, _) => ViewModel.RebuildStartup();
        AppServices.Monitor.Updated += (_, _) => ShowReading();

        ViewModel.RebuildStartup();
        ShowReading();

        SizeChanged += (_, e) =>
        {
            Ui.FitToPage(Shell, e.NewSize.Width);
            ApplyLayout(e.NewSize.Width);
        };
    }

    /// <summary>A paused monitor still measures once when the page is opened.</summary>
    protected override void OnNavigatedTo(NavigationEventArgs e) => AppServices.Monitor.RefreshNow();

    private void ShowReading()
    {
        ViewModel.Update();

        var now = DateTimeOffset.Now;
        var history = AppServices.Monitor.History;
        var seconds = AppServices.Settings.RefreshSeconds;
        TimeSpan? interval = seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
        MemoryChart.Show(history.Select(p => ((now - p.Time).TotalSeconds, p.MemoryPercent)).ToList(), interval);
        CpuChart.Show(history.Select(p => ((now - p.Time).TotalSeconds, p.CpuPercent)).ToList(), interval);
    }

    /// <summary>The side panels sit beside the main column on wide pages and under it otherwise.</summary>
    private void ApplyLayout(double width)
    {
        var wide = width >= SidePanelMinWidth;
        var twoColumns = width >= TwoColumnMinWidth;

        Grid.SetRow(SidePanel, wide ? 0 : 1);
        Grid.SetColumn(SidePanel, wide ? 1 : 0);
        SidePanel.Width = wide ? (double)Application.Current.Resources["SidePanelWidth"] : double.NaN;
        Ui.Flow(SidePanel, sideBySide: !wide && twoColumns);

        Ui.Flow(MeterGrid, twoColumns);
        Ui.Flow(TopGrid, twoColumns);
        Ui.Flow(TileGrid, width >= TilesInARowMinWidth);
    }

    private static SlideNavigationTransitionInfo FromRight => new() { Effect = SlideNavigationTransitionEffect.FromRight };

    private void Open(UsageRowViewModel row) => Frame.Navigate(typeof(AppDetailPage), row.GroupId, FromRight);

    private void UsageRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is UsageRowViewModel row)
            Open(row);
    }

    // ---- Right-click menu ----

    private UsageRowViewModel? _menuRow;

    private void UsageRow_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: UsageRowViewModel row } element ||
            Resources["UsageMenu"] is not MenuFlyout menu)
        {
            return;
        }

        _menuRow = row;
        var group = AppServices.State.FindGroup(row.GroupId);
        var canToggle = group is not null && group.Items.Any(i => !i.Item.IsProtected && !i.Item.IsHiddenTask);
        foreach (var item in menu.Items.OfType<MenuFlyoutItem>())
        {
            switch (item.Tag)
            {
                case "file":
                    item.IsEnabled = row.MainPath is not null;
                    break;
                case "end":
                    item.IsEnabled = RunningActions.CanEnd(AppServices.Monitor.UsageOf(row.GroupId));
                    break;
                case "toggle":
                    item.IsEnabled = canToggle;
                    item.Text = canToggle && !ItemActions.AnyOn(group!)
                        ? "Turn everything it starts back on"
                        : "Turn off everything it starts";
                    break;
            }
        }

        if (args.TryGetPosition(element, out var point))
            menu.ShowAt(element, new FlyoutShowOptions { Position = point });
        else
            menu.ShowAt(element);
        args.Handled = true;
    }

    private void UsageOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_menuRow is { } row)
            Open(row);
    }

    private void UsageReveal_Click(object sender, RoutedEventArgs e) => ItemActions.Reveal(_menuRow?.MainPath);

    private async void UsageEnd_Click(object sender, RoutedEventArgs e)
    {
        if (_menuRow is { } row)
            await RunningActions.EndAppAsync(this, row.GroupId);
    }

    private void UsageCopyName_Click(object sender, RoutedEventArgs e) => ItemActions.Copy(_menuRow?.Name);

    private void UsageToggleAll_Click(object sender, RoutedEventArgs e)
    {
        if (_menuRow is { } row && AppServices.State.FindGroup(row.GroupId) is { } group)
            ItemActions.ToggleAll(group);
    }

    private void AtStartupTile_Click(object sender, RoutedEventArgs e) => OpenApps(AppFilter.AtStartup);

    private void HiddenTile_Click(object sender, RoutedEventArgs e) => OpenApps(AppFilter.NotInTaskManager);

    private void RunningTile_Click(object sender, RoutedEventArgs e) => OpenApps(AppFilter.All);

    private void OpenApps(AppFilter filter) => Frame.Navigate(typeof(AppsPage), filter, FromRight);

    private void OpenHistory_Click(object sender, RoutedEventArgs e) =>
        Frame.Navigate(typeof(HistoryPage), null, new EntranceNavigationTransitionInfo());

    private void OpenSettings_Click(object sender, RoutedEventArgs e) =>
        Frame.Navigate(typeof(SettingsPage), null, new EntranceNavigationTransitionInfo());
}
