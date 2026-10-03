using CommunityToolkit.Mvvm.ComponentModel;
using Dashio.App.ViewModels;
using Microsoft.UI.Xaml.Media;

namespace Dashio.App.Services;

public enum SearchTarget
{
    None,
    Page,
    App,
    Process,
    StartupItem,
    Setting,
}

/// <summary>One suggestion under the search box: a place in Dashio to jump to.</summary>
public sealed partial class SearchResult : ObservableObject
{
    public required SearchTarget Target { get; init; }
    public required string Title { get; init; }
    public required string Caption { get; init; }
    public required string Glyph { get; init; }

    /// <summary>A page tag, an app id, or the name of a control on the Settings page.</summary>
    public string? Id { get; init; }

    /// <summary>The filter text the opened page starts with.</summary>
    public string? Text { get; init; }

    public string? IconPath { get; init; }

    [ObservableProperty]
    public partial ImageSource? Icon { get; set; }

    public string AccessibleName => $"{Title}, {Caption}";

    public override string ToString() => Title;

    public async Task LoadIconAsync()
    {
        if (IconPath is not null)
            Icon = await AppServices.Icons.GetAsync(IconPath);
    }
}

/// <summary>Finds apps, running programs, startup items, pages and settings by name.</summary>
public static class SearchIndex
{
    private const int PerKind = 4;

    private sealed record Place(string Title, string Caption, string Glyph, string Id, string Keywords);

    private static readonly Place[] Pages =
    [
        new("Overview", "Page", "\uE80F", "overview", "home dashboard summary"),
        new("Processes", "Page", "\uE9D9", "processes", "running task manager memory processor cpu ram end"),
        new("Apps", "Page", "\uE71D", "apps", "programs installed software"),
        new("Storage", "Page", "\uEDA2", "storage", "disk space size drive folders"),
        new("Startup", "Page", "\uE7E8", "startup", "services scheduled tasks autostart boot sign in"),
        new("History", "Page", "\uE81C", "history", "changes undo log recent actions"),
        new("Settings", "Page", "\uE713", "settings", "options preferences"),
    ];

    private static readonly Place[] Settings =
    [
        new("Theme", "Setting", "\uE790", "ThemeBox", "light dark appearance colour color mode"),
        new("Show Windows components", "Setting", "\uE890", "ShowWindowsSwitch", "system microsoft parts hide"),
        new("Scan again now", "Setting", "\uE72C", "RescanButton", "refresh rescan reload update f5"),
        new("Scan with administrator rights", "Setting", "\uEA18", "AdminScanButton", "admin hidden tasks elevated"),
        new("Check when apps were last opened", "Setting", "\uE823", "UsageCheckButton", "unused last opened admin prefetch"),
        new("Refresh every", "Setting", "\uE72C", "RefreshBox", "interval live update pause seconds measure"),
        new("Change log", "Setting", "\uE8B7", "LogCard", "journal folder file history"),
    ];

    public static List<SearchResult> Find(string query)
    {
        query = query.Trim();
        var results = new List<SearchResult>();
        if (query.Length == 0)
            return results;

        var showWindows = AppServices.Settings.ShowWindowsComponents;

        results.AddRange(Best(AppCatalog.All(showWindows), query, a => a.Name, a => a.Publisher)
            .Select(a => new SearchResult
            {
                Target = SearchTarget.App,
                Title = a.Name,
                Caption = string.IsNullOrWhiteSpace(a.Publisher) ? "App" : $"App · {a.Publisher}",
                Glyph = "\uECAA",
                Id = a.Id,
                IconPath = a.IconPath,
            }));

        var programs = (AppServices.Monitor.Latest?.Apps ?? [])
            .SelectMany(a => a.Processes.Select(p => (App: a.Owner.Name, p.Name)))
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, g.First().App, Count: g.Count()))
            .ToList();
        results.AddRange(Best(programs, query, p => p.Name, _ => null)
            .Select(p => new SearchResult
            {
                Target = SearchTarget.Process,
                Title = p.Name,
                Caption = p.Count == 1 ? $"Running now · {p.App}" : $"{p.Count} running now · {p.App}",
                Glyph = "\uE9D9",
                Text = p.Name,
            }));

        var items = AppServices.State.Groups
            .Where(g => showWindows || !g.IsWindows)
            .SelectMany(g => g.Items.Select(i => (Group: g, i.Item)))
            .ToList();
        results.AddRange(Best(items, query, i => i.Item.DisplayName, i => i.Item.Name)
            .Select(i => new SearchResult
            {
                Target = SearchTarget.StartupItem,
                Title = i.Item.DisplayName,
                Caption = $"{ItemText.KindName(i.Item.Kind)} · {i.Group.Name}",
                Glyph = ItemText.KindGlyph(i.Item.Kind),
                Text = i.Item.DisplayName,
            }));

        results.AddRange(Best(Pages, query, p => p.Title, p => p.Keywords).Select(p => ToResult(p, SearchTarget.Page)));
        results.AddRange(Best(Settings, query, p => p.Title, p => p.Keywords).Select(p => ToResult(p, SearchTarget.Setting)));
        return results;
    }

    private static SearchResult ToResult(Place place, SearchTarget target) => new()
    {
        Target = target,
        Title = place.Title,
        Caption = place.Caption,
        Glyph = place.Glyph,
        Id = place.Id,
    };

    /// <summary>Names that start with the text come before names that only contain it.</summary>
    private static IEnumerable<T> Best<T>(IEnumerable<T> source, string query, Func<T, string> name, Func<T, string?> other) =>
        source
            .Select(x => (Item: x, Rank: Rank(name(x), other(x), query)))
            .Where(x => x.Rank < 3)
            .OrderBy(x => x.Rank)
            .ThenBy(x => name(x.Item), StringComparer.CurrentCultureIgnoreCase)
            .Take(PerKind)
            .Select(x => x.Item);

    private static int Rank(string name, string? other, string query)
    {
        const StringComparison Compare = StringComparison.CurrentCultureIgnoreCase;
        if (name.StartsWith(query, Compare))
            return 0;
        if (name.Contains(query, Compare))
            return 1;
        return other is not null && other.Contains(query, Compare) ? 2 : 3;
    }
}
