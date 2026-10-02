using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Dashio.App.Services;
using Dashio.Core.Inventory;
using Microsoft.UI.Xaml.Media;

namespace Dashio.App.ViewModels;

/// <summary>How sizes and "last opened" are written, so every page agrees.</summary>
public static class InstalledText
{
    public static string Size(InstalledAppView app)
    {
        if (app.TotalBytes == 0)
            return app.IsMeasuring ? "Measuring" : "0 MB";
        var size = UsageText.Memory(app.TotalBytes);
        // The figure Windows recorded at install time is rough, and nothing could be measured to check it.
        return app.IsReportedOnly ? $"about {size}" : size;
    }

    /// <summary>"app 1.2 GB, data 3.3 GB".</summary>
    public static string Breakdown(InstalledAppView app)
    {
        if (app.IsReportedOnly)
            return "as recorded by Windows";
        var parts = new List<string>();
        if (app.ProgramBytes > 0)
            parts.Add($"app {UsageText.Memory(app.ProgramBytes)}");
        if (app.DataBytes > 0)
            parts.Add($"data {UsageText.Memory(app.DataBytes)}");
        if (app.App.ReportedBytes > 0)
            parts.Add($"other parts about {UsageText.Memory(app.App.ReportedBytes)}");
        return string.Join(", ", parts);
    }

    public static string LastOpened(AppUsageHistory usage) => usage.Verdict switch
    {
        UsageVerdict.RunningNow => "Open now",
        UsageVerdict.NotOpenedLately when usage.LastOpened is { } last => $"Last opened {Ago(last)}",
        UsageVerdict.NotOpenedLately when usage.NotSince is { } since => $"Not since {since:d MMM} or earlier",
        _ when usage.LastOpened is { } last => $"Opened {Ago(last)}",
        UsageVerdict.Unknown => "No record",
        _ => "",
    };

    public static string Ago(DateTimeOffset time)
    {
        var days = (int)(DateTimeOffset.Now.Date - time.LocalDateTime.Date).TotalDays;
        return days switch
        {
            <= 0 => "today",
            1 => "yesterday",
            < 14 => $"{days} days ago",
            < 60 => $"{days / 7} weeks ago",
            < 365 => $"{days / 30} months ago",
            _ => $"on {time.LocalDateTime:d MMM yyyy}",
        };
    }
}

/// <summary>One installed app in the list.</summary>
public sealed partial class InstalledRowViewModel : ObservableObject
{
    private string? _iconPath;

    public InstalledRowViewModel(string id) => Id = id;

    public string Id { get; }

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string Publisher { get; set; } = "";

    [ObservableProperty]
    public partial string SizeText { get; set; } = "";

    [ObservableProperty]
    public partial string SizeTip { get; set; } = "";

    /// <summary>The bar's length, 0 to 100, relative to the largest app.</summary>
    [ObservableProperty]
    public partial double Share { get; set; }

    [ObservableProperty]
    public partial string LastOpenedText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsUnused { get; set; }

    /// <summary>"2 start with Windows", for an app that has set things to start by itself.</summary>
    [ObservableProperty]
    public partial string StartupText { get; set; } = "";

    [ObservableProperty]
    public partial bool HasStartup { get; set; }

    [ObservableProperty]
    public partial string AccessibleName { get; set; } = "";

    [ObservableProperty]
    public partial ImageSource? Icon { get; set; }

    public long Bytes { get; private set; }
    public DateTimeOffset? LastOpened { get; private set; }

    public void Update(InstalledAppView app, long largest)
    {
        Name = app.App.Owner.Name;
        Publisher = string.IsNullOrWhiteSpace(app.App.Owner.Publisher) ? "Unknown publisher" : app.App.Owner.Publisher;
        Bytes = app.TotalBytes;
        LastOpened = app.Usage.LastOpened;
        SizeText = InstalledText.Size(app);
        SizeTip = InstalledText.Breakdown(app);
        Share = largest <= 0 ? 0 : 100.0 * app.TotalBytes / largest;
        LastOpenedText = InstalledText.LastOpened(app.Usage);
        IsUnused = app.Usage.Verdict == UsageVerdict.NotOpenedLately;

        var starts = AppServices.State.Groups
            .FirstOrDefault(g => g.Id.Equals(Id, StringComparison.OrdinalIgnoreCase))?.StartsWithWindowsCount ?? 0;
        HasStartup = starts > 0;
        StartupText = starts == 1 ? "1 starts with Windows" : $"{starts} start with Windows";

        var parts = new List<string> { Name, Publisher, SizeText };
        if (LastOpenedText.Length > 0)
            parts.Add(LastOpenedText);
        if (HasStartup)
            parts.Add(StartupText);
        AccessibleName = string.Join(", ", parts);

        var iconPath = app.App.Owner.IconPath ?? app.App.Programs.FirstOrDefault();
        if (iconPath != _iconPath || (Icon is null && iconPath is not null && _iconPath is null))
        {
            _iconPath = iconPath;
            _ = LoadIconAsync(iconPath);
        }
    }

    private async Task LoadIconAsync(string? path)
    {
        var icon = await AppServices.Icons.GetAsync(path);
        if (path == _iconPath)
            Icon = icon;
    }
}

public enum InstalledFilter
{
    All,
    NotOpenedLately,
}

public enum InstalledSort
{
    Size,
    Name,
    LastOpened,
}

public sealed partial class InstalledViewModel : ObservableObject
{
    public ObservableCollection<InstalledRowViewModel> Rows { get; } = [];

    public InstalledFilter Filter { get; set; }
    public InstalledSort Sort { get; set; }

    private bool _wasMeasuring;

    [ObservableProperty]
    public partial string Summary { get; set; } = "Every app on this PC, how much space it takes and when you last opened it.";

    [ObservableProperty]
    public partial string AllLabel { get; set; } = "All";

    [ObservableProperty]
    public partial string UnusedLabel { get; set; } = "Not opened lately";

    [ObservableProperty]
    public partial bool IsLoading { get; set; } = true;

    [ObservableProperty]
    public partial bool IsMeasuring { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial string EmptyTitle { get; set; } = "";

    [ObservableProperty]
    public partial string EmptyText { get; set; } = "";

    // What the "last opened" column can and cannot say on this PC.
    [ObservableProperty]
    public partial string EvidenceTitle { get; set; } = "";

    [ObservableProperty]
    public partial string EvidenceText { get; set; } = "";

    /// <summary>Nothing on this PC can vouch for an app not having been opened until the admin check is run.</summary>
    [ObservableProperty]
    public partial bool NeedsAdminCheck { get; set; }

    [ObservableProperty]
    public partial bool ShowEvidence { get; set; }

    // The system drive.
    [ObservableProperty]
    public partial string DiskValue { get; set; } = "";

    [ObservableProperty]
    public partial string DiskDetail { get; set; } = "";

    [ObservableProperty]
    public partial double DiskUsedPercent { get; set; }

    /// <param name="resort">
    /// True when the user changed tab, sort or search. Otherwise rows keep their places while
    /// sizes are still arriving, so the list does not shuffle under the pointer.
    /// </param>
    public void Rebuild(bool resort)
    {
        var inventory = AppServices.Inventory;
        IsLoading = !inventory.IsReady;
        IsMeasuring = inventory.IsMeasuring;
        ShowDisk();
        ShowEvidenceNote(inventory);
        if (!inventory.IsReady)
            return;

        var search = AppServices.Shell.SearchText;
        var matching = inventory.Apps
            .Where(a => ShellState.Matches(search, a.App.Owner.Name, a.App.Owner.Publisher))
            .ToList();
        var unused = matching.Where(a => a.Usage.Verdict == UsageVerdict.NotOpenedLately).ToList();
        AllLabel = $"All ({matching.Count})";
        UnusedLabel = $"Not opened lately ({unused.Count})";

        var total = inventory.Apps.Sum(a => a.TotalBytes);
        Summary = search.Length > 0
            ? $"{ItemText.Plural(matching.Count, "app")} {(matching.Count == 1 ? "matches" : "match")} “{search}”."
            : $"{ItemText.Plural(inventory.Apps.Count, "app")} taking {UsageText.Memory(total)}" +
              (inventory.IsMeasuring ? " so far. Still measuring." : ".");

        var shown = Filter == InstalledFilter.NotOpenedLately ? unused : matching;
        var largest = inventory.Apps.Count == 0 ? 0 : inventory.Apps.Max(a => a.TotalBytes);

        var finishedMeasuring = _wasMeasuring && !inventory.IsMeasuring;
        _wasMeasuring = inventory.IsMeasuring;
        var sameApps = Rows.Count == shown.Count && Rows.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(shown.Select(a => a.Id));

        if (!resort && !finishedMeasuring && sameApps)
        {
            var byId = shown.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var row in Rows)
                row.Update(byId[row.Id], largest);
        }
        else
        {
            IEnumerable<InstalledAppView> sorted = Sort switch
            {
                InstalledSort.Name => shown.OrderBy(a => a.App.Owner.Name, StringComparer.CurrentCultureIgnoreCase),
                // Longest unopened first; apps with no record at all come before any with a date.
                InstalledSort.LastOpened => shown
                    .OrderBy(a => a.Usage.Verdict == UsageVerdict.RunningNow ? 1 : 0)
                    .ThenBy(a => a.Usage.LastOpened ?? DateTimeOffset.MinValue)
                    .ThenBy(a => a.App.Owner.Name, StringComparer.CurrentCultureIgnoreCase),
                _ => shown.OrderByDescending(a => a.TotalBytes)
                    .ThenBy(a => a.App.Owner.Name, StringComparer.CurrentCultureIgnoreCase),
            };

            var existing = Rows.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
            Rows.Clear();
            foreach (var app in sorted)
            {
                var row = existing.GetValueOrDefault(app.Id) ?? new InstalledRowViewModel(app.Id);
                row.Update(app, largest);
                Rows.Add(row);
            }
        }

        IsEmpty = Rows.Count == 0;
        if (search.Length > 0)
        {
            EmptyTitle = "No matches";
            EmptyText = $"No installed app contains “{search}”. Clear the search to see everything.";
        }
        else if (Filter == InstalledFilter.NotOpenedLately)
        {
            // Without records the note above the list already says why it is empty.
            EmptyTitle = inventory.CoversFrom is null ? "Nothing to list yet" : "Nothing to show";
            EmptyText = inventory.CoversFrom is { } since
                ? $"Every app you can open has been opened since {since:d MMMM}."
                : "";
        }
        else
        {
            EmptyTitle = "Nothing here";
            EmptyText = "No installed apps were found.";
        }
    }

    private void ShowEvidenceNote(InventoryState inventory)
    {
        ShowEvidence = inventory.IsReady && Filter == InstalledFilter.NotOpenedLately;
        NeedsAdminCheck = inventory.CoversFrom is null && !inventory.HasAdminCheck;
        if (inventory.CoversFrom is { } since)
        {
            EvidenceTitle = $"No sign of being opened since {since:d MMMM}";
            EvidenceText =
                "Only apps with something to open are listed: drivers and runtimes are never called unused. " +
                "An app opened in a way Windows does not record can be missed, so check before removing anything.";
        }
        else if (inventory.ReachesBackTo is { } reach)
        {
            EvidenceTitle = "The records are too short to say";
            EvidenceText =
                $"Windows only remembers which programs ran since {reach:d MMMM} on this PC. " +
                "That is too short to call any app unused.";
        }
        else
        {
            EvidenceTitle = "Dashio cannot tell yet";
            EvidenceText =
                "Windows is not keeping a list of the apps you open on this PC. " +
                "With administrator rights Dashio can read which programs Windows has run lately instead.";
        }
    }

    private void ShowDisk()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\";
            var drive = new DriveInfo(root);
            DiskValue = $"{UsageText.Memory(drive.AvailableFreeSpace)} free";
            DiskDetail = $"of {UsageText.Memory(drive.TotalSize)} on {root.TrimEnd('\\')}";
            DiskUsedPercent = drive.TotalSize <= 0 ? 0 : 100.0 * (drive.TotalSize - drive.AvailableFreeSpace) / drive.TotalSize;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            DiskValue = "";
            DiskDetail = "";
        }
    }
}
