using Dashio.Core.Changes;
using Dashio.Core.Models;
using Dashio.Core.Processes;
using Dashio.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Dashio.App.Services;

/// <summary>Ending what is running: asks first, then hands over to <see cref="ChangeRunner"/>.</summary>
public static class RunningActions
{
    /// <summary>Windows and Dashio itself are never offered.</summary>
    public static bool CanEnd(AppUsage? usage) =>
        usage is { Owner.IsWindows: false }
        && !usage.GroupId.Equals(ResourceMonitor.OwnGroupId, StringComparison.OrdinalIgnoreCase)
        && usage.Processes.Any(CanEnd);

    public static bool CanEnd(ProcessUsage process) => process.Path is not null && process.Pid != Environment.ProcessId;

    /// <summary>A file of the app to show in File Explorer: what it is running, else what it starts.</summary>
    public static string? LocationOf(string groupId)
    {
        var running = AppServices.Monitor.UsageOf(groupId)?.Processes.Select(p => p.Path).FirstOrDefault(p => p is not null);
        if (running is not null)
            return running;
        return AppServices.State.FindGroup(groupId)?.Items
            .Select(i => i.Item)
            .Where(i => i.Evidence is { Exists: true })
            .Select(i => i.TargetPath)
            .FirstOrDefault(p => p is not null);
    }

    public static async Task EndAppAsync(FrameworkElement anchor, string groupId)
    {
        var usage = AppServices.Monitor.UsageOf(groupId);
        if (ChangeRunner.IsBusy || usage is null || !CanEnd(usage))
            return;

        var processes = usage.Processes.Where(CanEnd).ToList();
        var services = RunningServices(groupId, null);
        var name = usage.Owner.Name;

        var lines = new List<string>
        {
            $"{UsageText.Processes(processes.Count)} will be closed. Anything unsaved in {name} is lost.",
            "Dashio first asks its windows to close, then ends whatever is still running a few seconds later.",
        };
        if (services.Count > 0)
        {
            lines.Add(
                $"{ItemText.Plural(services.Count, "running service")} will be stopped first, which needs administrator permission. " +
                "They start again as before unless you turn them off.");
        }

        if (await ConfirmAsync(anchor, $"End {name}?", lines))
            await ChangeRunner.EndAsync(new PlannedEnd(name, name, Requests(processes), services));
    }

    public static async Task EndProcessAsync(FrameworkElement anchor, string groupId, ProcessUsage process)
    {
        if (ChangeRunner.IsBusy || !CanEnd(process))
            return;

        var appName = AppServices.Monitor.UsageOf(groupId)?.Owner.Name ?? process.Name;
        var services = RunningServices(groupId, process.Services);
        var lines = new List<string> { $"Process {process.Pid} will be closed. Anything unsaved in it is lost." };
        if (services.Count > 0)
        {
            lines.Add(
                $"It runs {ItemText.Plural(services.Count, "service")}, which will be stopped first. That needs administrator permission.");
        }

        if (await ConfirmAsync(anchor, $"End {process.Name}?", lines))
            await ChangeRunner.EndAsync(new PlannedEnd(appName, process.Name, Requests([process]), services));
    }

    private static List<EndRequest> Requests(IEnumerable<ProcessUsage> processes) =>
        processes.Select(p => new EndRequest(p.Pid, p.StartTicks)).ToList();

    /// <summary>The app's running services that may be changed, optionally only those with the given names.</summary>
    private static List<AutostartItem> RunningServices(string groupId, IReadOnlyList<string>? names) =>
        AppServices.State.FindGroup(groupId)?.Items
            .Select(i => i.Item)
            .Where(i => i.Kind == AutostartKind.Service && i.IsRunning == true && !i.IsProtected)
            .Where(i => names is null || names.Contains(i.Name, StringComparer.OrdinalIgnoreCase))
            .ToList() ?? [];

    private static async Task<bool> ConfirmAsync(FrameworkElement anchor, string title, List<string> lines)
    {
        var content = new StackPanel { Spacing = 12, MaxWidth = 440 };
        foreach (var line in lines)
            content.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock
        {
            Text = "This cannot be undone, but the app can be opened again.",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });

        var dialog = new ContentDialog
        {
            XamlRoot = anchor.XamlRoot,
            RequestedTheme = (anchor.XamlRoot.Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default,
            Title = title,
            Content = content,
            PrimaryButtonText = "End",
            CloseButtonText = "Cancel",
            // Cancel is the safe answer, so Enter picks it.
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
