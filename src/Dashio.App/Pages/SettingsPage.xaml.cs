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
        LogCard.Description = ChangeJournal.DefaultPath;

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        AboutExpander.Description = version is null ? "" : $"Version {version.Major}.{version.Minor}.{version.Build}";
        _loading = false;
    }

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;
        var tag = (ThemeBox.SelectedItem as ComboBoxItem)?.Tag as string;
        AppServices.Settings.Theme = Enum.TryParse<ElementTheme>(tag, out var theme) ? theme : ElementTheme.Default;
        AppServices.Settings.Save();
    }

    private void ShowWindows_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        AppServices.Settings.ShowWindowsComponents = ShowWindowsSwitch.IsOn;
        AppServices.Settings.Save();
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

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = Path.GetDirectoryName(ChangeJournal.DefaultPath)!;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }
}
