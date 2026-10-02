using CommunityToolkit.Mvvm.ComponentModel;
using Dashio.App.Services;
using Dashio.Core.Collectors;
using Dashio.Core.Models;
using Microsoft.UI.Xaml.Media;

namespace Dashio.App.ViewModels;

/// <summary>A small icon with a count, such as "3 services".</summary>
public sealed record KindCount(string Glyph, string Count, string Description);

/// <summary>One app in the Apps list.</summary>
public sealed partial class AppRowViewModel : ObservableObject
{
    public AppRowViewModel(AppGroup group)
    {
        Group = group;
        Kinds = group.Items
            .GroupBy(i => i.Item.Kind)
            .OrderBy(g => g.Key)
            .Select(g => new KindCount(
                ItemText.KindGlyph(g.Key), g.Count().ToString(), ItemText.KindCount(g.Key, g.Count())))
            .ToList();
        UpdateUsage();
        _ = LoadIconAsync();
    }

    public AppGroup Group { get; }
    public string Name => Group.Name;
    public string Publisher => string.IsNullOrWhiteSpace(Group.Publisher) ? "Unknown publisher" : Group.Publisher;

    /// <summary>Which kinds of item the app has, and how many of each.</summary>
    public IReadOnlyList<KindCount> Kinds { get; }

    public int ItemCount => Group.Items.Count;
    public int StartsCount => Group.StartsWithWindowsCount;
    public int RunningCount => Group.RunningCount;

    public bool IsRunning => RunningCount > 0;
    public bool StartsWithWindows => StartsCount > 0;
    public bool IsHidden => Group.HiddenFromTaskManager;

    public int AdminFoundCount => Group.Items.Count(i => i.Item.VisibleOnlyWithAdmin || i.Item.IsHiddenTask);
    public bool IsAdminFound => AdminFoundCount > 0;
    public bool HasHiddenTask => Group.Items.Any(i => i.Item.IsHiddenTask);
    public string AdminFoundText => HasHiddenTask ? "Hidden task" : "Admin scan";
    public string AdminFoundTip => HasHiddenTask
        ? "Has a task that Task Scheduler does not list. Only the admin scan could see it."
        : $"{ItemText.Plural(AdminFoundCount, "item")} found only by the admin scan";

    public string RunningText => $"{RunningCount} running";
    public string StartsText => StartsCount == ItemCount
        ? $"{ItemText.Plural(StartsCount, "item")} at startup"
        : $"{StartsCount} of {ItemCount} at startup";
    public string ItemsText => $"{ItemText.Plural(ItemCount, "item")}, none at startup";

    public string AccessibleName
    {
        get
        {
            var parts = new List<string> { Name, Publisher };
            parts.Add(StartsWithWindows ? StartsText : ItemsText);
            if (IsRunning)
                parts.Add(RunningText);
            if (IsHidden)
                parts.Add("not shown in Task Manager");
            if (IsAdminFound)
                parts.Add(HasHiddenTask ? "has a hidden task" : "found by the admin scan");
            return string.Join(", ", parts);
        }
    }

    // The green badge: live figures while the app is running, otherwise how many items the scan saw running.
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    public partial string ActivityText { get; set; } = "";

    [ObservableProperty]
    public partial string ActivityTip { get; set; } = "";

    /// <summary>Call after each measurement.</summary>
    public void UpdateUsage()
    {
        if (AppServices.Monitor.UsageOf(Group.Id) is { } usage)
        {
            IsActive = true;
            ActivityText = $"{UsageText.Memory(usage.MemoryBytes)} · {UsageText.Cpu(usage.CpuPercent)}";
            ActivityTip = $"Running now: {UsageText.Processes(usage.Processes.Count)} using {UsageText.Memory(usage.MemoryBytes)} of memory and {UsageText.Cpu(usage.CpuPercent)} of the processor";
        }
        else
        {
            IsActive = IsRunning;
            ActivityText = RunningText;
            ActivityTip = $"{ItemText.Plural(RunningCount, "item")} of this app were running at the last scan";
        }
    }

    public bool CanToggleAll => Group.Items.Any(i => !i.Item.IsProtected && !i.Item.IsHiddenTask);
    public string ToggleAllLabel => ItemActions.AnyOn(Group) ? "Turn off everything" : "Turn everything back on";

    [ObservableProperty]
    public partial ImageSource? Icon { get; set; }

    private async Task LoadIconAsync() => Icon = await AppServices.Icons.GetAsync(Group.IconPath);
}

public enum DetailTone
{
    None,
    Good,
    Warning,
}

/// <summary>One line of an item's details: an icon, a label and a formatted value.</summary>
/// <param name="IsCode">The value is a path or command, shown in a monospace font.</param>
/// <param name="RevealPath">When set, the row offers to show this file in File Explorer.</param>
public sealed record DetailRow(
    string Glyph, string Label, string Value,
    bool IsCode = false, bool CanCopy = false, string? RevealPath = null, DetailTone Tone = DetailTone.None)
{
    /// <summary>False for the first line, which has no line above it to be separated from.</summary>
    public bool HasDivider { get; init; } = true;

    /// <summary>A hairline above the row, except on the first.</summary>
    public Microsoft.UI.Xaml.Thickness Divider => new(0, HasDivider ? 1 : 0, 0, 0);

    public bool IsText => !IsCode;
    public bool CanReveal => RevealPath is not null;
    public bool IsGood => Tone == DetailTone.Good;
    public bool IsWarning => Tone == DetailTone.Warning;
    public string CopyName => $"Copy {Label.ToLowerInvariant()}";
    public string AccessibleName => $"{Label}: {Value}";
}

/// <summary>One autostart item, with the on/off state the user has asked for.</summary>
public sealed partial class ItemRowViewModel : ObservableObject
{
    private readonly PendingChanges _pending = AppServices.Pending;
    private IReadOnlyList<DetailRow>? _details;

    public ItemRowViewModel(AttributedItem attributed, AppGroup group)
    {
        Attributed = attributed;
        Group = group;
    }

    public AttributedItem Attributed { get; }
    public AppGroup Group { get; }
    public AutostartItem Item => Attributed.Item;

    public string Name => Item.DisplayName;
    public string AppName => Group.Name;
    public string Publisher => Group.Publisher ?? "";
    public string KindName => ItemText.KindName(Item.Kind);
    public string KindGlyph => ItemText.KindGlyph(Item.Kind);
    public string TriggerText => ItemText.Trigger(Item);
    public string TriggerGlyph => ItemText.TriggerGlyph(Item);

    public bool CanChange => !Item.IsProtected && !Item.IsHiddenTask;
    public bool NeedsAdmin => Item.RequiresAdmin && CanChange;
    public bool IsPending => _pending.Desired(Item.Id) is not null;
    public bool IsSettled => !IsPending;

    /// <summary>What the switch shows: the queued state if there is one, otherwise the real one.</summary>
    public bool IsOn
    {
        get => _pending.Desired(Item.Id) ?? Item.Enabled;
        set
        {
            if (value == IsOn)
                return;
            _pending.SetDesired(Item, AppName, value);
            Refresh();
        }
    }

    public bool IsEnabled => Item.Enabled;
    public bool IsDisabled => !Item.Enabled;
    public string StateText => Item.Enabled ? "On" : "Off";

    // The pieces of the status line, shown as separate labels.
    public string StartsLabel => Item.Enabled ? TriggerText : "Turned off";
    public bool IsRunning => Item.IsRunning == true;
    public bool ShowRunning => IsRunning && Item.Enabled && IsSettled;
    public bool ShowStillRunning => IsRunning && !Item.Enabled && IsSettled;
    public string PendingLabel => IsOn ? "Will turn on when you apply" : "Will turn off when you apply";

    /// <summary>For the table: the queued change if there is one, otherwise when the item starts.</summary>
    public string StartsOrPending => IsPending ? PendingLabel : StartsLabel;
    public string ToggleLabel => IsOn ? "Turn off" : "Turn on";
    public bool HasFile => Item.Evidence is { Exists: true };
    public bool HasCommand => !string.IsNullOrWhiteSpace(Item.Command);

    /// <summary>The status in one sentence, for screen readers and the table.</summary>
    public string Status
    {
        get
        {
            if (IsPending)
                return PendingLabel;
            if (!Item.Enabled)
                return IsRunning ? "Turned off · Still running" : "Turned off";
            return IsRunning ? $"{TriggerText} · Running" : TriggerText;
        }
    }

    public string AccessibleName => $"{Name}, {KindName}, {Status}";

    public bool IsWindowsComponent => Item.IsProtected;
    public bool IsHiddenTask => Item.IsHiddenTask;
    public bool IsAdminOnly => Item.VisibleOnlyWithAdmin && !Item.IsHiddenTask;
    public bool IsAdminFound => Item.VisibleOnlyWithAdmin || Item.IsHiddenTask;
    public string AdminFoundLabel => Item.IsHiddenTask ? "Hidden task" : "Found by admin scan";
    public bool IsMissingFile => Item.Evidence is { Exists: false };

    /// <summary>A service installed by a driver package may be needed by the hardware.</summary>
    public bool HasDriverWarning =>
        Item.Kind == AutostartKind.Service && !Item.IsProtected && DriverPackageCollector.Locate(Item.TargetPath) is not null;

    /// <summary>The lines shown when the row is expanded. Built on first use.</summary>
    public IReadOnlyList<DetailRow> Details => _details ??= BuildDetails();

    private List<DetailRow> BuildDetails()
    {
        var rows = new List<DetailRow>();
        if (!string.IsNullOrWhiteSpace(Item.Description))
            rows.Add(new DetailRow("\uE946", "What it does", Item.Description));

        rows.Add(new DetailRow("\uE71B", "Listed here because", Attributed.Reason));

        var type = Item.Kind == AutostartKind.Service
            ? $"{KindName} · start type {ItemText.StartType(Item.ServiceStartType)}"
            : KindName;
        rows.Add(new DetailRow(KindGlyph, "Type", type));

        var evidence = Item.Evidence;
        if (Item.TargetPath is { } path)
        {
            rows.Add(new DetailRow(
                "\uE8A5", "File", path, IsCode: true, CanCopy: true,
                RevealPath: evidence is { Exists: true } ? path : null));
        }
        else
        {
            rows.Add(new DetailRow("\uE8A5", "File", "Could not be worked out from the command", Tone: DetailTone.Warning));
        }

        rows.Add(evidence switch
        {
            null => new DetailRow("\uEB95", "Signed by", "No file to check"),
            { Exists: false } => new DetailRow("\uEB95", "Signed by", "The file no longer exists", Tone: DetailTone.Warning),
            { Signer: null } => new DetailRow(
                "\uEB95", "Signed by", "Not signed, or the signature is not trusted", Tone: DetailTone.Warning),
            { SignerIsGeneric: true } => new DetailRow(
                "\uEB95", "Signed by", $"{evidence.Signer} (a shared hardware certificate; it does not identify the maker)"),
            _ => new DetailRow("\uEB95", "Signed by", evidence.Signer, Tone: DetailTone.Good),
        });

        if (!string.IsNullOrWhiteSpace(Item.Command))
            rows.Add(new DetailRow("\uE756", "Command", Item.Command, IsCode: true, CanCopy: true));
        if (!string.IsNullOrWhiteSpace(Item.Location))
            rows.Add(new DetailRow("\uE8B7", "Stored in", Item.Location, IsCode: true, CanCopy: true));
        rows[0] = rows[0] with { HasDivider = false };
        return rows;
    }

    [ObservableProperty]
    public partial ImageSource? AppIcon { get; set; }

    /// <summary>Loads the owning app's icon, for lists that show it beside each item.</summary>
    public async Task LoadAppIconAsync() => AppIcon = await AppServices.Icons.GetAsync(Group.IconPath);

    /// <summary>Call after the pending changes change from outside this row.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(IsSettled));
        OnPropertyChanged(nameof(ShowRunning));
        OnPropertyChanged(nameof(ShowStillRunning));
        OnPropertyChanged(nameof(PendingLabel));
        OnPropertyChanged(nameof(StartsOrPending));
        OnPropertyChanged(nameof(ToggleLabel));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(AccessibleName));
    }
}

/// <summary>A heading between rows in a flat, virtualised list.</summary>
public sealed record SectionHeader(string Glyph, string Title, string Detail, string Explanation);

/// <summary>A headline number with its label and icon.</summary>
public sealed record StatTile(string Glyph, string Value, string Label);
