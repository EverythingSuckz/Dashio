using System.Runtime.InteropServices;
using Dashio.App.Pages;
using Dashio.App.Services;
using Dashio.App.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Windows.Graphics;

namespace Dashio.App;

public sealed partial class MainWindow : Window
{
    // Multi-pane layout: navigation plus a content column of up to 1040.
    private const int DefaultWidth = 1240;
    private const int DefaultHeight = 820;
    private const int MinimumWidth = 640;
    private const int MinimumHeight = 480;

    private Guid? _noticeUndoBatch;
    private bool _syncingSelection;

    public ScanState State => AppServices.State;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        SizeWindow();
        ApplyTheme();

        AppServices.Settings.Changed += (_, _) => ApplyTheme();
        AppServices.Pending.Changed += (_, _) => UpdatePendingBar();
        AppServices.State.Changed += (_, _) => ShowScanErrors();
        AppServices.Shell.Noticed += (_, notice) => ShowNotice(notice);
        ChangeRunner.BusyChanged += (_, busy) => ShowBusy(busy);
        NavFrame.Navigated += NavFrame_Navigated;
        NavFrame.SizeChanged += (_, e) => Ui.FitToPage(BannerPanel, e.NewSize.Width);

        AppServices.Monitor.Start(DispatcherQueue);
        AppServices.Inventory.Start(DispatcherQueue);
        VisibilityChanged += (_, e) => UpdateMonitorVisibility(e.Visible);
        AppWindow.Changed += (_, _) =>
        {
            // Minimising is reported in different ways on different builds, so any change is checked.
            UpdateMonitorVisibility(AppWindow.IsVisible);
        };

        NavView.SelectedItem = OverviewItem;
        _ = AppServices.State.RefreshAsync();
    }

    /// <summary>Nothing is measured while the window is hidden or minimised.</summary>
    private void UpdateMonitorVisibility(bool visible)
    {
        var minimised = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        AppServices.Monitor.SetVisible(visible && !minimised);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    private void SizeWindow()
    {
        // AppWindow.Resize takes physical pixels.
        var scale = GetDpiForWindow(Win32Interop.GetWindowFromWindowId(AppWindow.Id)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(DefaultWidth * scale), (int)(DefaultHeight * scale)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(MinimumWidth * scale);
            presenter.PreferredMinimumHeight = (int)(MinimumHeight * scale);
        }
    }

    private void ApplyTheme()
    {
        var theme = AppServices.Settings.Theme;
        RootGrid.RequestedTheme = theme;
        AppWindow.TitleBar.PreferredTheme = theme switch
        {
            ElementTheme.Light => TitleBarTheme.Light,
            ElementTheme.Dark => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
    }

    // ---- Navigation ----

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var page = args.IsSettingsSelected ? typeof(SettingsPage) : PageOf(args.SelectedItem);
        // The frame's own navigation also moves the selection; that must not navigate again.
        if (_syncingSelection || page is null || NavFrame.CurrentSourcePageType == page)
            return;
        NavigateTop(page);
    }

    private static Type? PageOf(object? item) => (item as NavigationViewItem)?.Tag switch
    {
        "overview" => typeof(OverviewPage),
        "apps" => typeof(AppsPage),
        "installed" => typeof(InstalledPage),
        "items" => typeof(AllItemsPage),
        "history" => typeof(HistoryPage),
        _ => null,
    };

    /// <summary>
    /// An app's page keeps the item it was opened from selected, so pressing that item again
    /// changes no selection and has to be handled as a click.
    /// </summary>
    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (NavFrame.CurrentSourcePageType == typeof(AppDetailPage) &&
            args.InvokedItemContainer == NavView.SelectedItem as NavigationViewItem &&
            PageOf(args.InvokedItemContainer) is { } page)
        {
            NavigateTop(page);
        }
    }

    private void NavigateTop(Type page)
    {
        NavFrame.Navigate(page, null, new EntranceNavigationTransitionInfo());
        NavFrame.BackStack.Clear();
    }

    private void NavFrame_Navigated(object sender, NavigationEventArgs e)
    {
        var page = e.SourcePageType;
        _syncingSelection = true;
        try
        {
            if (page == typeof(SettingsPage))
                NavView.SelectedItem = NavView.SettingsItem;
            else if (page == typeof(OverviewPage))
                NavView.SelectedItem = OverviewItem;
            else if (page == typeof(InstalledPage))
                NavView.SelectedItem = InstalledItem;
            else if (page == typeof(AllItemsPage))
                NavView.SelectedItem = AllItemsItem;
            else if (page == typeof(HistoryPage))
                NavView.SelectedItem = HistoryItem;
            else if (page == typeof(AppsPage))
                NavView.SelectedItem = AppsItem;
            // An app's page leaves the selection where it was: on the page it was opened from.
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void Home_Click(object sender, RoutedEventArgs e)
    {
        if (NavFrame.CurrentSourcePageType != typeof(OverviewPage))
            NavigateTop(typeof(OverviewPage));
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args) => NavView.IsPaneOpen = !NavView.IsPaneOpen;

    private void TitleBar_BackRequested(TitleBar sender, object args) => GoBack();

    private void Back_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        GoBack();
        args.Handled = true;
    }

    private void GoBack()
    {
        if (NavFrame.CanGoBack)
            NavFrame.GoBack();
    }

    // ---- Search and refresh ----

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        AppServices.Shell.SetSearch(sender.Text);
        var page = NavFrame.CurrentSourcePageType;
        if (sender.Text.Length > 0 && page != typeof(AppsPage) && page != typeof(AllItemsPage) && page != typeof(InstalledPage))
            NavigateTop(typeof(AppsPage));
    }

    private void Search_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SearchBox.Focus(FocusState.Keyboard);
        args.Handled = true;
    }

    private void Refresh_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        Refresh();
        args.Handled = true;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Refresh()
    {
        // Queued switches survive a rescan: applying re-reads each item from Windows anyway.
        NoticeBar.IsOpen = false;
        _ = AppServices.State.RefreshAsync();
    }

    private void ShowScanErrors()
    {
        var errors = AppServices.State.Errors;
        ErrorBar.Message = string.Join("\n", errors.Select(e => $"{e.Collector}: {e.Message}"));
        if (errors.Count > 0 && !ErrorBar.IsOpen)
        {
            _closingBars.Remove(ErrorBar);
            ErrorBar.IsOpen = true;
            Motion.Enter(ErrorBar, fromY: -12);
        }
        else if (errors.Count == 0)
        {
            ErrorBar.IsOpen = false;
        }
    }

    // ---- Banner ----

    private void ShowNotice(Notice notice)
    {
        NoticeBar.Severity = notice.Severity;
        NoticeBar.Title = notice.Title;
        NoticeBar.Message = notice.Message;
        _noticeUndoBatch = notice.UndoBatchId;
        NoticeUndoButton.Visibility = notice.UndoBatchId is null ? Visibility.Collapsed : Visibility.Visible;
        _closingBars.Remove(NoticeBar);     // A fade-out still running must not close the new notice.
        NoticeBar.IsOpen = true;
        Motion.Enter(NoticeBar, fromY: -12);
    }

    private void ShowBusy(bool busy)
    {
        if (busy)
        {
            NoticeBar.IsOpen = false;
            BusyBar.IsOpen = true;
            Motion.Enter(BusyBar, fromY: -12);
        }
        else
        {
            BusyBar.IsOpen = false;
        }
    }

    private readonly HashSet<InfoBar> _closingBars = [];

    /// <summary>Fades a banner out before it closes, so it does not just vanish.</summary>
    private async void Bar_Closing(InfoBar sender, InfoBarClosingEventArgs args)
    {
        if (!_closingBars.Add(sender))
            return;     // Second pass: the animation is done, let it close.

        args.Cancel = true;
        await Motion.ExitAsync(sender);
        if (!_closingBars.Contains(sender))
            return;     // Opened again in the meantime.

        sender.IsOpen = false;
        Motion.Reset(sender);
        _closingBars.Remove(sender);
    }

    private async void NoticeUndo_Click(object sender, RoutedEventArgs e)
    {
        if (_noticeUndoBatch is not { } batch)
            return;
        var entries = AppServices.State.Journal.Where(j => j.BatchId == batch).ToList();
        var (changes, _, _) = ChangeRunner.PlanUndo(entries);
        NoticeBar.IsOpen = false;
        await ChangeRunner.ApplyAsync(changes);
    }

    // ---- Pending changes ----

    private void UpdatePendingBar()
    {
        var pending = AppServices.Pending;
        PendingBar.Visibility = pending.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PendingTitle.Text = $"{ItemText.Plural(pending.Count, "change")} not applied yet";
        PendingDetail.Text = pending.NeedsAdmin
            ? "Nothing has changed on your PC so far. Applying will ask for administrator permission."
            : "Nothing has changed on your PC so far.";
    }

    private void Discard_Click(object sender, RoutedEventArgs e) => AppServices.Pending.Clear();

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var changes = AppServices.Pending.All;
        if (changes.Count == 0)
            return;

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.RequestedTheme,
            Title = $"Apply {ItemText.Plural(changes.Count, "change")}?",
            Content = ReviewContent(changes),
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        ApplyButton.IsEnabled = DiscardButton.IsEnabled = false;
        try
        {
            await ChangeRunner.ApplyAsync(changes);
        }
        finally
        {
            ApplyButton.IsEnabled = DiscardButton.IsEnabled = true;
        }
    }

    private static UIElement ReviewContent(IReadOnlyList<Dashio.Core.Changes.PlannedChange> changes)
    {
        var list = new StackPanel { Spacing = 12 };
        foreach (var change in changes)
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new FontIcon
            {
                Glyph = ItemText.KindGlyph(change.Item.Kind),
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 0, 0),
            });

            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = ItemText.Describe(change), TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new TextBlock
            {
                Text = $"{change.AppName} · {ItemText.KindName(change.Item.Kind)}",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap,
            });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            if (change.Item.RequiresAdmin)
            {
                var shield = new FontIcon { Glyph = "\uEA18", FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
                ToolTipService.SetToolTip(shield, "Needs administrator permission");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(shield, "Needs administrator permission");
                Grid.SetColumn(shield, 2);
                row.Children.Add(shield);
            }
            list.Children.Add(row);
        }

        var panel = new StackPanel { Spacing = 16, MinWidth = 360 };
        panel.Children.Add(new ScrollViewer { Content = list, MaxHeight = 320 });
        panel.Children.Add(new TextBlock
        {
            Text = changes.Any(c => c.Item.RequiresAdmin)
                ? "Windows will ask for administrator permission once. Every change can be undone from History."
                : "Every change can be undone from History.",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        });
        return panel;
    }
}
