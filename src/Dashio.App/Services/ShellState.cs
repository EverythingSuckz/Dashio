using Dashio.App.ViewModels;
using Dashio.Core.Changes;
using Dashio.Core.Journal;
using Dashio.Core.Models;
using Microsoft.UI.Xaml.Controls;

namespace Dashio.App.Services;

/// <summary>A message for the banner at the top of the window.</summary>
/// <param name="UndoBatchId">When set, the banner offers to undo that batch.</param>
public sealed record Notice(InfoBarSeverity Severity, string Title, string Message, Guid? UndoBatchId = null);

/// <summary>State shared between the window and its pages: the search text and banner messages.</summary>
public sealed class ShellState
{
    public string SearchText { get; private set; } = "";

    public event EventHandler? SearchChanged;
    public event EventHandler<Notice>? Noticed;

    public void SetSearch(string text)
    {
        text = text.Trim();
        if (text == SearchText)
            return;
        SearchText = text;
        SearchChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Notify(Notice notice) => Noticed?.Invoke(this, notice);

    public static bool Matches(string search, params string?[] fields) =>
        search.Length == 0 || fields.Any(f => f is not null && f.Contains(search, StringComparison.CurrentCultureIgnoreCase));
}

/// <summary>Applies changes, then updates the lists and tells the user what happened.</summary>
public static class ChangeRunner
{
    public static bool IsBusy { get; private set; }

    /// <summary>Raised with true when a batch starts and false when it ends.</summary>
    public static event EventHandler<bool>? BusyChanged;

    public static async Task<BatchOutcome?> ApplyAsync(IReadOnlyList<PlannedChange> changes)
    {
        if (IsBusy || changes.Count == 0)
            return null;

        Notice? notice = null;
        IsBusy = true;
        BusyChanged?.Invoke(null, true);
        try
        {
            var outcome = await AppServices.Coordinator.ApplyAsync(changes);

            // Cancelled changes stay queued so the user can try again; everything else is settled.
            AppServices.Pending.Remove(outcome.Outcomes
                .Where(o => o.Result != JournalResult.Cancelled)
                .Select(o => o.Change.Item.Id));
            await AppServices.State.ReplaceItemsAsync(
                outcome.Outcomes.Where(o => o.Current is not null).Select(o => o.Current!));

            notice = Describe(outcome);
            return outcome;
        }
        catch (Exception e)
        {
            notice = new Notice(InfoBarSeverity.Error, "The changes could not be applied", e.Message);
            return null;
        }
        finally
        {
            IsBusy = false;
            BusyChanged?.Invoke(null, false);
            if (notice is not null)
                AppServices.Shell.Notify(notice);
        }
    }

    /// <summary>Ends processes, then refreshes what is shown and says what happened.</summary>
    public static async Task<EndOutcome?> EndAsync(PlannedEnd end)
    {
        if (IsBusy)
            return null;

        Notice? notice = null;
        IsBusy = true;
        BusyChanged?.Invoke(null, true);
        try
        {
            var outcome = await AppServices.Ends.EndAsync(end);

            // Stopped services are read back from Windows, so their rows show the real state.
            var current = await Task.Run(() => AppServices.Scanner.Find(outcome.ServiceIds));
            await AppServices.State.ReplaceItemsAsync(current.Values);
            AppServices.Monitor.RefreshNow();

            notice = outcome.Result switch
            {
                JournalResult.Applied => new Notice(InfoBarSeverity.Success, $"Ended {end.ItemName}", outcome.Error ?? ""),
                JournalResult.Cancelled => new Notice(
                    InfoBarSeverity.Informational, "Nothing was ended", "You cancelled the administrator prompt."),
                _ => new Notice(
                    outcome.Ended > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Error,
                    $"{end.ItemName} could not be ended{(outcome.Ended > 0 ? " completely" : "")}",
                    outcome.Error ?? ""),
            };
            return outcome;
        }
        catch (Exception e)
        {
            notice = new Notice(InfoBarSeverity.Error, $"{end.ItemName} could not be ended", e.Message);
            return null;
        }
        finally
        {
            IsBusy = false;
            BusyChanged?.Invoke(null, false);
            if (notice is not null)
                AppServices.Shell.Notify(notice);
        }
    }

    private static Notice Describe(BatchOutcome outcome)
    {
        var total = outcome.Outcomes.Count;
        var applied = outcome.AppliedCount;
        var failed = outcome.Outcomes.Where(o => o.Result == JournalResult.Failed).ToList();
        var notes = outcome.Outcomes
            .Where(o => o.Result == JournalResult.Applied && o.Error is not null)
            .Select(o => $"{o.Change.Item.DisplayName}: {o.Error}");
        var isUndo = outcome.Outcomes.All(o => o.Change.UndoOf is not null);
        Guid? undoable = applied > 0 && !isUndo ? outcome.BatchId : null;

        if (failed.Count == 0 && !outcome.AdminPromptCancelled)
        {
            var title = isUndo
                ? $"{ItemText.Plural(applied, "change")} undone"
                : $"{ItemText.Plural(applied, "change")} applied";
            return new Notice(InfoBarSeverity.Success, title, string.Join("\n", notes), undoable);
        }

        if (outcome.AdminPromptCancelled && applied == 0 && failed.Count == 0)
        {
            return new Notice(
                InfoBarSeverity.Informational, "Nothing was changed",
                "You cancelled the administrator prompt. Your changes are still queued.");
        }

        var lines = failed.Select(o => $"{o.Change.Item.DisplayName}: {o.Error}").Concat(notes).ToList();
        if (outcome.AdminPromptCancelled)
            lines.Add("Changes that need administrator rights were not applied and are still queued.");
        return new Notice(
            applied == 0 ? InfoBarSeverity.Error : InfoBarSeverity.Warning,
            $"{applied} of {total} changes applied",
            string.Join("\n", lines),
            undoable);
    }

    /// <summary>Works out the changes that would undo the given entries against the current scan.</summary>
    public static (List<PlannedChange> Changes, List<JournalEntry> Drifted, List<JournalEntry> Missing) PlanUndo(
        IEnumerable<JournalEntry> entries)
    {
        var changes = new List<PlannedChange>();
        var drifted = new List<JournalEntry>();
        var missing = new List<JournalEntry>();
        var journal = AppServices.State.Journal;

        foreach (var entry in entries.Where(e => UndoPlanner.CanUndo(e, journal)))
        {
            var current = FindItem(entry.ItemId);
            if (current is null)
            {
                missing.Add(entry);
                continue;
            }
            if (UndoPlanner.HasDrifted(entry, current))
                drifted.Add(entry);
            changes.Add(UndoPlanner.CreateUndo(entry, current));
        }
        return (changes, drifted, missing);
    }

    private static AutostartItem? FindItem(string itemId) => AppServices.State.Groups
        .SelectMany(g => g.Items)
        .FirstOrDefault(i => i.Item.Id.Equals(itemId, StringComparison.OrdinalIgnoreCase))?.Item;
}
