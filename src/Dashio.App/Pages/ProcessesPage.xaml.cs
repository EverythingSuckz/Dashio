using Dashio.App.Services;
using Dashio.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace Dashio.App.Pages;

public sealed partial class ProcessesPage : Page, IFilterPage
{
    // Widths in effective pixels of the page itself, so the layout is right at any display scale.
    private const double StatsBesideTitleMinWidth = 1040;
    private const double StatsInARowMinWidth = 700;

    private bool _isShown;

    public ProcessesViewModel ViewModel { get; } = new();

    public ProcessesPage()
    {
        InitializeComponent();

        // The page is cached, so this subscription lives as long as the window.
        AppServices.Monitor.Updated += (_, _) =>
        {
            if (_isShown)
                ViewModel.Update();
        };
        UpdateHeaders();

        SizeChanged += (_, e) =>
        {
            Ui.FitToPage(Shell, e.NewSize.Width);
            Ui.PlaceStats(StatsPanel, e.NewSize.Width, StatsBesideTitleMinWidth, StatsInARowMinWidth);
        };
    }

    /// <summary>The search box opens this page with the name of a program to show.</summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _isShown = true;
        if (e.NavigationMode == NavigationMode.New)
        {
            var filter = e.Parameter as string ?? "";
            if (FilterBox.Text != filter)
                FilterBox.Text = filter;
            ViewModel.Search = filter;
        }

        // The order is brought up to date each time the page is opened, then left alone.
        ViewModel.Rebuild();
        // A paused monitor still measures once when the page is opened.
        AppServices.Monitor.RefreshNow();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => _isShown = false;

    public void FocusFilter() => FilterBox.Focus(FocusState.Keyboard);

    private void FilterBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        var text = sender.Text.Trim();
        if (text == ViewModel.Search)
            return;
        ViewModel.Search = text;
        ViewModel.Rebuild();
    }

    private void CollapseAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var group in ViewModel.Rows.OfType<ProcessGroupViewModel>().Where(g => g.IsExpanded).ToList())
            ViewModel.Toggle(group);
    }

    private void Header_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag && Enum.TryParse<ProcessColumn>(tag, out var column))
        {
            ViewModel.SortBy(column);
            UpdateHeaders();
            Motion.Enter(ProcessList);
        }
    }

    /// <summary>Shows an arrow on the column the list is sorted by.</summary>
    private void UpdateHeaders()
    {
        var arrow = ViewModel.SortDescending ? "↓" : "↑";
        foreach (var (button, column, label) in new[]
                 {
                     (NameHeader, ProcessColumn.Name, "App"),
                     (CountHeader, ProcessColumn.Count, "Processes"),
                     (MemoryHeader, ProcessColumn.Memory, "Memory"),
                     (CpuHeader, ProcessColumn.Cpu, "Processor"),
                 })
        {
            button.Content = ViewModel.SortColumn == column ? $"{label}  {arrow}" : label;
        }
    }

    private void Row_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ProcessGroupViewModel group)
            ViewModel.Toggle(group);
    }

    private void OpenApp(string groupId) => Frame.Navigate(
        typeof(AppDetailPage), groupId,
        new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight });

    private void OpenGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ProcessGroupViewModel group })
            OpenApp(group.Id);
    }

    private async void EndGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ProcessGroupViewModel group })
            await RunningActions.EndAppAsync(this, group.Id);
    }

    private async void EndProcess_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ProcessRowViewModel row })
            await RunningActions.EndProcessAsync(this, row.GroupId, row.Usage);
    }

    private void RevealProcess_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ProcessRowViewModel row })
            ItemActions.Reveal(row.Path);
    }

    // ---- Right-click menu ----

    private object? _menuRow;

    /// <summary>Points the shared menu at the row it was opened on: an app or one of its processes.</summary>
    private void RowMenu_Opening(object? sender, object e)
    {
        if (sender is not MenuFlyout menu)
            return;
        _menuRow = (menu.Target as ContentControl)?.Content;
        var group = _menuRow as ProcessGroupViewModel;
        var process = _menuRow as ProcessRowViewModel;
        if (group is null && process is null)
        {
            menu.Hide();
            return;
        }

        foreach (var item in menu.Items.OfType<MenuFlyoutItem>())
        {
            switch (item.Tag)
            {
                case "end":
                    item.Text = group is not null ? "End app" : "End process";
                    item.IsEnabled = group?.CanEnd ?? process!.CanEnd;
                    break;
                case "file":
                    item.IsEnabled = group is not null
                        ? RunningActions.LocationOf(group.Id) is not null
                        : process!.HasPath;
                    break;
            }
        }
    }

    private void MenuOpenApp_Click(object sender, RoutedEventArgs e)
    {
        var id = (_menuRow as ProcessGroupViewModel)?.Id ?? (_menuRow as ProcessRowViewModel)?.GroupId;
        if (id is not null)
            OpenApp(id);
    }

    private async void MenuEnd_Click(object sender, RoutedEventArgs e)
    {
        if (_menuRow is ProcessGroupViewModel group)
            await RunningActions.EndAppAsync(this, group.Id);
        else if (_menuRow is ProcessRowViewModel row)
            await RunningActions.EndProcessAsync(this, row.GroupId, row.Usage);
    }

    private void MenuReveal_Click(object sender, RoutedEventArgs e) => ItemActions.Reveal(
        _menuRow is ProcessGroupViewModel group ? RunningActions.LocationOf(group.Id) : (_menuRow as ProcessRowViewModel)?.Path);

    private void MenuCopyName_Click(object sender, RoutedEventArgs e) =>
        ItemActions.Copy((_menuRow as ProcessGroupViewModel)?.Name ?? (_menuRow as ProcessRowViewModel)?.Name);
}

/// <summary>Picks the app or the process template for a row of the flat list.</summary>
public sealed partial class ProcessRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? GroupTemplate { get; set; }
    public DataTemplate? ProcessTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item is ProcessGroupViewModel ? GroupTemplate : ProcessTemplate;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}
