using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Dashio.App.Services;
using Dashio.Core.Inventory;
using Dashio.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Dashio.App.ViewModels;

public enum AppFilter
{
    All,
    Running,
    AtStartup,
    NotInTaskManager,
    NotOpenedLately,
    AdminScan,
}

public enum AppColumn
{
    Name,
    Running,
    Starts,
    Size,
    LastOpened,
}

/// <summary>Which columns of the app list fit the page. Shared by the headings and every row.</summary>
public sealed partial class AppColumns : ObservableObject
{
    public const double SizeColumn = 104;
    public const double LastOpenedColumn = 184;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeWidth))]
    public partial bool ShowSize { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastOpenedWidth))]
    public partial bool ShowLastOpened { get; set; } = true;

    public GridLength SizeWidth => Ui.ColumnWidth(ShowSize, SizeColumn);
    public GridLength LastOpenedWidth => Ui.ColumnWidth(ShowLastOpened, LastOpenedColumn);
}

/// <summary>One app in the Apps list: what it is running, what it starts, its size and when it was opened.</summary>
public sealed partial class AppRowViewModel : ObservableObject
{
    private string? _iconPath;

    public AppRowViewModel(AppEntry entry, AppColumns columns)
    {
        Entry = entry;
        Columns = columns;
        Update(entry);
    }

    public string Id => Entry.Id;
    public AppEntry Entry { get; private set; }
    public AppColumns Columns { get; }

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string Publisher { get; set; } = "";

    // Running now.
    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial string RunningText { get; set; } = "";

    [ObservableProperty]
    public partial string RunningTip { get; set; } = "";

    // What it has set up to start by itself.
    [ObservableProperty]
    public partial bool HasItems { get; set; }

    [ObservableProperty]
    public partial bool StartsWithWindows { get; set; }

    [ObservableProperty]
    public partial bool StartsNothing { get; set; }

    [ObservableProperty]
    public partial string StartsText { get; set; } = "";

    [ObservableProperty]
    public partial string StartsTip { get; set; } = "";

    [ObservableProperty]
    public partial bool IsHidden { get; set; }

    [ObservableProperty]
    public partial bool IsAdminFound { get; set; }

    [ObservableProperty]
    public partial string AdminFoundTip { get; set; } = "";

    // From the installed list.
    [ObservableProperty]
    public partial string SizeText { get; set; } = "";

    [ObservableProperty]
    public partial string SizeTip { get; set; } = "";

    [ObservableProperty]
    public partial bool HasSize { get; set; }

    [ObservableProperty]
    public partial string LastOpenedText { get; set; } = "";

    [ObservableProperty]
    public partial bool HasLastOpened { get; set; }

    /// <summary>What stands in for a date: there is nothing to open, or Windows does not list the app at all.</summary>
    [ObservableProperty]
    public partial string NoLastOpenedText { get; set; } = "";

    [ObservableProperty]
    public partial string NoLastOpenedTip { get; set; } = "";

    [ObservableProperty]
    public partial bool IsUnused { get; set; }

    [ObservableProperty]
    public partial string AccessibleName { get; set; } = "";

    [ObservableProperty]
    public partial ImageSource? Icon { get; set; }

    public bool CanToggleAll => Entry.Group is { } group && group.Items.Any(i => !i.Item.IsProtected && !i.Item.IsHiddenTask);
    public string ToggleAllLabel => Entry.Group is { } group && !ItemActions.AnyOn(group)
        ? "Turn everything it starts back on"
        : "Turn off everything it starts";

    public void Update(AppEntry entry)
    {
        Entry = entry;
        Name = entry.Name;
        Publisher = string.IsNullOrWhiteSpace(entry.Publisher) ? "Unknown publisher" : entry.Publisher;

        var items = entry.Group?.Items ?? [];
        var starts = entry.Group?.StartsWithWindowsCount ?? 0;
        HasItems = items.Count > 0;
        StartsWithWindows = starts > 0;
        StartsNothing = starts == 0;
        StartsText = HasItems ? $"{starts} of {items.Count}" : "0";
        var kinds = string.Join(", ", items
            .GroupBy(i => i.Item.Kind)
            .OrderBy(g => g.Key)
            .Select(g => ItemText.KindCount(g.Key, g.Count())));
        StartsTip = HasItems
            ? $"{starts} of the {ItemText.Plural(items.Count, "thing")} this app set up start with Windows ({kinds})"
            : "This app has not set anything up to start by itself";
        IsHidden = entry.Group?.HiddenFromTaskManager ?? false;

        var adminFound = items.Count(i => i.Item.VisibleOnlyWithAdmin || i.Item.IsHiddenTask);
        IsAdminFound = adminFound > 0;
        AdminFoundTip = items.Any(i => i.Item.IsHiddenTask)
            ? "Has a task that Task Scheduler does not list. Only the admin scan could see it."
            : $"{ItemText.Plural(adminFound, "item")} found only by the admin scan";

        if (entry.Installed is { } installed)
        {
            SizeText = InstalledText.Size(installed);
            SizeTip = InstalledText.Breakdown(installed);
            LastOpenedText = InstalledText.LastOpened(installed.Usage);
            IsUnused = installed.Usage.Verdict == UsageVerdict.NotOpenedLately;
        }
        else
        {
            SizeText = SizeTip = LastOpenedText = "";
            IsUnused = false;
        }

        HasSize = SizeText.Length > 0;
        HasLastOpened = LastOpenedText.Length > 0;
        NoLastOpenedText = entry.Installed is null ? "Not tracked" : "No shortcut";
        NoLastOpenedTip = entry.Installed is null
            ? "Windows does not list this as an installed app, so Dashio does not follow when it is opened"
            : "Dashio follows an app through its Start menu shortcut or app entry, and this has neither: " +
              "a driver, a runtime, a background tool, or a game opened from its launcher";
        UpdateUsage();

        if (_iconPath != entry.IconPath || (Icon is null && _iconPath is null && entry.IconPath is not null))
        {
            _iconPath = entry.IconPath;
            _ = LoadIconAsync(_iconPath);
        }
    }

    /// <summary>Call after each measurement.</summary>
    public void UpdateUsage()
    {
        if (AppServices.Monitor.UsageOf(Id) is { } usage)
        {
            IsRunning = true;
            RunningText = UsageText.Memory(usage.MemoryBytes);
            RunningTip = $"Running now: {UsageText.Processes(usage.Processes.Count)} using {RunningText} of memory and {UsageText.Cpu(usage.CpuPercent)} of the processor";
        }
        else
        {
            IsRunning = false;
            RunningText = RunningTip = "";
        }
        Describe();
    }

    private void Describe()
    {
        var parts = new List<string> { Name, Publisher };
        parts.Add(IsRunning ? $"running, using {RunningText}" : "not running");
        parts.Add(HasItems ? $"{StartsText} start with Windows" : "starts nothing by itself");
        if (IsHidden)
            parts.Add("not shown in Task Manager");
        if (IsAdminFound)
            parts.Add("found by the admin scan");
        if (SizeText.Length > 0)
            parts.Add(SizeText);
        if (LastOpenedText.Length > 0)
            parts.Add(LastOpenedText);
        AccessibleName = string.Join(", ", parts);
    }

    private async Task LoadIconAsync(string? path)
    {
        var icon = await AppServices.Icons.GetAsync(path);
        if (path == _iconPath)
            Icon = icon;
    }
}

public sealed partial class AppsViewModel : ObservableObject
{
    private const string DefaultSummary = "Every app on this PC: what it is running, what it starts by itself, and the space it takes.";

    private List<AppEntry> _matching = [];

    public ObservableCollection<AppRowViewModel> Apps { get; } = [];
    public AppColumns Columns { get; } = new();

    public AppFilter Filter { get; set; }
    public string Search { get; set; } = "";
    public AppColumn SortColumn { get; private set; } = AppColumn.Name;

    /// <summary>The opposite of the order each column starts in: names from Z, figures smallest first.</summary>
    public bool SortReversed { get; private set; }

    // Shown from the start, so the page does not shift when the first scan finishes.
    [ObservableProperty]
    public partial string Summary { get; set; } = DefaultSummary;

    [ObservableProperty]
    public partial string AllLabel { get; set; } = "All";

    [ObservableProperty]
    public partial string RunningLabel { get; set; } = "Running";

    [ObservableProperty]
    public partial string AtStartupLabel { get; set; } = "At startup";

    [ObservableProperty]
    public partial string NotInTaskManagerLabel { get; set; } = "Not in Task Manager";

    [ObservableProperty]
    public partial string UnusedLabel { get; set; } = "Not opened lately";

    [ObservableProperty]
    public partial string AdminScanLabel { get; set; } = "Admin scan";

    [ObservableProperty]
    public partial bool HasAdminFound { get; set; }

    /// <summary>The first scan has not finished yet.</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; set; } = true;

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial string EmptyTitle { get; set; } = "";

    [ObservableProperty]
    public partial string EmptyText { get; set; } = "";

    // What "not opened lately" rests on. Shown on that tab.
    [ObservableProperty]
    public partial bool ShowEvidence { get; set; }

    [ObservableProperty]
    public partial string EvidenceTitle { get; set; } = "";

    [ObservableProperty]
    public partial string EvidenceText { get; set; } = "";

    [ObservableProperty]
    public partial bool NeedsAdminCheck { get; set; }

    // The components that are left out, or that have been asked for.
    [ObservableProperty]
    public partial bool HasComponents { get; set; }

    [ObservableProperty]
    public partial string ComponentsText { get; set; } = "";

    [ObservableProperty]
    public partial string ComponentsAction { get; set; } = "";

    public void SortBy(AppColumn column)
    {
        SortReversed = SortColumn == column && !SortReversed;
        SortColumn = column;
        Rebuild(resort: true);
    }

    /// <summary>Sets a sort order outright, for the View menu.</summary>
    public void SetSort(AppColumn column)
    {
        SortColumn = column;
        SortReversed = false;
        Rebuild(resort: true);
    }

    /// <param name="resort">
    /// True when the user changed tab, sort or filter, or a scan finished. Otherwise rows keep their
    /// places while sizes are still arriving, so the list does not shuffle under the pointer.
    /// </param>
    public void Rebuild(bool resort)
    {
        var state = AppServices.State;
        IsLoading = !state.HasScanned;
        if (!state.HasScanned)
            return;

        var everything = AppCatalog.All(AppServices.Settings.ShowWindowsComponents);
        var components = everything.Count(a => a.IsComponent);
        // A name typed into the filter is looked for everywhere, so a runtime can still be found.
        var showComponents = AppServices.Settings.ShowComponents;
        var all = showComponents || Search.Length > 0 ? everything : everything.Where(a => !a.IsComponent).ToList();
        _matching = all.Where(Matches).ToList();
        ShowLabels();

        HasComponents = components > 0 && Search.Length == 0;
        var noun = components == 1 ? "component or background program" : "components and background programs";
        ComponentsText = showComponents
            ? $"Including {components} {noun}."
            : $"{components} {noun} {(components == 1 ? "is" : "are")} left out.";
        ComponentsAction = showComponents ? "Leave them out" : "Show them";

        var evidence = LastOpenedEvidence.Of(AppServices.Inventory);
        ShowEvidence = Filter == AppFilter.NotOpenedLately && AppServices.Inventory.IsReady;
        EvidenceTitle = evidence.Title;
        EvidenceText = evidence.Text;
        NeedsAdminCheck = evidence.NeedsAdminCheck;

        var running = all.Count(IsRunning);
        var atStartup = all.Count(a => a.Group is { StartsWithWindowsCount: > 0 });
        Summary = Search.Length > 0
            ? $"{ItemText.Plural(_matching.Count, "app")} {(_matching.Count == 1 ? "matches" : "match")} “{Search}”."
            : $"{ItemText.Plural(all.Count, "app")} on this PC. {running} running now, {atStartup} start with Windows.";

        var shown = _matching.Where(a => Fits(a, Filter)).ToList();
        var sameApps = Apps.Count == shown.Count &&
                       Apps.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(shown.Select(a => a.Id));

        if (!resort && sameApps)
        {
            var byId = shown.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var row in Apps)
                row.Update(byId[row.Id]);
        }
        else
        {
            var existing = Apps.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
            Apps.Clear();
            foreach (var entry in Sorted(shown))
            {
                if (existing.TryGetValue(entry.Id, out var row))
                    row.Update(entry);
                else
                    row = new AppRowViewModel(entry, Columns);
                Apps.Add(row);
            }
        }

        ShowEmpty();
    }

    /// <summary>Call after each measurement: the figures change in place and the tab counts follow.</summary>
    public void UpdateUsage()
    {
        foreach (var row in Apps)
            row.UpdateUsage();
        if (!IsLoading)
            RunningLabel = $"Running ({_matching.Count(IsRunning)})";
    }

    private void ShowLabels()
    {
        AllLabel = $"All ({_matching.Count})";
        RunningLabel = $"Running ({_matching.Count(IsRunning)})";
        AtStartupLabel = $"At startup ({_matching.Count(a => Fits(a, AppFilter.AtStartup))})";
        NotInTaskManagerLabel = $"Not in Task Manager ({_matching.Count(a => Fits(a, AppFilter.NotInTaskManager))})";
        UnusedLabel = $"Not opened lately ({_matching.Count(a => Fits(a, AppFilter.NotOpenedLately))})";
        var adminFound = _matching.Count(a => Fits(a, AppFilter.AdminScan));
        AdminScanLabel = $"Admin scan ({adminFound})";
        HasAdminFound = adminFound > 0 || Filter == AppFilter.AdminScan;
    }

    private void ShowEmpty()
    {
        IsEmpty = Apps.Count == 0;
        if (Search.Length > 0)
        {
            EmptyTitle = "No matches";
            EmptyText = $"No app contains “{Search}”. Clear the filter to see everything.";
        }
        else if (Filter == AppFilter.NotOpenedLately)
        {
            // Without records the note above the list already says why it is empty.
            EmptyTitle = AppServices.Inventory.CoversFrom is null ? "Nothing to list yet" : "Nothing to show";
            EmptyText = AppServices.Inventory.CoversFrom is { } since
                ? $"Every app you can open has been opened since {since:d MMMM}."
                : "";
        }
        else
        {
            EmptyTitle = "Nothing here";
            EmptyText = Filter == AppFilter.All
                ? "No apps were found."
                : "No app fits this filter. Choose All to see everything.";
        }
    }

    private static bool IsRunning(AppEntry app) => AppServices.Monitor.UsageOf(app.Id) is not null;

    private static bool Fits(AppEntry app, AppFilter filter) => filter switch
    {
        AppFilter.Running => IsRunning(app),
        AppFilter.AtStartup => app.Group is { StartsWithWindowsCount: > 0 },
        AppFilter.NotInTaskManager => app.Group is { HiddenFromTaskManager: true },
        AppFilter.NotOpenedLately => app.Installed is { Usage.Verdict: UsageVerdict.NotOpenedLately },
        AppFilter.AdminScan => app.Group is { } group && group.Items.Any(i => i.Item.VisibleOnlyWithAdmin || i.Item.IsHiddenTask),
        _ => true,
    };

    private bool Matches(AppEntry app) =>
        ShellState.Matches(Search, app.Name, app.Publisher) ||
        (app.Group is { } group && group.Items.Any(i => ShellState.Matches(Search, i.Item.DisplayName, i.Item.Name)));

    /// <summary>Sorted once, when asked: re-sorting on every measurement would make the rows jump about.</summary>
    private IEnumerable<AppEntry> Sorted(List<AppEntry> apps)
    {
        var names = StringComparer.CurrentCultureIgnoreCase;
        var monitor = AppServices.Monitor;
        IEnumerable<AppEntry> sorted = SortColumn switch
        {
            AppColumn.Running => apps
                .OrderByDescending(a => monitor.UsageOf(a.Id)?.MemoryBytes ?? -1)
                .ThenBy(a => a.Name, names),
            AppColumn.Starts => apps
                .OrderByDescending(a => a.Group?.StartsWithWindowsCount ?? -1)
                .ThenByDescending(a => a.Group?.Items.Count ?? 0)
                .ThenBy(a => a.Name, names),
            AppColumn.Size => apps
                .OrderByDescending(a => a.Installed?.TotalBytes ?? -1)
                .ThenBy(a => a.Name, names),
            // Longest unopened first; apps that cannot be judged come last.
            AppColumn.LastOpened => apps
                .OrderBy(a => a.Installed?.Usage.Verdict switch
                {
                    null or UsageVerdict.NotJudged => 3,
                    UsageVerdict.RunningNow => 2,
                    UsageVerdict.Unknown => 1,
                    _ => 0,
                })
                .ThenBy(a => a.Installed?.Usage.LastOpened ?? DateTimeOffset.MinValue)
                .ThenBy(a => a.Name, names),
            _ => apps.OrderBy(a => a.Rank).ThenBy(a => a.Name, names),
        };
        return SortReversed ? sorted.Reverse() : sorted;
    }
}
