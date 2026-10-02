using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Dashio.App.Services;
using Dashio.Core.Models;

namespace Dashio.App.ViewModels;

public enum ItemColumn
{
    Name,
    Type,
    App,
    Starts,
    State,
}

public sealed partial class AllItemsViewModel : ObservableObject
{
    public ObservableCollection<ItemRowViewModel> Rows { get; } = [];

    /// <summary>Null shows every kind.</summary>
    public AutostartKind? Kind { get; set; }
    public ItemColumn SortColumn { get; private set; } = ItemColumn.App;
    public bool SortDescending { get; private set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

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

        var search = AppServices.Shell.SearchText;
        var rows = state.Groups
            .Where(g => AppServices.Settings.ShowWindowsComponents || !g.IsWindows)
            .SelectMany(g => g.Items.Select(i => new ItemRowViewModel(i, g)))
            .Where(r => Kind is null || r.Item.Kind == Kind)
            .Where(r => ShellState.Matches(search, r.Name, r.Item.Name, r.AppName, r.Publisher))
            .ToList();

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
            ? $"Nothing contains “{search}”. Clear the search to see everything."
            : "No items of this type were found.";
    }

    public void RefreshPending()
    {
        foreach (var row in Rows)
            row.Refresh();
    }
}
