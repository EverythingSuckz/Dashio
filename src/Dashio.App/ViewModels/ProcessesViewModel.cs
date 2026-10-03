using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Dashio.App.Services;
using Dashio.Core.Processes;
using Microsoft.UI.Xaml.Media;

namespace Dashio.App.ViewModels;

/// <summary>One running app in the Processes list. Its processes appear under it when it is expanded.</summary>
public sealed partial class ProcessGroupViewModel : ObservableObject
{
    private string? _iconPath;

    public ProcessGroupViewModel(AppUsage usage, long totalMemory)
    {
        Id = usage.GroupId;
        Usage = usage;
        Update(usage, totalMemory);
    }

    public string Id { get; }
    public AppUsage Usage { get; private set; }

    /// <summary>The children currently in the list, in the order they are shown.</summary>
    public List<ProcessRowViewModel> Children { get; } = [];

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string CountText { get; set; } = "";

    [ObservableProperty]
    public partial string Memory { get; set; } = "";

    [ObservableProperty]
    public partial string Cpu { get; set; } = "";

    /// <summary>How strongly the memory cell is tinted, 0 to 1.</summary>
    [ObservableProperty]
    public partial double MemoryHeat { get; set; }

    [ObservableProperty]
    public partial double CpuHeat { get; set; }

    [ObservableProperty]
    public partial bool CanEnd { get; set; }

    [ObservableProperty]
    public partial bool IsWindows { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial string AccessibleName { get; set; } = "";

    [ObservableProperty]
    public partial ImageSource? Icon { get; set; }

    public string ChevronGlyph => IsExpanded ? "\uE70D" : "\uE76C";
    public string EndName => $"End {Name}";
    public string OpenName => $"Open the page of {Name}";

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ChevronGlyph));
        Describe();
    }

    public void Update(AppUsage usage, long totalMemory)
    {
        Usage = usage;
        MemoryHeat = UsageText.Heat(totalMemory <= 0 ? 0 : (double)usage.MemoryBytes / totalMemory);
        CpuHeat = UsageText.Heat(usage.CpuPercent / 100);
        Name = usage.Owner.Name;
        IsWindows = usage.Owner.IsWindows;
        CountText = usage.Processes.Count.ToString();
        Memory = UsageText.Memory(usage.MemoryBytes);
        Cpu = UsageText.Cpu(usage.CpuPercent);
        CanEnd = RunningActions.CanEnd(usage);
        Describe();

        if (_iconPath != usage.Owner.IconPath || (Icon is null && _iconPath is null && usage.Owner.IconPath is not null))
        {
            _iconPath = usage.Owner.IconPath;
            _ = LoadIconAsync(_iconPath);
        }
    }

    private void Describe() => AccessibleName =
        $"{Name}, {UsageText.Processes(Usage.Processes.Count)}, memory {Memory}, processor {Cpu}, {(IsExpanded ? "expanded" : "collapsed")}";

    private async Task LoadIconAsync(string? path)
    {
        var icon = await AppServices.Icons.GetAsync(path);
        if (path == _iconPath)
            Icon = icon;
    }
}

public enum ProcessColumn
{
    Name,
    Count,
    Memory,
    Cpu,
}

public sealed partial class ProcessesViewModel : ObservableObject
{
    /// <summary>App rows and, under an expanded app, its process rows, in one flat list so it can be virtualised.</summary>
    public ObservableCollection<object> Rows { get; } = [];

    public string Search { get; set; } = "";
    public ProcessColumn SortColumn { get; private set; } = ProcessColumn.Memory;
    public bool SortDescending { get; private set; } = true;

    [ObservableProperty]
    public partial string Summary { get; set; } = "Everything that is running, grouped by the app it belongs to.";

    [ObservableProperty]
    public partial bool IsLoading { get; set; } = true;

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial string EmptyText { get; set; } = "";

    // The headline figures.
    [ObservableProperty]
    public partial string MemoryValue { get; set; } = "";

    [ObservableProperty]
    public partial string MemoryDetail { get; set; } = "memory in use";

    [ObservableProperty]
    public partial string CpuValue { get; set; } = "";

    [ObservableProperty]
    public partial string CountValue { get; set; } = "";

    private IEnumerable<ProcessGroupViewModel> Groups => Rows.OfType<ProcessGroupViewModel>();

    private static long TotalMemory => AppServices.Monitor.Latest?.System.MemoryTotalBytes ?? 0;

    /// <summary>Sorts by the column, or reverses the order when it is already the sort column.</summary>
    public void SortBy(ProcessColumn column)
    {
        // Figures read best largest first, names from A.
        SortDescending = SortColumn == column ? !SortDescending : column != ProcessColumn.Name;
        SortColumn = column;
        Rebuild();
    }

    /// <summary>Builds the list again in sorted order. Called when the user asks for an order, not on every reading.</summary>
    public void Rebuild()
    {
        var latest = AppServices.Monitor.Latest;
        ShowHeadlines(latest);
        IsLoading = latest is null || latest.Apps.Count == 0;
        if (IsLoading)
            return;

        var expanded = Groups.Where(g => g.IsExpanded).Select(g => g.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existing = Groups.ToDictionary(g => g.Id, StringComparer.OrdinalIgnoreCase);

        Rows.Clear();
        foreach (var usage in Sorted(latest!.Apps.Where(Matches)))
        {
            var group = existing.GetValueOrDefault(usage.GroupId) ?? new ProcessGroupViewModel(usage, TotalMemory);
            group.Update(usage, TotalMemory);
            group.Children.Clear();
            // A search that found the app through one of its programs shows that program straight away.
            group.IsExpanded = expanded.Contains(group.Id) || (Search.Length > 0 && !NameMatches(usage));
            Rows.Add(group);
            if (group.IsExpanded)
                AddChildren(group, Rows.Count);
        }
        ShowEmpty();
    }

    /// <summary>Updates the figures in place after a reading, so rows keep their places.</summary>
    public void Update()
    {
        var latest = AppServices.Monitor.Latest;
        if (IsLoading || latest is null)
        {
            Rebuild();
            return;
        }
        ShowHeadlines(latest);

        var current = latest.Apps.Where(Matches).ToDictionary(a => a.GroupId, StringComparer.OrdinalIgnoreCase);
        foreach (var group in Groups.ToList())
        {
            if (!current.Remove(group.Id, out var usage))
            {
                RemoveGroup(group);
                continue;
            }
            group.Update(usage, TotalMemory);
            if (group.IsExpanded)
                SyncChildren(group);
        }

        foreach (var usage in current.Values)
            InsertGroup(new ProcessGroupViewModel(usage, TotalMemory));
        ShowEmpty();
    }

    public void Toggle(ProcessGroupViewModel group)
    {
        var index = Rows.IndexOf(group);
        if (index < 0)
            return;

        group.IsExpanded = !group.IsExpanded;
        if (group.IsExpanded)
        {
            AddChildren(group, index + 1);
            return;
        }
        foreach (var child in group.Children)
            Rows.Remove(child);
        group.Children.Clear();
    }

    private void AddChildren(ProcessGroupViewModel group, int index)
    {
        foreach (var process in ShownProcesses(group.Usage))
        {
            var row = new ProcessRowViewModel(process, group.CanEnd, group.Id, TotalMemory);
            group.Children.Add(row);
            Rows.Insert(index++, row);
        }
    }

    private void SyncChildren(ProcessGroupViewModel group)
    {
        var current = ShownProcesses(group.Usage).ToDictionary(p => (p.Pid, p.StartTicks));
        foreach (var child in group.Children.ToList())
        {
            if (current.Remove((child.Usage.Pid, child.Usage.StartTicks), out var process))
            {
                child.Update(process, TotalMemory);
                continue;
            }
            group.Children.Remove(child);
            Rows.Remove(child);
        }

        var index = Rows.IndexOf(group) + 1 + group.Children.Count;
        foreach (var process in current.Values)
        {
            var row = new ProcessRowViewModel(process, group.CanEnd, group.Id, TotalMemory);
            group.Children.Add(row);
            Rows.Insert(index++, row);
        }
    }

    private void RemoveGroup(ProcessGroupViewModel group)
    {
        foreach (var child in group.Children)
            Rows.Remove(child);
        Rows.Remove(group);
    }

    /// <summary>A newly started app goes where the current order would put it; nothing else moves.</summary>
    private void InsertGroup(ProcessGroupViewModel added)
    {
        var comparer = Comparer();
        var index = Rows.Count;
        for (var i = 0; i < Rows.Count; i++)
        {
            if (Rows[i] is ProcessGroupViewModel group && comparer.Compare(added.Usage, group.Usage) < 0)
            {
                index = i;
                break;
            }
        }
        Rows.Insert(index, added);
    }

    private IEnumerable<AppUsage> Sorted(IEnumerable<AppUsage> apps) => apps.Order(Comparer());

    private Comparer<AppUsage> Comparer()
    {
        var names = StringComparer.CurrentCultureIgnoreCase;
        Comparison<AppUsage> byColumn = SortColumn switch
        {
            ProcessColumn.Name => (a, b) => names.Compare(a.Owner.Name, b.Owner.Name),
            ProcessColumn.Count => (a, b) => a.Processes.Count.CompareTo(b.Processes.Count),
            ProcessColumn.Cpu => (a, b) => a.CpuPercent.CompareTo(b.CpuPercent),
            _ => (a, b) => a.MemoryBytes.CompareTo(b.MemoryBytes),
        };
        var direction = SortDescending ? -1 : 1;
        return Comparer<AppUsage>.Create((a, b) =>
        {
            var result = direction * byColumn(a, b);
            return result != 0 ? result : names.Compare(a.Owner.Name, b.Owner.Name);
        });
    }

    private bool NameMatches(AppUsage usage) => ShellState.Matches(Search, usage.Owner.Name, usage.Owner.Publisher);

    private bool Matches(AppUsage usage) => NameMatches(usage) || usage.Processes.Any(Matches);

    private bool Matches(ProcessUsage process) =>
        ShellState.Matches(Search, process.Name) || process.Services.Any(s => ShellState.Matches(Search, s));

    /// <summary>Every process of the app, or only the ones the search found when the app's own name did not match.</summary>
    private IEnumerable<ProcessUsage> ShownProcesses(AppUsage usage) =>
        Search.Length == 0 || NameMatches(usage) ? usage.Processes : usage.Processes.Where(Matches);

    private void ShowHeadlines(UsageSnapshot? latest)
    {
        if (latest is null)
            return;
        var system = latest.System;
        MemoryValue = UsageText.Memory(system.MemoryUsedBytes);
        MemoryDetail = $"of {UsageText.Memory(system.MemoryTotalBytes)} memory in use";
        CpuValue = UsageText.Cpu(system.CpuPercent);
        var processes = latest.Apps.Sum(a => a.Processes.Count);
        CountValue = processes == 0 ? "" : processes.ToString();

        var seconds = AppServices.Settings.RefreshSeconds;
        var pace = seconds switch
        {
            0 => "Live figures are paused; these are from when you opened the page.",
            1 => "Updated every second.",
            _ => $"Updated every {seconds} seconds.",
        };
        Summary = Search.Length > 0
            ? $"Showing what matches “{Search}”. {pace}"
            : $"Everything that is running, grouped by the app it belongs to. {pace}";
    }

    private void ShowEmpty()
    {
        IsEmpty = Rows.Count == 0;
        EmptyText = Search.Length > 0
            ? $"Nothing running contains “{Search}”. Clear the filter to see everything."
            : "Nothing was found running.";
    }
}
