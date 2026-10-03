using Dashio.Core.Changes;
using Dashio.Core.Models;
using Dashio.Core.Processes;
using Dashio.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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

    private const string EndFooter = "Ending cannot be undone, but the app can be opened again.";

    public static async Task EndAppAsync(FrameworkElement anchor, string groupId)
    {
        var usage = AppServices.Monitor.UsageOf(groupId);
        if (ChangeRunner.IsBusy || usage is null || !CanEnd(usage))
            return;

        var processes = usage.Processes.Where(CanEnd).ToList();
        var services = RunningServices(groupId, null);
        var name = usage.Owner.Name;

        var points = new List<ConfirmDialog.Point>
        {
            new("\uE711", $"{UsageText.Processes(processes.Count)} will be closed",
                "Dashio asks the app's windows to close first, then ends whatever is still running a few seconds later."),
            new("\uE7BA", "Unsaved work is lost", $"Anything not saved in {name} goes with it.", IsWarning: true),
        };
        if (services.Count > 0)
        {
            points.Add(new ConfirmDialog.Point(
                "\uEA18", $"{ItemText.Plural(services.Count, "running service")} will be stopped first",
                "That needs administrator permission. They start again as before unless you turn them off."));
        }

        var summary = $"{UsageText.Processes(usage.Processes.Count)} using {UsageText.Memory(usage.MemoryBytes)}";
        if (await ConfirmDialog.ShowAsync(anchor, $"End {name}?", summary, usage.Owner.IconPath, points, "End", "\uE71A", EndFooter))
            await ChangeRunner.EndAsync(new PlannedEnd(name, name, Requests(processes), services));
    }

    public static async Task EndProcessAsync(FrameworkElement anchor, string groupId, ProcessUsage process)
    {
        if (ChangeRunner.IsBusy || !CanEnd(process))
            return;

        var owner = AppServices.Monitor.UsageOf(groupId)?.Owner;
        var appName = owner?.Name ?? process.Name;
        var services = RunningServices(groupId, process.Services);
        var points = new List<ConfirmDialog.Point>
        {
            new("\uE711", "This one process will be closed", $"The rest of {appName} keeps running."),
            new("\uE7BA", "Unsaved work is lost", "Anything not saved in this process goes with it.", IsWarning: true),
        };
        if (services.Count > 0)
        {
            points.Add(new ConfirmDialog.Point(
                "\uEA18", $"It runs {ItemText.Plural(services.Count, "service")}, stopped first",
                "That needs administrator permission."));
        }

        var summary = $"Process {process.Pid} of {appName}, using {UsageText.Memory(process.MemoryBytes)}";
        if (await ConfirmDialog.ShowAsync(anchor, $"End {process.Name}?", summary, process.Path ?? owner?.IconPath, points, "End", "\uE71A", EndFooter))
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
}
