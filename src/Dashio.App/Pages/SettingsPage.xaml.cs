using System.Diagnostics;
using System.Reflection;
using Dashio.App.Services;
using Dashio.Core.Journal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Dashio.App.Pages;

public sealed partial class SettingsPage : Page
{
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        SizeChanged += (_, e) => Ui.FitToPage(Shell, e.NewSize.Width);

        var settings = AppServices.Settings;
        ThemeBox.SelectedIndex = settings.Theme switch
        {
            ElementTheme.Light => 1,
            ElementTheme.Dark => 2,
            _ => 0,
        };
        ShowWindowsSwitch.IsOn = settings.ShowWindowsComponents;
        StartWithWindowsSwitch.IsOn = StartWithWindows.IsOn;
        RefreshBox.SelectedIndex = Math.Max(0, Array.IndexOf(SettingsStore.RefreshChoices, settings.RefreshSeconds));
        LogCard.Description = ChangeJournal.DefaultPath;

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        AboutExpander.Description = version is null ? "" : $"Version {version.Major}.{version.Minor}.{version.Build}";
        _loading = false;
    }

    /// <summary>The search box opens this page with the name of the control to go to.</summary>
    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        // The entry can have been switched from the Startup page or Task Manager since.
        _loading = true;
        StartWithWindowsSwitch.IsOn = StartWithWindows.IsOn;
        _loading = false;

        if (e.Parameter is not string name || FindName(name) is not Control control)
            return;

        void Show()
        {
            control.StartBringIntoView();
            control.Focus(FocusState.Keyboard);
        }

        if (control.IsLoaded)
            Show();
        else
            control.Loaded += (_, _) => Show();
    }

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;
        var tag = (ThemeBox.SelectedItem as ComboBoxItem)?.Tag as string;
        AppServices.Settings.Theme = Enum.TryParse<ElementTheme>(tag, out var theme) ? theme : ElementTheme.Default;
        AppServices.Settings.Save();
    }

    private void RefreshBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !int.TryParse((RefreshBox.SelectedItem as ComboBoxItem)?.Tag as string, out var seconds))
            return;
        AppServices.Settings.RefreshSeconds = seconds;
        AppServices.Settings.Save();
        AppServices.Monitor.RefreshNow();
    }

    private async void StartWithWindows_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        if (!StartWithWindows.Set(StartWithWindowsSwitch.IsOn))
        {
            AppServices.Shell.Notify(new Notice(InfoBarSeverity.Error, "The startup entry could not be changed", ""));
            _loading = true;
            StartWithWindowsSwitch.IsOn = StartWithWindows.IsOn;
            _loading = false;
            return;
        }
        // So the Startup page lists the entry, or no longer does.
        await AppServices.State.RefreshAsync();
    }

    private void ShowWindows_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        AppServices.Settings.ShowWindowsComponents = ShowWindowsSwitch.IsOn;
        AppServices.Settings.Save();
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e)
    {
        RescanButton.IsEnabled = false;
        try
        {
            await AppServices.State.RefreshAsync();
        }
        finally
        {
            RescanButton.IsEnabled = true;
        }
    }

    private async void AdminScan_Click(object sender, RoutedEventArgs e)
    {
        if (!AppServices.State.HasScanned)
            return;

        AdminScanButton.IsEnabled = false;
        AdminScanRing.IsActive = true;
        try
        {
            var (message, isError) = await AppServices.State.RunAdminScanAsync();
            AppServices.Shell.Notify(new Notice(
                isError ? InfoBarSeverity.Error : InfoBarSeverity.Informational, "Admin scan", message));
        }
        finally
        {
            AdminScanRing.IsActive = false;
            AdminScanButton.IsEnabled = true;
        }
    }

    private async void UsageCheck_Click(object sender, RoutedEventArgs e)
    {
        UsageCheckButton.IsEnabled = false;
        UsageCheckRing.IsActive = true;
        try
        {
            var (message, isError) = await AppServices.Inventory.RunAdminCheckAsync();
            AppServices.Shell.Notify(new Notice(
                isError ? InfoBarSeverity.Error : InfoBarSeverity.Informational, "Last opened", message));
        }
        finally
        {
            UsageCheckRing.IsActive = false;
            UsageCheckButton.IsEnabled = true;
        }
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = Path.GetDirectoryName(ChangeJournal.DefaultPath)!;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }
}
