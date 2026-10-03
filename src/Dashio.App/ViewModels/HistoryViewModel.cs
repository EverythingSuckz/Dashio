using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Dashio.App.Services;
using Dashio.Core.Changes;
using Dashio.Core.Journal;

namespace Dashio.App.ViewModels;

/// <summary>The heading for one batch of changes: when it happened and whether it can be undone as a whole.</summary>
public sealed class HistoryBatch
{
    public HistoryBatch(Guid batchId, IReadOnlyList<JournalEntry> entries, IReadOnlyList<JournalEntry> journal)
    {
        BatchId = batchId;
        Entries = entries;
        var time = entries[0].Time.LocalDateTime;
        Title = time.Date == DateTime.Today ? $"Today, {time:t}"
            : time.Date == DateTime.Today.AddDays(-1) ? $"Yesterday, {time:t}"
            : $"{time:D}, {time:t}";
        Detail = ItemText.Plural(entries.Count, "change");
        UndoableCount = entries.Count(e => UndoPlanner.CanUndo(e, journal) && e.UndoOf is null);
    }

    public Guid BatchId { get; }
    public IReadOnlyList<JournalEntry> Entries { get; }
    public string Title { get; }
    public string Detail { get; }
    public int UndoableCount { get; }

    /// <summary>Offered when there is more than one change to undo, so it takes a single admin prompt.</summary>
    public bool CanUndoAll => UndoableCount > 1;
    public string UndoAllName => $"Undo all {UndoableCount} changes from {Title}";
}

public sealed class HistoryRow
{
    public HistoryRow(JournalEntry entry, IReadOnlyList<JournalEntry> journal)
    {
        Entry = entry;
        var verb = ItemText.Verb(entry.Action);
        Title = entry.Result switch
        {
            JournalResult.Applied => $"{ItemText.Done(entry.Action)} {entry.ItemName}",
            JournalResult.Cancelled => $"Did not {verb} {entry.ItemName}",
            _ => $"Could not {verb} {entry.ItemName}",
        };

        var parts = new List<string> { entry.AppName, ItemText.KindName(entry.Kind) };
        if (entry.UndoOf is not null)
            parts.Add("Undo of an earlier change");
        IsUndone = journal.Any(e => e.UndoOf == entry.Id && e.Result == JournalResult.Applied);
        if (IsUndone)
            parts.Add("Undone");
        if (entry is { Action: ChangeAction.End or ChangeAction.Uninstall, Result: JournalResult.Applied })
            parts.Add("Cannot be undone");
        if (entry is { Note.Length: > 0, Result: JournalResult.Applied })
            parts.Add(entry.Note);
        Subtitle = string.Join(" · ", parts);

        Error = entry.Error ?? "";
        CanUndo = UndoPlanner.CanUndo(entry, journal) && entry.UndoOf is null;
        (Glyph, IsProblem) = entry.Result switch
        {
            JournalResult.Applied => ("\uE73E", false),     // CheckMark
            JournalResult.Cancelled => ("\uE711", false),   // Cancel
            _ => ("\uE783", true),                          // Error
        };
    }

    public JournalEntry Entry { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public string Error { get; }
    public bool HasError => Error.Length > 0;
    public bool CanUndo { get; }
    public bool IsUndone { get; }
    public string Glyph { get; }
    public bool IsProblem { get; }
    public bool IsApplied => Entry.Result == JournalResult.Applied;
    public bool IsCancelled => Entry.Result == JournalResult.Cancelled;
    public string AccessibleName => HasError ? $"{Title}, {Subtitle}, {Error}" : $"{Title}, {Subtitle}";
    public string UndoName => $"Undo: {Title}";
}

public sealed partial class HistoryViewModel : ObservableObject
{
    /// <summary>Batch headings and their entries in one flat list, newest first.</summary>
    public ObservableCollection<object> Rows { get; } = [];

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial bool HasSkippedLines { get; set; }

    [ObservableProperty]
    public partial string SkippedText { get; set; } = "";

    public void Rebuild()
    {
        var journal = AppServices.State.Journal;
        Rows.Clear();

        foreach (var batch in journal.GroupBy(e => e.BatchId).OrderByDescending(b => b.Max(e => e.Time)))
        {
            var entries = batch.ToList();
            Rows.Add(new HistoryBatch(batch.Key, entries, journal));
            foreach (var entry in entries)
                Rows.Add(new HistoryRow(entry, journal));
        }

        IsEmpty = Rows.Count == 0;
        var skipped = AppServices.State.JournalSkippedLines;
        HasSkippedLines = skipped > 0;
        SkippedText = $"{ItemText.Plural(skipped, "line")} in the change log could not be read and " +
                      $"{(skipped == 1 ? "is" : "are")} not shown. The other entries are unaffected.";
    }
}
