using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Dashio.App.Services;
using Dashio.Core.Models;
using Microsoft.UI.Xaml;

namespace Dashio.App.ViewModels;

public enum ItemColumn
{
    Name,
    Type,
    App,
    Starts,
    Memory,
    State,
}

/// <summary>Which columns of the startup table fit the page. Shared by the headings and every row.</summary>
public sealed partial class StartupColumns : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TypeWidth))]
    public partial bool ShowType { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartsWidth))]
    public partial bool ShowStarts { get; set; } = true;

    public GridLength TypeWidth => new(ShowType ? 0.9 : 0, GridUnitType.Star);
    public GridLength StartsWidth => new(ShowStarts ? 1.6 : 0, GridUnitType.Star);
}

/// <summary>What the Startup page is opened on: a kind of item, a filter text, or both.</summary>
public sealed record StartupTarget(AutostartKind? Kind = null, string? Search = null, bool NewOnly = false);

public sealed partial class StartupViewModel : ObservableObject
{
    public StartupColumns Columns { get; } = new();
    public string Search { get; set; } = "";

    public ObservableCollection<ItemRowViewModel> Rows { get; } = [];

    /// <summary>Null shows every kind.</summary>
    public AutostartKind? Kind { get; set; }

    /// <summary>Only the items an app added since Dashio first looked.</summary>
    public bool NewOnly { get; set; }

    [ObservableProperty]
    public partial bool HasNew { get; set; }

    [ObservableProperty]
    public partial string NewLabel { get; set; } = "New";
    public ItemColumn SortColumn { get; private set; } = ItemColumn.App;
    public bool SortDescending { get; private set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = "Everything set to start by itself: services, scheduled tasks and startup entries.";

    /// <summary>How many items each tab holds, by kind name. The empty name stands for every kind.</summary>
    public IReadOnlyDictionary<string, int> Counts { get; private set; } = new Dictionary<string, int>();

    [ObservableProperty]
    public partial bool IsLoading { get; set; } = true;

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial string EmptyText { get; set; } = "";

    /// <summary>Sorts by the column, or reverses the order when it is already the sort column.</summary>
    public void SortBy(ItemColumn column)
    {
        SortDescending = SortColumn == column && !SortDescending;
        SortColumn = column;
        Rebuild();
    }

    public void Rebuild()
    {
        var state = AppServices.State;
        IsLoading = !state.HasScanned;
        if (!state.HasScanned)
            return;

        var search = Search;
        var matching = state.Groups
            .Where(g => AppServices.Settings.ShowWindowsComponents || !g.IsWindows)
            .SelectMany(g => g.Items.Select(i => new ItemRowViewModel(i, g, Columns)))
            .Where(r => ShellState.Matches(search, r.Name, r.Item.Name, r.AppName, r.Publisher))
            .ToList();
        var counts = matching.GroupBy(r => r.Item.Kind).ToDictionary(g => g.Key.ToString(), g => g.Count());
        counts[""] = matching.Count;
        Counts = counts;
        var newCount = matching.Count(r => r.IsNew);
        HasNew = newCount > 0 || NewOnly;
        NewLabel = $"New ({newCount})";
        var rows = matching.Where(r => (Kind is null || r.Item.Kind == Kind) && (!NewOnly || r.IsNew)).ToList();
        ShowUsage(rows);

        Func<ItemRowViewModel, string> key = SortColumn switch
        {
            ItemColumn.Type => r => r.KindName,
            ItemColumn.App => r => r.AppName,
            ItemColumn.Starts => r => r.Item.Enabled ? r.TriggerText : "~",
            ItemColumn.State => r => r.StateText,
            _ => r => r.Name,
        };
        var comparer = StringComparer.CurrentCultureIgnoreCase;
        var sorted = SortDescending
            ? rows.OrderByDescending(key, comparer).ThenBy(r => r.Name, comparer)
            : rows.OrderBy(key, comparer).ThenBy(r => r.Name, comparer);
        // The most memory first; pressing the heading again turns it round.
        if (SortColumn == ItemColumn.Memory)
        {
            sorted = SortDescending
                ? rows.OrderBy(r => r.MemoryBytes).ThenBy(r => r.Name, comparer)
                : rows.OrderByDescending(r => r.MemoryBytes).ThenBy(r => r.Name, comparer);
        }

        Rows.Clear();
        foreach (var row in sorted)
        {
            Rows.Add(row);
            _ = row.LoadAppIconAsync();
        }

        var apps = rows.Select(r => r.Group.Id).Distinct().Count();
        var on = rows.Count(r => r.Item.Enabled);
        Summary = $"{ItemText.Plural(rows.Count, "item")} across {ItemText.Plural(apps, "app")}. {on} turned on.";
        IsEmpty = rows.Count == 0;
        EmptyText = search.Length > 0
            ? $"Nothing contains “{search}”. Clear the filter to see everything."
            : NewOnly ? "Nothing new has been set to start by itself lately."
            : "No items of this type were found.";
    }

    /// <summary>Call after each measurement: every row says what its program is using now.</summary>
    public void UpdateUsage() => ShowUsage(Rows);

    /// <summary>
    /// A service is found through the process that hosts it; anything else through the file it
    /// runs. That ties "this starts by itself" to "and this is what it costs right now".
    /// </summary>
    private static void ShowUsage(IEnumerable<ItemRowViewModel> rows)
    {
        if (AppServices.Monitor.Latest is not { } latest)
            return;

        var byService = new Dictionary<string, (long Bytes, int Services)>(StringComparer.OrdinalIgnoreCase);
        var byFile = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in latest.Apps.SelectMany(a => a.Processes))
        {
            foreach (var service in process.Services)
                byService[service] = (process.MemoryBytes, process.Services.Count);
            if (process.Path is { } path)
                byFile[path] = byFile.GetValueOrDefault(path) + process.MemoryBytes;
        }

        foreach (var row in rows)
        {
            if (row.Item.Kind == AutostartKind.Service)
            {
                if (byService.TryGetValue(row.Item.Name, out var host))
                    row.ShowUsage(host.Bytes, host.Services - 1);
                else
                    row.ShowUsage(null, 0);
            }
            else if (row.Item.TargetPath is { } target && byFile.TryGetValue(target, out var bytes))
            {
                row.ShowUsage(bytes, 0);
            }
            else
            {
                row.ShowUsage(null, 0);
            }
        }
    }

    public void RefreshPending()
    {
        foreach (var row in Rows)
            row.Refresh();
    }
}
