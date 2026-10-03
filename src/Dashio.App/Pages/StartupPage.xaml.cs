using Dashio.App.Services;
using Dashio.App.ViewModels;
using Dashio.Core.Journal;
using Dashio.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace Dashio.App.Pages;

public sealed partial class StartupPage : Page, IFilterPage
{
    // Widths in effective pixels of the page itself, so the layout is right at any display scale.
    private const double StartsMinWidth = 960;
    private const double TypeMinWidth = 800;
    private const double FilterBesideTitleMinWidth = 700;
    private const double TabSlide = 48;

    private bool _isShown;

    public StartupViewModel ViewModel { get; } = new();

    public StartupPage()
    {
        InitializeComponent();

        // The page is cached, so these subscriptions live as long as the window.
        AppServices.State.Changed += (_, _) => Rebuild();
        AppServices.Pending.Changed += (_, _) => ViewModel.RefreshPending();
        AppServices.Monitor.Updated += (_, _) =>
        {
            if (_isShown)
                ViewModel.UpdateUsage();
        };
        AppServices.Settings.Changed += (_, _) =>
        {
            ShowWindowsItem.IsChecked = AppServices.Settings.ShowWindowsComponents;
            Rebuild();
        };

        ShowWindowsItem.IsChecked = AppServices.Settings.ShowWindowsComponents;
        Rebuild();
        UpdateHeaders();

        SizeChanged += (_, e) =>
        {
            var width = e.NewSize.Width;
            Ui.FitToPage(Shell, width);
            ViewModel.Columns.ShowStarts = width >= StartsMinWidth;
            ViewModel.Columns.ShowType = width >= TypeMinWidth;

            var beside = width >= FilterBesideTitleMinWidth;
            Grid.SetRow(FilterBox, beside ? 0 : 1);
            Grid.SetColumn(FilterBox, beside ? 1 : 0);
            FilterBox.HorizontalAlignment = beside ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        };
    }

    /// <summary>Overview and the search box open this page on a kind of item, or filtered to a name.</summary>
    protected override void OnNavigatedFrom(NavigationEventArgs e) => _isShown = false;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _isShown = true;
        ViewModel.UpdateUsage();
        if (e.Parameter is not StartupTarget target || e.NavigationMode != NavigationMode.New)
            return;

        var search = target.Search ?? "";
        if (FilterBox.Text != search)
            FilterBox.Text = search;
        ViewModel.Search = search;
        ViewModel.Kind = target.Kind;
        ViewModel.NewOnly = target.NewOnly;
        NewToggle.IsChecked = target.NewOnly;
        Rebuild();

        // The tab bar ignores a selection made before it has been laid out, so this waits its turn.
        void Select() => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            var tab = Tabs.First(t => KindOf(t) == target.Kind);
            tab.IsSelected = true;
            KindBar.SelectedItem = tab;
        });

        if (KindBar.IsLoaded)
        {
            Select();
            return;
        }

        void OnLoaded(object sender, RoutedEventArgs args)
        {
            KindBar.Loaded -= OnLoaded;
            Select();
        }
        KindBar.Loaded += OnLoaded;
    }

    public void FocusFilter() => FilterBox.Focus(FocusState.Keyboard);

    private IEnumerable<SelectorBarItem> Tabs =>
        [KindAll, KindService, KindScheduledTask, KindRunKey, KindStartupFolder, KindPackagedStartupTask];

    private static AutostartKind? KindOf(SelectorBarItem tab) =>
        Enum.TryParse<AutostartKind>(tab.Tag as string, out var kind) ? kind : null;

    private static string TabName(SelectorBarItem tab) => KindOf(tab) switch
    {
        null => "All",
        AutostartKind.PackagedStartupTask => "Store apps",
        { } kind => ItemText.KindHeading(kind),
    };

    private void Rebuild()
    {
        ViewModel.Rebuild();
        foreach (var tab in Tabs)
            tab.Text = $"{TabName(tab)} ({ViewModel.Counts.GetValueOrDefault(tab.Tag as string ?? "")})";
    }

    private void FilterBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        var text = sender.Text.Trim();
        if (text == ViewModel.Search)
            return;
        ViewModel.Search = text;
        Rebuild();
    }

    /// <summary>The table comes in from the side the chosen tab is on.</summary>
    private void KindBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is not { } tab)
            return;
        var kind = KindOf(tab);
        if (kind == ViewModel.Kind)
            return;

        var tabs = Tabs.ToList();
        var toTheRight = tabs.IndexOf(tab) > tabs.FindIndex(t => KindOf(t) == ViewModel.Kind);
        ViewModel.Kind = kind;
        Rebuild();
        Motion.EnterSideways(ItemList, toTheRight ? TabSlide : -TabSlide);
    }

    private void NewToggle_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.NewOnly = NewToggle.IsChecked == true;
        Rebuild();
        Motion.Enter(ItemList);
    }

    private void ShowWindows_Click(object sender, RoutedEventArgs e)
    {
        AppServices.Settings.ShowWindowsComponents = ShowWindowsItem.IsChecked;
        AppServices.Settings.Save();
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
        // Memory starts with the most, so its arrow points the other way from the text columns.
        var arrow = ViewModel.SortDescending != (ViewModel.SortColumn == ItemColumn.Memory) ? "↓" : "↑";
        foreach (var (button, column, label) in new[]
                 {
                     (NameHeader, ItemColumn.Name, "Name"),
                     (TypeHeader, ItemColumn.Type, "Type"),
                     (AppHeader, ItemColumn.App, "App"),
                     (StartsHeader, ItemColumn.Starts, "Starts"),
                     (MemoryHeader, ItemColumn.Memory, "Memory"),
                     (StateHeader, ItemColumn.State, "Enabled"),
                 })
        {
            button.Content = ViewModel.SortColumn == column ? $"{label}  {arrow}" : label;
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
                case "stop":
                    item.Visibility = row.Item.Kind == AutostartKind.Service ? Visibility.Visible : Visibility.Collapsed;
                    item.IsEnabled = row.CanStopNow;
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

    private async void StopItem_Click(object sender, RoutedEventArgs e)
    {
        if (_menuRow is { } row)
            await ChangeRunner.ApplyAsync([ChangePlanner.StopNow(row.Item, row.AppName)]);
    }

    private void CopyName_Click(object sender, RoutedEventArgs e) => ItemActions.Copy(_menuRow?.Name);

    private void CopyCommand_Click(object sender, RoutedEventArgs e) => ItemActions.Copy(_menuRow?.Item.Command);

    private void RevealFile_Click(object sender, RoutedEventArgs e) => ItemActions.Reveal(_menuRow?.Item.TargetPath);
}
