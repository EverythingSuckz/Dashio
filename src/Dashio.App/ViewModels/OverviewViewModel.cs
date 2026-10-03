using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Dashio.App.Services;
using Dashio.Core.Changes;
using Dashio.Core.Inventory;
using Dashio.Core.Journal;
using Dashio.Core.Processes;
using Microsoft.UI.Xaml.Media;

namespace Dashio.App.ViewModels;

/// <summary>One app in a "using the most" list.</summary>
public sealed partial class UsageRowViewModel : ObservableObject
{
    private string? _iconPath;

    public UsageRowViewModel(string groupId) => GroupId = groupId;

    public string GroupId { get; }

    /// <summary>The file of the app's largest process, for "Show file in File Explorer".</summary>
    public string? MainPath { get; private set; }

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string Detail { get; set; } = "";

    [ObservableProperty]
    public partial string Value { get; set; } = "";

    /// <summary>The bar's length, 0 to 100, relative to the largest app in the list.</summary>
    [ObservableProperty]
    public partial double Share { get; set; }

    [ObservableProperty]
    public partial string AccessibleName { get; set; } = "";

    [ObservableProperty]
    public partial ImageSource? Icon { get; set; }

    /// <summary>For the lists that are not about live use: the largest apps, and the unopened ones.</summary>
    public void Set(string name, string detail, string value, double share, string? iconPath, string? mainPath)
    {
        Name = name;
        Detail = detail;
        Value = value;
        Share = share;
        MainPath = mainPath;
        AccessibleName = $"{name}, {value}, {detail}";
        if (_iconPath != iconPath || (Icon is null && _iconPath is null && iconPath is not null))
        {
            _iconPath = iconPath;
            _ = LoadIconAsync(iconPath);
        }
    }

    public void Update(AppUsage usage, string value, double share)
    {
        Name = usage.Owner.Name;
        MainPath = usage.Processes.Select(p => p.Path).FirstOrDefault(p => p is not null);
        Detail = UsageText.Processes(usage.Processes.Count);
        Value = value;
        Share = share;
        AccessibleName = $"{Name}, {value}, {Detail}";

        if (_iconPath != usage.Owner.IconPath || (Icon is null && _iconPath is null && usage.Owner.IconPath is not null))
        {
            _iconPath = usage.Owner.IconPath;
            _ = LoadIconAsync(_iconPath);
        }
    }

    private async Task LoadIconAsync(string? path)
    {
        var icon = await AppServices.Icons.GetAsync(path);
        if (path == _iconPath)
            Icon = icon;
    }
}

/// <summary>One line of the "Recent changes" panel.</summary>
/// <param name="IsLast">The last line has no connector running down to a next one.</param>
public sealed record RecentChange(string Glyph, string Title, string AppName, string When, bool IsLast)
{
    public string AccessibleName => $"{Title}, {AppName}, {When}";
    public bool HasNext => !IsLast;
}

public sealed partial class OverviewViewModel : ObservableObject
{
    private const int TopCount = 6;
    private const int RecentCount = 5;

    public ObservableCollection<UsageRowViewModel> TopMemory { get; } = [];
    public ObservableCollection<UsageRowViewModel> TopCpu { get; } = [];
    public ObservableCollection<UsageRowViewModel> TopStorage { get; } = [];
    public ObservableCollection<UsageRowViewModel> TopUnused { get; } = [];

    [ObservableProperty]
    public partial bool IsLoadingInstalled { get; set; } = true;

    [ObservableProperty]
    public partial string StorageCaption { get; set; } = "Each app with the data it keeps, largest first";

    [ObservableProperty]
    public partial string UnusedCaption { get; set; } = "Apps you can open but have not";

    /// <summary>Shown in place of the list when there is nothing to list, or no way to tell.</summary>
    [ObservableProperty]
    public partial string UnusedNote { get; set; } = "";

    /// <summary>The list of unopened apps needs the administrator check before it can say anything.</summary>
    [ObservableProperty]
    public partial bool NeedsAdminCheck { get; set; }

    [ObservableProperty]
    public partial string DiskValue { get; set; } = "";

    [ObservableProperty]
    public partial string DiskDetail { get; set; } = "";

    [ObservableProperty]
    public partial double DiskUsedPercent { get; set; }

    /// <summary>Call when the installed list, a size or a verdict changes.</summary>
    public void RebuildInstalled()
    {
        ShowDisk();
        var inventory = AppServices.Inventory;
        IsLoadingInstalled = !inventory.IsReady;
        if (!inventory.IsReady)
            return;

        var largest = inventory.Apps.OrderByDescending(a => a.TotalBytes).Take(TopCount).ToList();
        var most = Math.Max(1, largest.Count == 0 ? 1 : largest[0].TotalBytes);
        Fill(TopStorage, largest, a => InstalledText.Size(a), a => InstalledText.Breakdown(a), a => 100.0 * a.TotalBytes / most);
        StorageCaption = inventory.IsMeasuring
            ? "Still measuring. Each app with the data it keeps, largest first"
            : "Each app with the data it keeps, largest first";

        var unused = inventory.Apps.Where(a => a.Usage.Verdict == UsageVerdict.NotOpenedLately)
            .OrderByDescending(a => a.TotalBytes).ToList();
        var mostUnused = Math.Max(1, unused.Count == 0 ? 1 : unused[0].TotalBytes);
        Fill(
            TopUnused, unused.Take(TopCount).ToList(), a => InstalledText.Size(a),
            a => InstalledText.LastOpened(a.Usage), a => 100.0 * a.TotalBytes / mostUnused);

        if (inventory.CoversFrom is { } since)
        {
            NeedsAdminCheck = false;
            UnusedCaption = unused.Count == 0
                ? $"Going by Windows' records since {since:d MMMM}"
                : $"{ItemText.Plural(unused.Count, "app")} with no sign of being opened since {since:d MMMM}, " +
                  $"taking {UsageText.Memory(unused.Sum(a => a.TotalBytes))}";
            UnusedNote = unused.Count == 0 ? "Every app you can open has been opened in that time." : "";
        }
        else
        {
            var evidence = LastOpenedEvidence.Of(inventory);
            UnusedCaption = "Apps you can open but have not";
            NeedsAdminCheck = evidence.NeedsAdminCheck;
            UnusedNote = evidence.NeedsAdminCheck ? "" : evidence.Text;
        }
    }

    private static void Fill(
        ObservableCollection<UsageRowViewModel> rows, List<InstalledAppView> apps,
        Func<InstalledAppView, string> value, Func<InstalledAppView, string> detail, Func<InstalledAppView, double> share)
    {
        for (var i = 0; i < apps.Count; i++)
        {
            var app = apps[i];
            if (i >= rows.Count)
                rows.Add(new UsageRowViewModel(app.Id));
            else if (!rows[i].GroupId.Equals(app.Id, StringComparison.OrdinalIgnoreCase))
                rows[i] = new UsageRowViewModel(app.Id);
            rows[i].Set(
                app.App.Owner.Name, detail(app), value(app), share(app),
                app.App.Owner.IconPath ?? app.App.Programs.FirstOrDefault(),
                app.App.Programs.FirstOrDefault());
        }
        while (rows.Count > apps.Count)
            rows.RemoveAt(rows.Count - 1);
    }

    private void ShowDisk()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\";
            var drive = new DriveInfo(root);
            DiskValue = UsageText.Memory(drive.AvailableFreeSpace);
            DiskDetail = $"free of {UsageText.Memory(drive.TotalSize)} on {root.TrimEnd((char)92)}";
            DiskUsedPercent = drive.TotalSize <= 0 ? 0 : 100.0 * (drive.TotalSize - drive.AvailableFreeSpace) / drive.TotalSize;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            DiskValue = "";
            DiskDetail = "";
        }
    }

    /// <summary>False until the first measurement has arrived.</summary>
    [ObservableProperty]
    public partial bool HasReading { get; set; }

    [ObservableProperty]
    public partial string MemoryValue { get; set; } = "";

    [ObservableProperty]
    public partial string MemoryDetail { get; set; } = "";

    [ObservableProperty]
    public partial string MemoryTip { get; set; } = "";

    [ObservableProperty]
    public partial string CpuValue { get; set; } = "";

    [ObservableProperty]
    public partial string CpuTip { get; set; } = "";

    /// <summary>The apps have not been matched yet: either no measurement, or the first scan is still running.</summary>
    [ObservableProperty]
    public partial bool IsLoadingApps { get; set; } = true;

    [ObservableProperty]
    public partial bool IsCpuQuiet { get; set; }

    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    // What starts by itself.
    [ObservableProperty]
    public partial bool IsLoadingStartup { get; set; } = true;

    [ObservableProperty]
    public partial string AtStartupCount { get; set; } = "";

    [ObservableProperty]
    public partial string HiddenCount { get; set; } = "";

    [ObservableProperty]
    public partial string RunningCount { get; set; } = "";

    // What a screen reader says for each tile: the number, what it counts, and what pressing it does.
    [ObservableProperty]
    public partial string AtStartupName { get; set; } = "Apps that start with Windows";

    [ObservableProperty]
    public partial string HiddenName { get; set; } = "Apps not in Task Manager";

    [ObservableProperty]
    public partial string RunningName { get; set; } = "Services running now";

    [ObservableProperty]
    public partial IReadOnlyList<KindBreakdown> Kinds { get; set; } = [];

    /// <summary>"2 new in the last two weeks", or empty when nothing was added.</summary>
    [ObservableProperty]
    public partial string NewItemsText { get; set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<RecentChange> Recent { get; set; } = [];

    [ObservableProperty]
    public partial bool HasNoRecent { get; set; } = true;

    /// <summary>Call after a scan, a change, or a settings change.</summary>
    public void RebuildStartup()
    {
        var state = AppServices.State;
        IsPaused = AppServices.Settings.RefreshSeconds == 0;
        IsLoadingStartup = !state.HasScanned;
        if (state.HasScanned)
        {
            var stats = StartupStats.Of(StartupStats.ShownGroups());
            AtStartupCount = stats.AtStartup.ToString();
            HiddenCount = stats.NotInTaskManager.ToString();
            RunningCount = stats.RunningServices.ToString();
            Kinds = stats.Kinds;
            var now = DateTimeOffset.Now;
            var added = StartupStats.ShownGroups().SelectMany(g => g.Items).Count(i => state.Seen.NewSince(i.Item.Id, now) is not null);
            NewItemsText = added == 0 ? "" : $"{ItemText.Plural(added, "new item")} in the last two weeks";
            AtStartupName = $"{ItemText.Plural(stats.AtStartup, "app")} start with Windows. Show them.";
            HiddenName = $"{ItemText.Plural(stats.NotInTaskManager, "app")} start with Windows without being in Task Manager. Show them.";
            RunningName = $"{ItemText.Plural(stats.RunningServices, "service")} running now. Show the services.";
        }

        var recent = state.Journal
            .Where(e => e.Result == JournalResult.Applied)
            .OrderByDescending(e => e.Time)
            .Take(RecentCount)
            .ToList();
        Recent = recent
            .Select((e, index) => new RecentChange(
                ItemText.ActionGlyph(e.Action),
                $"{ItemText.Done(e.Action)} {e.ItemName}",
                e.AppName,
                When(e.Time.LocalDateTime),
                index == recent.Count - 1))
            .ToList();
        HasNoRecent = Recent.Count == 0;
    }

    private static string When(DateTime time) =>
        time.Date == DateTime.Today ? $"Today, {time:t}"
        : time.Date == DateTime.Today.AddDays(-1) ? $"Yesterday, {time:t}"
        : $"{time:d}";

    /// <summary>Call after each measurement.</summary>
    public void Update()
    {
        var monitor = AppServices.Monitor;
        if (monitor.Latest is not { } latest)
            return;

        HasReading = true;
        var system = latest.System;
        MemoryValue = UsageText.Memory(system.MemoryUsedBytes);
        MemoryDetail = $"of {UsageText.Memory(system.MemoryTotalBytes)} in use ({system.MemoryPercent:F0}%)";
        CpuValue = UsageText.Cpu(system.CpuPercent);
        MemoryTip = RangeTip(monitor.History.Select(p => p.MemoryPercent));
        CpuTip = RangeTip(monitor.History.Select(p => p.CpuPercent));

        IsLoadingApps = latest.Apps.Count == 0;
        if (IsLoadingApps)
            return;

        var byMemory = latest.Apps.OrderByDescending(a => a.MemoryBytes).Take(TopCount).ToList();
        var mostMemory = Math.Max(1, byMemory[0].MemoryBytes);
        Fill(TopMemory, byMemory, a => UsageText.Memory(a.MemoryBytes), a => 100.0 * a.MemoryBytes / mostMemory);

        var byCpu = latest.Apps.Where(a => a.CpuPercent >= 0.05)
            .OrderByDescending(a => a.CpuPercent).Take(TopCount).ToList();
        var mostCpu = byCpu.Count == 0 ? 1 : Math.Max(0.1, byCpu[0].CpuPercent);
        Fill(TopCpu, byCpu, a => UsageText.Cpu(a.CpuPercent), a => 100.0 * a.CpuPercent / mostCpu);
        IsCpuQuiet = byCpu.Count == 0;
    }

    private static string RangeTip(IEnumerable<double> values)
    {
        var list = values.ToList();
        return list.Count < 2
            ? "The last 60 seconds"
            : $"The last 60 seconds: between {list.Min():F0}% and {list.Max():F0}%";
    }

    /// <summary>Keeps a row when the same app stays in the same place, so only its figures change.</summary>
    private static void Fill(
        ObservableCollection<UsageRowViewModel> rows, List<AppUsage> apps,
        Func<AppUsage, string> value, Func<AppUsage, double> share)
    {
        for (var i = 0; i < apps.Count; i++)
        {
            var app = apps[i];
            if (i >= rows.Count)
                rows.Add(new UsageRowViewModel(app.GroupId));
            else if (!rows[i].GroupId.Equals(app.GroupId, StringComparison.OrdinalIgnoreCase))
                rows[i] = new UsageRowViewModel(app.GroupId);
            rows[i].Update(app, value(app), share(app));
        }
        while (rows.Count > apps.Count)
            rows.RemoveAt(rows.Count - 1);
    }
}
