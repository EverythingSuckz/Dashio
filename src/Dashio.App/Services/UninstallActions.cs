using Dashio.App.ViewModels;
using Dashio.Core;
using Dashio.Core.Changes;
using Dashio.Core.Models;
using Dashio.Core.Processes;
using Dashio.Core.Uninstall;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Dashio.App.Services;

/// <summary>An uninstall that has been started and has not ended yet.</summary>
/// <param name="ByWindows">Windows is removing a Store app itself; otherwise the app's own uninstaller is open.</param>
public sealed record UninstallUnderway(string GroupId, string SourceId, string Name, bool ByWindows);

/// <summary>
/// Uninstalling an app: asks first, starts the app's own uninstaller, follows it to its end, and
/// says and records how it ended.
/// </summary>
public static class UninstallActions
{
    /// <summary>How long to wait for the installed list to follow a scan before saying the app is gone.</summary>
    private static readonly TimeSpan ListsFollowWithin = TimeSpan.FromSeconds(5);

    private static readonly ProcessSampler Sampler = new();
    private static readonly PendingUninstalls Pending = new(DashioPaths.PendingUninstalls);
    private static readonly Dictionary<string, UninstallUnderway> Underway = new(StringComparer.OrdinalIgnoreCase);
    private static bool _settling;

    /// <summary>Raised on the UI thread when an uninstall starts or ends.</summary>
    public static event EventHandler? Changed;

    public static IReadOnlyCollection<UninstallUnderway> Current => Underway.Values;

    public static bool IsUnderway(string groupId) => Underway.ContainsKey(groupId);

    /// <summary>An uninstall whose end was not seen is looked at again after every scan.</summary>
    public static void Start() => AppServices.State.Changed += async (_, _) => await SettleAsync();

    /// <summary>What Windows lists for the app that can be removed from here.</summary>
    public static List<UninstallPlan> PlansFor(string groupId)
    {
        var group = AppServices.State.FindGroup(groupId);
        if (group is null || group.IsWindows || groupId.Equals(ResourceMonitor.OwnGroupId, StringComparison.OrdinalIgnoreCase))
            return [];

        var own = AppContext.BaseDirectory.TrimEnd('\\');
        return (AppServices.Inventory.Find(groupId)?.App.Sources ?? [])
            .Concat(group.Sources)
            .DistinctBy(s => s.Id, StringComparer.OrdinalIgnoreCase)
            // Dashio does not remove itself while it is running.
            .Where(s => !own.Equals(s.InstallLocation?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            .Select(s => Uninstaller.PlanFor(group.Name, s))
            .OfType<UninstallPlan>()
            .ToList();
    }

    public static bool CanUninstall(string groupId) => PlansFor(groupId).Count > 0;

    public static async Task UninstallAsync(FrameworkElement anchor, string groupId)
    {
        var plans = PlansFor(groupId);
        var group = AppServices.State.FindGroup(groupId);
        if (plans.Count == 0 || group is null || IsUnderway(groupId))
            return;

        var points = new List<ConfirmDialog.Point>
        {
            new("\uE74D", plans.Any(p => !p.IsPackage) ? "The app's own uninstaller will open" : "Windows will remove the app",
                plans.Any(p => !p.IsPackage)
                    ? "It is the same one Windows Settings starts. It may ask for administrator permission and for a few choices of its own."
                    : "This is a Store app, so Windows removes it directly."),
            new("\uE7BA", "This cannot be undone from History", "To get the app back you would have to install it again.", IsWarning: true),
        };
        var size = AppServices.Inventory.Find(groupId) is { } installed ? InstalledText.Size(installed) : "";
        if (AppServices.Monitor.UsageOf(groupId) is not null)
        {
            points.Insert(1, new ConfirmDialog.Point(
                "\uE9D9", "It is running right now",
                plans.Any(p => !p.IsPackage) ? "Close it first, or the uninstaller may ask you to." : "Windows closes it first, which can take a minute.",
                IsWarning: true));
        }

        // An app can be listed more than once, for example a program and its add-on. One is removed at a time.
        RadioButtons? choice = null;
        if (plans.Count > 1)
        {
            choice = new RadioButtons { Header = "Windows lists this app more than once. Remove:", SelectedIndex = 0 };
            foreach (var plan in plans)
                choice.Items.Add(plan.Source.Kind == AppSourceKind.StorePackage ? $"{plan.Source.Name} (Store app)" : plan.Source.Name);
        }

        var confirmed = await ConfirmDialog.ShowAsync(
            anchor, $"Uninstall {group.Name}?", size.Length > 0 ? $"Takes up {size}" : group.Publisher ?? "", group.IconPath,
            points, "Uninstall", "\uE74D", extra: choice);
        if (confirmed)
            await RunAsync(groupId, plans[Math.Max(0, choice?.SelectedIndex ?? 0)]);
    }

    private static async Task RunAsync(string groupId, UninstallPlan plan)
    {
        var name = plan.Source.Name;
        Notice? notice = null;
        var gone = false;

        Underway[groupId] = new UninstallUnderway(groupId, plan.Source.Id, name, plan.IsPackage);
        Changed?.Invoke(null, EventArgs.Empty);
        try
        {
            // Written first, so the end is still recorded if Dashio is closed before it comes.
            Pending.Add(PendingUninstall.Of(plan, DateTimeOffset.Now));
            var outcome = await Uninstaller.StartAsync(plan);
            switch (outcome.Start)
            {
                case UninstallStart.Cancelled:
                    Conclude(plan, JournalResult.Cancelled, "The administrator prompt was cancelled.");
                    notice = new Notice(InfoBarSeverity.Informational, "Nothing was uninstalled", "You cancelled the administrator prompt.");
                    break;
                case UninstallStart.Failed:
                    Conclude(plan, JournalResult.Failed, outcome.Error);
                    notice = new Notice(InfoBarSeverity.Error, $"{name} could not be uninstalled", outcome.Error ?? "");
                    break;
                case UninstallStart.Removed:
                    Conclude(plan, JournalResult.Applied, null);
                    gone = true;
                    break;
                default:
                    switch (await FollowAsync(plan, outcome))
                    {
                        case UninstallEnd.Removed:
                            Conclude(plan, JournalResult.Applied, null);
                            gone = true;
                            break;
                        case UninstallEnd.Closed:
                            Conclude(plan, JournalResult.Cancelled, "The uninstaller was closed and the app is still installed.");
                            notice = new Notice(
                                InfoBarSeverity.Informational, $"{name} is still installed", "");
                            break;
                        default:
                            // Still pending: the scan that finds the app gone records it.
                            notice = new Notice(InfoBarSeverity.Informational, $"{name} is still installed", "");
                            break;
                    }
                    break;
            }
        }
        catch (Exception e)
        {
            Conclude(plan, JournalResult.Failed, e.Message);
            notice = new Notice(InfoBarSeverity.Error, $"{name} could not be uninstalled", e.Message);
        }

        if (gone)
        {
            // The banner stays until the lists no longer show the app.
            AppServices.Monitor.Forget(groupId);
            await RescanAsync();
            notice = new Notice(InfoBarSeverity.Success, $"Uninstalled {name}", "");
        }
        else
        {
            await AppServices.State.ReloadJournalAsync();
        }

        Underway.Remove(groupId);
        Changed?.Invoke(null, EventArgs.Empty);
        if (notice is not null)
            AppServices.Shell.Notify(notice);
    }

    private static void Conclude(UninstallPlan plan, JournalResult result, string? error)
    {
        Uninstaller.Record(AppServices.Journal, plan.AppName, plan.Source, result, error);
        Pending.Remove(plan.Source.Id);
    }

    /// <summary>
    /// An uninstaller often hands over to a copy of itself and exits at once, so its own process
    /// says little. What counts is whether Windows still lists the app once all of them are gone.
    /// </summary>
    private static Task<UninstallEnd> FollowAsync(UninstallPlan plan, UninstallOutcome outcome)
    {
        var processes = outcome.ProcessId is { } pid ? new UninstallerProcesses(pid, outcome.LaunchedAt) : null;
        return Task.Run(() => UninstallFollower.FollowAsync(
            () => Uninstaller.IsStillInstalled(plan.Source),
            processes is null ? null : () => processes.AnyRunning(Sampler.Sample().Processes),
            FollowTimes.Default));
    }

    /// <summary>Reads what is installed again, and waits for the installed list to follow.</summary>
    private static async Task RescanAsync()
    {
        var state = AppServices.State;
        while (state.IsScanning)
            await Task.Delay(200);

        await state.RefreshAsync();

        // Only now: the list that was being built before the scan may still report in until then.
        var listed = new TaskCompletionSource();
        void OnChanged(object? sender, EventArgs e) => listed.TrySetResult();
        AppServices.Inventory.Changed += OnChanged;
        try
        {
            await Task.WhenAny(listed.Task, Task.Delay(ListsFollowWithin));
        }
        finally
        {
            AppServices.Inventory.Changed -= OnChanged;
        }
    }

    /// <summary>
    /// Records the uninstalls that ended while Dashio was not looking: it was closed, or the
    /// uninstaller handed over to a launcher.
    /// </summary>
    private static async Task SettleAsync()
    {
        if (_settling || AppServices.State.IsScanning || !AppServices.State.HasScanned)
            return;
        _settling = true;
        try
        {
            var underway = Underway.Values.Select(u => u.SourceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var (gone, _) = await Task.Run(() => Pending.Settle(
                Uninstaller.IsStillInstalled, DateTimeOffset.Now, p => underway.Contains(p.SourceId)));
            if (gone.Count == 0)
                return;

            foreach (var pending in gone)
                Uninstaller.Record(AppServices.Journal, pending.AppName, pending.Source, JournalResult.Applied, null);
            await AppServices.State.ReloadJournalAsync();
            AppServices.Shell.Notify(new Notice(
                InfoBarSeverity.Success, $"Uninstalled {string.Join(", ", gone.Select(p => p.Name))}", ""));
        }
        finally
        {
            _settling = false;
        }
    }
}
