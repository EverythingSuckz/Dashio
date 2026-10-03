using Dashio.App.Services;
using Dashio.App.ViewModels;
using Dashio.Core.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace Dashio.App.Pages;

public sealed partial class StoragePage : Page
{
    // Sizes in effective pixels of the page itself, so the layout is right at any display scale.
    private const double MapMinHeight = 620;
    private const double MapBesideListMinWidth = 1200;
    private const double BarBesideMapMinWidth = 1400;

    /// <summary>How often the figures follow a scan that is still running.</summary>
    private static readonly TimeSpan ScanTick = TimeSpan.FromSeconds(1);

    private readonly DispatcherTimer _followScan = new() { Interval = ScanTick };
    private bool _isShown;

    public StorageViewModel ViewModel { get; } = new();

    public StoragePage()
    {
        InitializeComponent();

        // The page is cached, so these subscriptions live as long as the window.
        AppServices.Disk.Finished += async (_, _) =>
        {
            ViewModel.RefreshDrives();
            RefreshKeepingTop(resort: true);
            if (ViewModel.ShowingFiles)
                await ViewModel.RefreshLargeFilesAsync();
        };
        _followScan.Tick += (_, _) => FollowScan();
        ViewModel.MapChanged += (_, _) => ShowMap();

        ViewModel.RefreshDrives();
        UpdateHeaders();
        AdminCheck.IsChecked = AppServices.Settings.ReadDrivesAsAdmin;

        SizeChanged += (_, e) =>
        {
            Ui.FitToPage(Shell, e.NewSize.Width);
            PlaceMap(e.NewSize.Width, e.NewSize.Height);
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _isShown = true;
        ViewModel.RefreshDrives();
        ViewModel.RefreshFolder(resort: false);
        ShowMap();
        FollowScan();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _isShown = false;
        _followScan.Stop();
    }

    /// <summary>The map goes beside the list when the page is wide, above it otherwise, and away on a short window.</summary>
    private void PlaceMap(double width, double height)
    {
        var beside = width >= MapBesideListMinWidth;
        MapColumn.Width = beside ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
        ListColumn.Width = new GridLength(beside ? 3 : 1, GridUnitType.Star);

        Grid.SetRow(MapPanel, beside ? 5 : 4);
        Grid.SetRowSpan(MapPanel, beside ? 2 : 1);
        Grid.SetColumnSpan(MapPanel, beside ? 1 : 2);
        MapPanel.Height = beside ? double.NaN : 168;
        MapPanel.Margin = beside ? new Thickness(36, 0, 0, 8) : new Thickness(36, 0, 36, 8);
        // On a short window the list matters more than the map.
        MapPanel.MaxHeight = beside || height >= MapMinHeight ? double.PositiveInfinity : 0;

        foreach (var element in new FrameworkElement[] { FolderHeader, FolderList })
        {
            Grid.SetColumn(element, beside ? 1 : 0);
            Grid.SetColumnSpan(element, beside ? 1 : 2);
        }
        FolderHeader.Margin = beside ? new Thickness(16, 0, 36, 0) : new Thickness(36, 0, 36, 0);
        FolderList.Padding = beside ? new Thickness(16, 4, 36, 8) : new Thickness(36, 4, 36, 8);
        ViewModel.Columns.ShowBar = !beside || width >= BarBesideMapMinWidth;
    }

    // ---- Drives ----

    private void Drive_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DriveViewModel drive })
            return;
        ViewModel.Select(drive);
        ShowMap();
        FollowScan();
    }

    /// <summary>How much room each app takes is a column of the Apps page.</summary>
    private void AppsBySize_Click(object sender, RoutedEventArgs e) => Frame.Navigate(
        typeof(AppsPage), AppColumn.Size,
        new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight });

    private const double TabSlide = 48;

    private async void ViewBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var files = sender.SelectedItem == ViewFiles;
        if (files == ViewModel.ShowingFiles)
            return;
        ViewModel.ShowingFiles = files;
        Motion.EnterSideways(files ? FileList : FolderList, files ? TabSlide : -TabSlide);
        if (files)
            await ViewModel.RefreshLargeFilesAsync();
    }

    // ---- Reading a drive ----

    private const string AdminReadNote = "Much faster, and includes the folders Windows protects.";

    private void AdminCheck_Click(object sender, RoutedEventArgs e) => RememberAdminChoice(AdminCheck.IsChecked == true);

    private async void StartScan_Click(object sender, RoutedEventArgs e) => await ReadAsync(AdminCheck.IsChecked == true);

    /// <summary>Reading again replaces what is showing, so it asks first, and offers the faster way.</summary>
    private async void ReadAgain_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedDrive is not { } drive)
            return;

        var resources = Application.Current.Resources;
        var asAdmin = new CheckBox
        {
            Content = "Scan as administrator",
            IsChecked = AppServices.Settings.ReadDrivesAsAdmin,
            MinHeight = 0,
            Padding = new Thickness(8, 0, 0, 0),
        };
        AutomationProperties.SetAutomationId(asAdmin, "StorageAgainAdminCheck");
        var choice = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        choice.Children.Add(asAdmin);
        choice.Children.Add(new TextBlock
        {
            Text = AdminReadNote,
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)resources["CaptionTextBlockStyle"],
            Foreground = (Brush)resources["TextFillColorSecondaryBrush"],
        });

        var confirmed = await ConfirmDialog.ShowAsync(
            this, $"Scan {drive.Title} again?", ViewModel.ScanStatus, null,
            [new ConfirmDialog.Point("\uE72C", "Every folder is scanned again", "The figures showing now are replaced.")],
            "Scan again", "\uE72C", extra: ConfirmDialog.Row("\uEA18", choice), headerGlyph: "\uEDA2", primaryIsDefault: true);
        if (confirmed)
            await ReadAsync(asAdmin.IsChecked == true);
    }

    private void RememberAdminChoice(bool asAdmin)
    {
        AdminCheck.IsChecked = asAdmin;
        if (AppServices.Settings.ReadDrivesAsAdmin == asAdmin)
            return;
        AppServices.Settings.ReadDrivesAsAdmin = asAdmin;
        AppServices.Settings.Save();
    }

    private async Task ReadAsync(bool asAdmin)
    {
        RememberAdminChoice(asAdmin);
        if (!asAdmin)
        {
            ViewModel.StartScan();
            ShowMap();
            FollowScan();
            return;
        }

        var error = await ViewModel.ReadAsAdminAsync();
        if (error is not null)
            AppServices.Shell.Notify(new Notice(InfoBarSeverity.Error, "The drive could not be scanned", error));
    }

    /// <summary>While a drive is being read the figures are brought up to date every second.</summary>
    private void FollowScan()
    {
        if (!_isShown || !ViewModel.IsScanning)
        {
            _followScan.Stop();
            return;
        }
        RefreshKeepingTop(resort: false);
        _followScan.Start();
    }

    private ScrollViewer? _listScroller;

    /// <summary>
    /// Brings the figures up to date. A list keeps the lines it is showing in view, so a line that
    /// moves to the top would land above them, out of sight: if the list was at its top it stays there.
    /// </summary>
    private void RefreshKeepingTop(bool resort)
    {
        _listScroller ??= FindScroller(FolderList);
        var wasAtTop = _listScroller is null || _listScroller.VerticalOffset < 1;
        ViewModel.RefreshFolder(resort);
        ShowMap();
        if (wasAtTop)
        {
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => _listScroller?.ChangeView(null, 0, null, disableAnimation: true));
        }
    }

    private static ScrollViewer? FindScroller(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer scroller)
                return scroller;
            if (FindScroller(child) is { } found)
                return found;
        }
        return null;
    }

    /// <summary>The layout needs the weights in the order of the tiles.</summary>
    private void ShowMap() => MapLayout.Weights = ViewModel.Tiles.Select(t => (double)t.Bytes).ToList();

    // ---- Moving through the folders ----

    private void OpenFolder(FolderNode? folder)
    {
        if (folder is null)
            return;
        ViewModel.Open(folder);
        ShowMap();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.GoBack();
        ShowMap();
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.GoForward();
        ShowMap();
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.GoUp();
        ShowMap();
    }

    private void FolderRow_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FolderEntryViewModel { IsFolder: true } row)
            OpenFolder(row.Node);
    }

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: FolderEntryViewModel { IsFolder: true } tile })
            OpenFolder(tile.Node);
    }

    private void PathBar_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Item is Crumb crumb)
            OpenFolder(crumb.Folder);
    }

    private void FolderHeader_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag && Enum.TryParse<FolderSort>(tag, out var sort))
        {
            ViewModel.SortFoldersBy(sort);
            UpdateHeaders();
        }
    }

    /// <summary>Shows an arrow on the column the list is sorted by.</summary>
    private void UpdateHeaders()
    {
        // Names start from A; sizes and counts start with the most.
        var startsDescending = ViewModel.FolderSort != FolderSort.Name;
        var arrow = startsDescending != ViewModel.FolderSortReversed ? "↓" : "↑";
        FolderNameHeader.Content = ViewModel.FolderSort == FolderSort.Name ? $"Name  {arrow}" : "Name";
        FolderSizeHeader.Content = ViewModel.FolderSort == FolderSort.Size ? $"Size  {arrow}" : "Size";
        FolderFilesHeader.Content = ViewModel.FolderSort == FolderSort.Files ? $"Files  {arrow}" : "Files";
    }

    private static void Reveal(FolderEntryViewModel entry)
    {
        if (entry.IsFolder)
            ItemActions.OpenFolder(entry.Path);
        else
            ItemActions.Reveal(entry.Path);
    }

    private void RevealFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: FolderEntryViewModel entry })
            Reveal(entry);
    }

    // ---- Right-click menu, the same on a line of the list and on a tile of the map ----

    private FolderEntryViewModel? _menuEntry;

    private void FolderList_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        var source = args.OriginalSource as DependencyObject;
        while (source is not null and not ListViewItem)
            source = VisualTreeHelper.GetParent(source);
        if (source is ListViewItem container && FolderList.ItemFromContainer(container) is FolderEntryViewModel entry)
            ShowMenu(container, entry, args);
    }

    private void Tile_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: FolderEntryViewModel entry } element)
            ShowMenu(element, entry, args);
    }

    private void ShowMenu(FrameworkElement element, FolderEntryViewModel entry, ContextRequestedEventArgs args)
    {
        if (Resources["FolderMenu"] is not MenuFlyout menu)
            return;

        _menuEntry = entry;
        foreach (var item in menu.Items.OfType<MenuFlyoutItem>())
        {
            item.IsEnabled = item.Tag switch
            {
                "open" => entry.IsFolder,
                "delete" => entry.HasPath && ViewModel.WhyNotDelete(entry.Path) is null,
                _ => entry.HasPath,
            };
        }

        if (args.TryGetPosition(element, out var point))
            menu.ShowAt(element, new FlyoutShowOptions { Position = point });
        else
            menu.ShowAt(element);
        args.Handled = true;
    }

    private void RevealFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: LargeFileRow file })
            ItemActions.Reveal(file.Path);
    }

    /// <summary>A file has the same menu as a line of the folder list, without "Look inside".</summary>
    private void FileList_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        var source = args.OriginalSource as DependencyObject;
        while (source is not null and not ListViewItem)
            source = VisualTreeHelper.GetParent(source);
        if (source is not ListViewItem container || FileList.ItemFromContainer(container) is not LargeFileRow file)
            return;

        var entry = new FolderEntryViewModel($"file:{file.Path}", FolderRowKind.File, file.Path, null, ViewModel.Columns)
        {
            SizeText = file.SizeText,
        };
        ShowMenu(container, entry, args);
    }

    private void FolderOpen_Click(object sender, RoutedEventArgs e) => OpenFolder(_menuEntry?.Node);

    private void FolderReveal_Click(object sender, RoutedEventArgs e)
    {
        if (_menuEntry is { } entry)
            Reveal(entry);
    }

    private void FolderCopyPath_Click(object sender, RoutedEventArgs e) => ItemActions.Copy(_menuEntry?.Path);

    /// <summary>Asks first. The Recycle Bin is the default, so a mistake can be put right.</summary>
    private async void FolderDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_menuEntry is not { Path: { } path } entry || ViewModel.WhyNotDelete(path) is not null)
            return;

        var name = System.IO.Path.GetFileName(path.TrimEnd('\\'));
        var resources = Application.Current.Resources;
        var forGood = new CheckBox { Content = "Delete permanently", MinHeight = 0, Padding = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetAutomationId(forGood, "StorageDeleteForGood");
        var choice = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        choice.Children.Add(forGood);
        choice.Children.Add(new TextBlock
        {
            Text = "Skips the Recycle Bin, so it cannot be put back.",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)resources["CaptionTextBlockStyle"],
            Foreground = (Brush)resources["TextFillColorSecondaryBrush"],
        });

        var points = new List<ConfirmDialog.Point>
        {
            entry.IsFolder
                ? new("\uE8B7", "The folder and everything in it", path)
                : new("\uE7C3", "This file", path),
            new("\uE74D", "It goes to the Recycle Bin", "Put it back from there if you need it again."),
        };
        var confirmed = await ConfirmDialog.ShowAsync(
            this, $"Delete {name}?", entry.SizeText, entry.IsFolder ? null : path, points, "Delete", "\uE74D",
            extra: ConfirmDialog.Row("\uE7BA", choice, isWarning: true), headerGlyph: entry.IsFolder ? "\uE8B7" : "\uE7C3");
        if (!confirmed)
            return;

        var permanently = forGood.IsChecked == true;
        var error = await ViewModel.DeleteAsync(path, permanently);
        ShowMap();
        AppServices.Shell.Notify(error is null
            ? new Notice(InfoBarSeverity.Success, permanently ? $"Deleted {name}" : $"Moved {name} to the Recycle Bin", "")
            : new Notice(InfoBarSeverity.Error, $"{name} could not be deleted", error));
    }
}
