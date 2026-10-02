using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Dashio.App.Services;
using Dashio.Core.Models;

namespace Dashio.App.ViewModels;

public enum AppFilter
{
    All,
    AtStartup,
    NotInTaskManager,
    AdminScan,
}

public enum AppSort
{
    MostAtStartup,
    Name,
    Memory,
    Cpu,
}

public sealed partial class AppsViewModel : ObservableObject
{
    public ObservableCollection<AppRowViewModel> Apps { get; } = [];

    public AppFilter Filter { get; set; }
    public AppSort Sort { get; set; }

    private const string DefaultSummary = "Everything each app has set up to start by itself, on one page per app.";

    // Shown from the start, so the page does not shift when the first scan finishes.
    [ObservableProperty]
    public partial string Summary { get; set; } = DefaultSummary;

    [ObservableProperty]
    public partial string AllLabel { get; set; } = "All";

    [ObservableProperty]
    public partial string AtStartupLabel { get; set; } = "At startup";

    [ObservableProperty]
    public partial string NotInTaskManagerLabel { get; set; } = "Not in Task Manager";

    [ObservableProperty]
    public partial string AdminScanLabel { get; set; } = "Admin scan";

    [ObservableProperty]
    public partial bool HasAdminFound { get; set; }

    // The three headline numbers.
    [ObservableProperty]
    public partial string AtStartupCount { get; set; } = "–";

    [ObservableProperty]
    public partial string HiddenCount { get; set; } = "–";

    [ObservableProperty]
    public partial string RunningCount { get; set; } = "–";

    /// <summary>The first scan has not finished yet.</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; set; } = true;

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial string EmptyTitle { get; set; } = "";

    [ObservableProperty]
    public partial string EmptyText { get; set; } = "";

    public void Rebuild()
    {
        var state = AppServices.State;
        IsLoading = !state.HasScanned;
        if (!state.HasScanned)
            return;

        var search = AppServices.Shell.SearchText;
        var shown = StartupStats.ShownGroups();
        var visible = shown.Where(g => Matches(g, search)).ToList();

        var atStartup = visible.Where(g => g.StartsWithWindowsCount > 0).ToList();
        var hidden = visible.Where(g => g.HiddenFromTaskManager).ToList();
        AllLabel = $"All ({visible.Count})";
        AtStartupLabel = $"At startup ({atStartup.Count})";
        NotInTaskManagerLabel = $"Not in Task Manager ({hidden.Count})";
        var adminFound = visible
            .Where(g => g.Items.Any(i => i.Item.VisibleOnlyWithAdmin || i.Item.IsHiddenTask))
            .ToList();
        AdminScanLabel = $"Admin scan ({adminFound.Count})";
        HasAdminFound = adminFound.Count > 0 || Filter == AppFilter.AdminScan;
        Summary = search.Length > 0
            ? $"{ItemText.Plural(visible.Count, "app")} {(visible.Count == 1 ? "matches" : "match")} “{search}”."
            : DefaultSummary;

        // The headline numbers describe the whole PC, so they ignore the search box.
        var stats = StartupStats.Of(shown);
        AtStartupCount = stats.AtStartup.ToString();
        HiddenCount = stats.NotInTaskManager.ToString();
        RunningCount = stats.RunningServices.ToString();

        var filtered = Filter switch
        {
            AppFilter.AtStartup => atStartup,
            AppFilter.NotInTaskManager => hidden,
            AppFilter.AdminScan => adminFound,
            _ => visible,
        };

        var monitor = AppServices.Monitor;
        IEnumerable<AppGroup> sorted = Sort switch
        {
            AppSort.Name => filtered.OrderBy(Rank).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
            // Sorted once, when chosen: re-sorting on every measurement would make the rows jump about.
            AppSort.Memory => filtered
                .OrderByDescending(g => monitor.UsageOf(g.Id)?.MemoryBytes ?? -1)
                .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
            AppSort.Cpu => filtered
                .OrderByDescending(g => monitor.UsageOf(g.Id)?.CpuPercent ?? -1)
                .ThenByDescending(g => monitor.UsageOf(g.Id)?.MemoryBytes ?? -1)
                .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => filtered.OrderBy(Rank)
                .ThenByDescending(g => g.StartsWithWindowsCount)
                .ThenByDescending(g => g.RunningCount)
                .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
        };

        Apps.Clear();
        foreach (var group in sorted)
            Apps.Add(new AppRowViewModel(group));

        IsEmpty = Apps.Count == 0;
        if (search.Length > 0)
        {
            EmptyTitle = "No matches";
            EmptyText = $"No app or item contains “{search}”. Clear the search to see everything.";
        }
        else
        {
            EmptyTitle = "Nothing here";
            EmptyText = Filter == AppFilter.All
                ? "No autostart items were found."
                : "No app fits this filter. Choose All to see everything.";
        }
    }

    /// <summary>Real apps first, then the vendor leftovers, the unmatched items, and Windows itself.</summary>
    private static int Rank(AppGroup group) => group switch
    {
        { IsWindows: true } => 3,
        { IsUnmatched: true } => 2,
        { IsVendorBucket: true } => 1,
        _ => 0,
    };

    private static bool Matches(AppGroup group, string search) =>
        ShellState.Matches(search, group.Name, group.Publisher) ||
        group.Items.Any(i => ShellState.Matches(search, i.Item.DisplayName, i.Item.Name));
}
