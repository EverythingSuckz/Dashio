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
    // Navigation plus a page wide enough for every column of the tables.
    private const int DefaultWidth = 1280;
    private const int DefaultHeight = 820;
    private const int MinimumWidth = 640;
    private const int MinimumHeight = 480;

    /// <summary>A message that has just appeared survives the page change that came with it.</summary>
    private static readonly TimeSpan NoticeSettles = TimeSpan.FromSeconds(1.5);

    private Guid? _noticeUndoBatch;
    private DateTimeOffset _noticeShown;
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
        UninstallActions.Changed += (_, _) => ShowUninstalls();
        UninstallActions.Start();
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

        // What is installed and what starts by itself is read again when the window is returned to.
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated && AppServices.State.IsStale)
                Refresh();
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

    // ---- Staying out of the way, for the automated tests ----

    private const int ExtendedStyle = -20;
    private const long NoActivateStyle = 0x08000000;
    private const uint KeepSizeAndOrder = 0x0015;   // No resize, no change of order, no activation.
    private const int OffScreen = -30000;
    private const int SentMessageHook = 4;
    private const uint ShowWindowMessage = 0x0018;
    private const uint PositionChangingMessage = 0x0046;
    private const int CloakAttribute = 13;
    private const string PopupWindowClass = "Microsoft.UI.Content.PopupWindowSiteBridge";

    private delegate IntPtr SentMessageHandler(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct SentMessage
    {
        public IntPtr LParam;
        public IntPtr WParam;
        public uint Message;
        public IntPtr Window;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int kind, SentMessageHandler handler, IntPtr module, uint thread);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int size);

    // Kept in a field so the hook outlives the call that set it.
    private SentMessageHandler? _hidePopups;
    private readonly HashSet<IntPtr> _hiddenPopups = [];

    /// <summary>
    /// The tests drive a copy of the window while someone is working in another app. Invoking a
    /// control through UI Automation brings an ordinary window to the front and takes the keyboard,
    /// so this copy refuses activation and sits off the screen. Menus and drop-downs are windows
    /// of their own that Windows moves back onto a screen, so those are kept from being drawn.
    /// It is all still rendered, so it can be read and captured.
    /// </summary>
    public void StayInBackground()
    {
        var window = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var style = GetWindowLongPtr(window, ExtendedStyle).ToInt64() | NoActivateStyle;
        SetWindowLongPtr(window, ExtendedStyle, (IntPtr)style);
        SetWindowPos(window, IntPtr.Zero, OffScreen, OffScreen, 0, 0, KeepSizeAndOrder);

        // This runs inside the call that shows a popup, before the popup is on the screen.
        _hidePopups = (code, wParam, lParam) =>
        {
            if (code >= 0)
            {
                var sent = Marshal.PtrToStructure<SentMessage>(lParam);
                if (sent.Message is ShowWindowMessage or PositionChangingMessage && !_hiddenPopups.Contains(sent.Window))
                    HideIfPopup(sent.Window);
            }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        };
        SetWindowsHookEx(SentMessageHook, _hidePopups, IntPtr.Zero, GetCurrentThreadId());
    }

    private void HideIfPopup(IntPtr window)
    {
        var name = new System.Text.StringBuilder(128);
        GetClassName(window, name, name.Capacity);
        if (name.ToString() != PopupWindowClass)
            return;
        var cloak = 1;
        if (DwmSetWindowAttribute(window, CloakAttribute, ref cloak, sizeof(int)) == 0)
            _hiddenPopups.Add(window);
    }

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

    private static Type? PageOf(object? item) => PageOf((item as NavigationViewItem)?.Tag as string);

    private static Type? PageOf(string? tag) => tag switch
    {
        "overview" => typeof(OverviewPage),
        "processes" => typeof(ProcessesPage),
        "apps" => typeof(AppsPage),
        "storage" => typeof(StoragePage),
        "startup" => typeof(StartupPage),
        "history" => typeof(HistoryPage),
        "settings" => typeof(SettingsPage),
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

    private void NavigateTop(Type page, object? parameter = null)
    {
        NavFrame.Navigate(page, parameter, new EntranceNavigationTransitionInfo());
        NavFrame.BackStack.Clear();
    }

    private void NavFrame_Navigated(object sender, NavigationEventArgs e)
    {
        // A message is about the page it appeared on, so it does not follow to the next one.
        if (NoticeBar.IsOpen && DateTimeOffset.Now - _noticeShown > NoticeSettles)
            NoticeBar.IsOpen = false;

        var page = e.SourcePageType;
        _syncingSelection = true;
        try
        {
            if (page == typeof(SettingsPage))
                NavView.SelectedItem = NavView.SettingsItem;
            else if (page == typeof(OverviewPage))
                NavView.SelectedItem = OverviewItem;
            else if (page == typeof(ProcessesPage))
                NavView.SelectedItem = ProcessesItem;
            else if (page == typeof(StoragePage))
                NavView.SelectedItem = StorageItem;
            else if (page == typeof(StartupPage))
                NavView.SelectedItem = StartupItem;
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
        if (args.Reason == AutoSuggestionBoxTextChangeReason.SuggestionChosen)
            return;

        var results = SearchIndex.Find(sender.Text);
        foreach (var result in results)
            _ = result.LoadIconAsync();
        if (results.Count == 0 && sender.Text.Trim().Length > 0)
        {
            results.Add(new SearchResult
            {
                Target = SearchTarget.None,
                Title = "Nothing found",
                Caption = "Try the name of an app, a program, a service or a setting",
                Glyph = "\uE721",
            });
        }
        sender.ItemsSource = results;
        // The list opens by itself only while the box has the keyboard; text can also arrive without it.
        sender.IsSuggestionListOpen = results.Count > 0;
    }

    /// <summary>Enter goes to the first suggestion; choosing one goes to that one.</summary>
    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var result = args.ChosenSuggestion as SearchResult
                     ?? (sender.ItemsSource as List<SearchResult>)?.FirstOrDefault();
        if (result is null || result.Target == SearchTarget.None)
            return;

        sender.Text = "";
        sender.ItemsSource = null;
        GoTo(result);
    }

    private void GoTo(SearchResult result)
    {
        switch (result.Target)
        {
            case SearchTarget.App:
                NavFrame.Navigate(
                    typeof(AppDetailPage), result.Id,
                    new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight });
                break;
            case SearchTarget.Process:
                NavigateTop(typeof(ProcessesPage), result.Text);
                break;
            case SearchTarget.StartupItem:
                NavigateTop(typeof(StartupPage), new StartupTarget(Search: result.Text));
                break;
            case SearchTarget.Setting:
                NavigateTop(typeof(SettingsPage), result.Id);
                break;
            case SearchTarget.Page when PageOf(result.Id) is { } page:
                NavigateTop(page);
                break;
        }
    }

    private void Search_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SearchBox.Focus(FocusState.Keyboard);
        args.Handled = true;
    }

    /// <summary>Ctrl+F goes to the filter of the page that is showing, or to the search box when it has none.</summary>
    private void Filter_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (NavFrame.Content is IFilterPage page)
            page.FocusFilter();
        else
            SearchBox.Focus(FocusState.Keyboard);
        args.Handled = true;
    }

    private void Refresh_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        Refresh();
        args.Handled = true;
    }

    private void Refresh()
    {
        // Queued switches survive a rescan: applying re-reads each item from Windows anyway.
        if (!ChangeRunner.IsBusy)
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
        _noticeShown = DateTimeOffset.Now;
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

    private void ShowUninstalls()
    {
        var underway = UninstallActions.Current;
        if (underway.Count == 0)
        {
            UninstallBar.IsOpen = false;
            return;
        }

        var first = underway.First();
        var others = underway.Count > 1 ? $" and {ItemText.Plural(underway.Count - 1, "other")}" : "";
        UninstallTitle.Text = first.ByWindows
            ? $"Uninstalling {first.Name}{others}"
            : $"The uninstaller of {first.Name}{others} is open";
        UninstallDetail.Text = first.ByWindows
            ? "Windows is removing the app. This can take a minute when the app is running."
            : "Finish it there. Dashio says so here when the app is gone.";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(UninstallBar, UninstallTitle.Text);
        if (!UninstallBar.IsOpen)
        {
            UninstallBar.IsOpen = true;
            Motion.Enter(UninstallBar, fromY: -12);
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
        ConfirmDialog.WithIcons(dialog, "\uE73E");
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
