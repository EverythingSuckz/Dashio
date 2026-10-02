using System.Text.Json;
using System.Text.Json.Serialization;
using Dashio.Core.Changes;
using Dashio.Core.Models;

namespace Dashio.Core.Journal;

/// <summary>One change Dashio attempted, with the state before and after.</summary>
public sealed record JournalEntry
{
    public required Guid Id { get; init; }
    public required Guid BatchId { get; init; }
    public required DateTimeOffset Time { get; init; }
    public required string ItemId { get; init; }
    public required AutostartKind Kind { get; init; }
    public required string ItemName { get; init; }
    public required string AppName { get; init; }
    public required ChangeAction Action { get; init; }

    /// <summary>The item's state read from Windows just before the change.</summary>
    public required ItemState Before { get; init; }

    /// <summary>What the change asked for.</summary>
    public required ItemState Target { get; init; }

    /// <summary>The item's state read from Windows after the change; null if it could not be read.</summary>
    public ItemState? After { get; init; }

    public required JournalResult Result { get; init; }
    public string? Error { get; init; }

    /// <summary>Set when this entry undoes an earlier one.</summary>
    public Guid? UndoOf { get; init; }
}

public sealed record JournalLoad(IReadOnlyList<JournalEntry> Entries, int SkippedLines);

/// <summary>Append-only log of changes, one JSON object per line.</summary>
public sealed class ChangeJournal
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Lock _gate = new();

    public ChangeJournal(string path) => Path = path;

    public string Path { get; }

    public static string DefaultPath => DashioPaths.Journal;

    public void Append(IEnumerable<JournalEntry> entries)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.AppendAllLines(Path, entries.Select(e => JsonSerializer.Serialize(e, Json)));
        }
    }

    /// <summary>Reads every entry. A line that cannot be parsed is skipped and counted, not fatal.</summary>
    public JournalLoad Load()
    {
        lock (_gate)
        {
            if (!File.Exists(Path))
                return new JournalLoad([], 0);

            var entries = new List<JournalEntry>();
            var skipped = 0;
            foreach (var line in File.ReadLines(Path))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    if (JsonSerializer.Deserialize<JournalEntry>(line, Json) is { } entry)
                        entries.Add(entry);
                    else
                        skipped++;
                }
                catch (JsonException)
                {
                    skipped++;
                }
            }
            return new JournalLoad(entries, skipped);
        }
    }
}

/// <summary>Works out what a change or an undo should ask for.</summary>
public static class ChangePlanner
{
    /// <summary>Stops a running service now. Its start type stays as it is.</summary>
    public static PlannedChange StopNow(AutostartItem item, string appName) => new(
        item, appName, ChangeAction.Stop, new ItemState(item.Enabled, item.ServiceStartType, Running: false));

    public static ItemState DisableTarget(AutostartItem item) => item.Kind == AutostartKind.Service
        ? new ItemState(false, ServiceStartType.Disabled, Running: false)
        : new ItemState(false);

    /// <summary>
    /// A service goes back to the start type it had when Dashio disabled it. With no such record it
    /// becomes Manual, the least intrusive choice. Enabling never starts a service.
    /// </summary>
    public static ItemState EnableTarget(AutostartItem item, IReadOnlyList<JournalEntry> journal)
    {
        if (item.Kind != AutostartKind.Service)
            return new ItemState(true);

        var lastDisable = journal.LastOrDefault(e =>
            e.ItemId.Equals(item.Id, StringComparison.OrdinalIgnoreCase)
            && e.Result == JournalResult.Applied
            && !e.Target.Enabled
            && e.Before.Enabled);
        var startType = lastDisable?.Before.StartType is { } recorded and not ServiceStartType.Disabled
            ? recorded
            : ServiceStartType.Manual;
        return new ItemState(true, startType);
    }

    public static PlannedChange Toggle(AttributedItem attributed, string appName, IReadOnlyList<JournalEntry> journal)
    {
        var item = attributed.Item;
        return item.Enabled
            ? new PlannedChange(item, appName, ChangeAction.Disable, DisableTarget(item))
            : new PlannedChange(item, appName, ChangeAction.Enable, EnableTarget(item, journal));
    }
}

public static class UndoPlanner
{
    /// <summary>
    /// An entry can be undone once: it must have been applied and not already undone.
    /// An ended program cannot be brought back.
    /// </summary>
    public static bool CanUndo(JournalEntry entry, IReadOnlyList<JournalEntry> journal) =>
        entry.Result == JournalResult.Applied
        && entry.Action != ChangeAction.End
        && !journal.Any(e => e.UndoOf == entry.Id && e.Result == JournalResult.Applied);

    /// <summary>Something other than Dashio changed the item after this entry was written.</summary>
    public static bool HasDrifted(JournalEntry entry, AutostartItem current) =>
        !(entry.After ?? entry.Target).IsSatisfiedBy(ItemState.Of(current));

    /// <summary>Restores the exact state recorded before the change, including whether a service was running.</summary>
    public static PlannedChange CreateUndo(JournalEntry entry, AutostartItem current) => new(
        current,
        entry.AppName,
        entry.Action switch
        {
            ChangeAction.Stop => ChangeAction.Start,
            ChangeAction.Start => ChangeAction.Stop,
            _ => entry.Before.Enabled ? ChangeAction.Enable : ChangeAction.Disable,
        },
        entry.Before,
        UndoOf: entry.Id);
}
