using Dashio.Core.Journal;
using Dashio.Core.Models;

namespace Dashio.Core.Changes;

/// <summary>
/// Applies a batch of queued changes: user-level ones in this process, admin ones through the
/// helper behind a single prompt. The outcome of each change is what Windows reports afterwards,
/// not what the code that made the change claims.
/// </summary>
public sealed class ChangeCoordinator
{
    private readonly Func<IEnumerable<string>, IReadOnlyDictionary<string, AutostartItem>> _find;
    private readonly Func<AutostartItem, ItemState, ChangeResult> _apply;
    private readonly IHelperLauncher _helper;
    private readonly ChangeJournal _journal;

    public ChangeCoordinator(
        Func<IEnumerable<string>, IReadOnlyDictionary<string, AutostartItem>> find,
        Func<AutostartItem, ItemState, ChangeResult> apply,
        IHelperLauncher helper,
        ChangeJournal journal)
    {
        _find = find;
        _apply = apply;
        _helper = helper;
        _journal = journal;
    }

    public async Task<BatchOutcome> ApplyAsync(
        IReadOnlyList<PlannedChange> changes, CancellationToken cancellation = default)
    {
        var batchId = Guid.NewGuid();
        var ids = changes.Select(c => c.Item.Id).ToList();

        // "Before" is read now, not taken from the scan, so the journal holds what was really there.
        var before = await Task.Run(() => _find(ids), cancellation);

        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var cancelled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var forHelper = new List<ChangeRequest>();

        foreach (var change in changes)
        {
            var id = change.Item.Id;
            if (!before.TryGetValue(id, out var current))
            {
                errors[id] = "The item no longer exists.";
                continue;
            }
            if (current.IsProtected)
            {
                errors[id] = "Windows components cannot be changed.";
                continue;
            }
            if (current.RequiresAdmin)
            {
                forHelper.Add(new ChangeRequest(id, change.Target));
                continue;
            }

            var result = await Task.Run(() => _apply(current, change.Target), cancellation);
            if (result.AccessDenied)
                forHelper.Add(new ChangeRequest(id, change.Target));
            else if (result.Error is not null)
                errors[id] = result.Error;
        }

        var promptCancelled = false;
        if (forHelper.Count > 0)
        {
            var run = await _helper.ApplyAsync(forHelper, cancellation);
            if (run.Cancelled)
            {
                promptCancelled = true;
                foreach (var request in forHelper)
                    cancelled.Add(request.ItemId);
            }
            else
            {
                foreach (var result in run.Results.Where(r => r.Error is not null))
                    errors[result.ItemId] = result.Error!;
                if (run.Error is not null)
                {
                    foreach (var request in forHelper.Where(r => !errors.ContainsKey(r.ItemId)))
                        errors[request.ItemId] = run.Error;
                }
            }
        }

        var after = await Task.Run(() => _find(ids), cancellation);

        var outcomes = new List<ChangeOutcome>();
        var entries = new List<JournalEntry>();
        foreach (var change in changes)
        {
            var id = change.Item.Id;
            before.TryGetValue(id, out var was);
            after.TryGetValue(id, out var now);
            errors.TryGetValue(id, out var error);

            JournalResult result;
            if (cancelled.Contains(id))
            {
                result = JournalResult.Cancelled;
                error = "The administrator prompt was cancelled.";
            }
            else if (now is not null && change.Action == ChangeAction.Stop && now.IsRunning == true)
            {
                result = JournalResult.Failed;
                error ??= "The service is still running.";
            }
            else if (now is not null && change.Action == ChangeAction.Start && now.IsRunning != true)
            {
                result = JournalResult.Failed;
                error ??= "The service did not start.";
            }
            else if (now is not null && change.Target.IsSatisfiedBy(ItemState.Of(now)))
            {
                // Applied, possibly with a note such as "could not be stopped".
                result = JournalResult.Applied;
            }
            else
            {
                result = JournalResult.Failed;
                error ??= "Windows still reports the old setting.";
            }

            outcomes.Add(new ChangeOutcome(change, result, error, now));
            entries.Add(new JournalEntry
            {
                Id = Guid.NewGuid(),
                BatchId = batchId,
                Time = DateTimeOffset.Now,
                ItemId = id,
                Kind = change.Item.Kind,
                ItemName = change.Item.DisplayName,
                AppName = change.AppName,
                Action = change.Action,
                Before = ItemState.Of(was ?? change.Item),
                Target = change.Target,
                After = now is null ? null : ItemState.Of(now),
                Result = result,
                Error = error,
                UndoOf = change.UndoOf,
            });
        }

        _journal.Append(entries);
        return new BatchOutcome(batchId, outcomes, promptCancelled);
    }
}
