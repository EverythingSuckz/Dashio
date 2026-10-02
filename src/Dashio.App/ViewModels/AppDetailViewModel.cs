using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Dashio.App.Services;
using Dashio.Core.Models;
using Dashio.Core.Parsing;
using Microsoft.UI.Xaml.Media;

namespace Dashio.App.ViewModels;

/// <summary>One row of the "About this app" panel.</summary>
public sealed record AboutRow(string Glyph, string Label, string Value)
{
    public string AccessibleName => $"{Label}: {Value}";
}

public sealed partial class AppDetailViewModel : ObservableObject
{
    private string _groupId = "";

    /// <summary>Section headings and item rows in one flat list, so the list can be virtualised.</summary>
    public ObservableCollection<object> Rows { get; } = [];

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string Publisher { get; set; } = "";

    [ObservableProperty]
    public partial ImageSource? Icon { get; set; }

    // The labels under the app's name.
    [ObservableProperty]
    public partial bool IsHidden { get; set; }

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial string RunningText { get; set; } = "";

    [ObservableProperty]
    public partial string StartsText { get; set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<AboutRow> About { get; set; } = [];

    [ObservableProperty]
    public partial string Note { get; set; } = "";

    [ObservableProperty]
    public partial bool HasNote { get; set; }

    [ObservableProperty]
    public partial string TurnAllLabel { get; set; } = "Turn off all";

    [ObservableProperty]
    public partial string TurnAllGlyph { get; set; } = "\uE7E8";

    [ObservableProperty]
    public partial string TurnAllTip { get; set; } = "";

    [ObservableProperty]
    public partial bool CanTurnAll { get; set; }

    /// <summary>False when the app is no longer in the scan, for example after a rescan.</summary>
    public bool Exists { get; private set; }

    private IEnumerable<ItemRowViewModel> Items => Rows.OfType<ItemRowViewModel>();

    public void Load(string groupId)
    {
        _groupId = groupId;
        Rebuild();
    }

    public void Rebuild()
    {
        var group = AppServices.State.FindGroup(_groupId);
        Exists = group is not null;
        Rows.Clear();
        if (group is null)
            return;

        Name = group.Name;
        Publisher = string.IsNullOrWhiteSpace(group.Publisher) ? "Unknown publisher" : group.Publisher;
        IsHidden = group.HiddenFromTaskManager;
        IsRunning = group.RunningCount > 0;
        RunningText = $"{group.RunningCount} running";
        StartsText = StartsLine(group);
        About = AboutRows(group);
        Note = NoteFor(group);
        HasNote = Note.Length > 0;
        _ = LoadIconAsync(group.IconPath);

        foreach (var kind in group.Items.GroupBy(i => i.Item.Kind).OrderBy(g => g.Key))
        {
            var on = kind.Count(i => i.Item.Enabled);
            Rows.Add(new SectionHeader(
                ItemText.KindGlyph(kind.Key),
                ItemText.KindHeading(kind.Key),
                $"{on} of {kind.Count()} on",
                ItemText.KindExplanation(kind.Key)));
            foreach (var item in kind)
                Rows.Add(new ItemRowViewModel(item, group));
        }
        RefreshPending();
    }

    private async Task LoadIconAsync(string? path) => Icon = await AppServices.Icons.GetAsync(path);

    /// <summary>Call when the queued changes change, so switches and the button follow.</summary>
    public void RefreshPending()
    {
        foreach (var row in Items)
            row.Refresh();

        var changeable = Items.Where(i => i.CanChange).ToList();
        var anyOn = changeable.Any(i => i.IsOn);
        CanTurnAll = changeable.Count > 0;
        var turnOff = anyOn || changeable.Count == 0;
        TurnAllLabel = turnOff ? "Turn off all" : "Turn all back on";
        TurnAllGlyph = turnOff ? "\uE7E8" : "\uE768";
        TurnAllTip = turnOff
            ? "Queues every item of this app to be turned off. Nothing changes until you apply."
            : "Queues every item of this app to be turned on. Nothing changes until you apply.";
    }

    /// <summary>Queues everything off, or everything back on when it is all off already.</summary>
    public void ToggleAll()
    {
        if (AppServices.State.FindGroup(_groupId) is { } group)
            ItemActions.ToggleAll(group);
    }

    private static string StartsLine(AppGroup group)
    {
        var count = group.Items.Count;
        var starts = group.StartsWithWindowsCount;
        return (count, starts) switch
        {
            (_, 0) => "Nothing starts with Windows",
            (1, _) => "Starts with Windows",
            _ when starts == count => $"All {count} start with Windows",
            _ => $"{starts} of {count} start with Windows",
        };
    }

    private static List<AboutRow> AboutRows(AppGroup group)
    {
        var rows = new List<AboutRow>
        {
            new("\uE8FD", "Items", group.Items.Count.ToString()),
            new("\uE7E8", "Start with Windows", group.StartsWithWindowsCount.ToString()),
            new("\uE768", "Running now", group.RunningCount.ToString()),
        };

        // Installers often register the same app twice (32- and 64-bit, or with and without "®").
        var sources = group.Sources
            .Select(s => (s.Kind, Name: NameTokens.CleanAppName(s.Name)))
            .DistinctBy(s => (s.Kind, s.Name.ToLowerInvariant()));
        foreach (var (kind, name) in sources)
        {
            rows.Add(kind switch
            {
                AppSourceKind.StorePackage => new AboutRow("\uE719", "Store app", name),
                AppSourceKind.DriverPackage => new AboutRow("\uE964", "Driver package", name),
                _ => new AboutRow("\uE74C", "Installed app", name),
            });
        }

        rows.Add(new AboutRow("\uE8FB", "How it was matched", group switch
        {
            { IsWindows: true } => "Signed as part of Windows",
            { IsUnmatched: true } => "Not matched to any app",
            { IsVendorBucket: true } => "By the maker only",
            { Confidence: Confidence.High } => "By where its files are installed",
            { Confidence: Confidence.Medium } => "By the folder its files share",
            _ => "Partly by name; expand an item to check",
        }));
        return rows;
    }

    private static string NoteFor(AppGroup group)
    {
        if (group.IsWindows)
            return "These are parts of Windows. They are shown for reference and cannot be changed here.";
        if (group.IsUnmatched)
            return "Dashio could not tell which app these belong to. Expand an item to see what it runs.";
        if (group.IsVendorBucket)
            return "The maker of these items is known, but not which of its products they belong to.";
        return "";
    }
}
