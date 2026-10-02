using Dashio.Core.Journal;
using Dashio.Core.Models;
using Dashio.Core.Processes;

namespace Dashio.Core.Changes;

/// <summary>One "End" the user confirmed: an app's processes, or a single process.</summary>
/// <param name="ItemName">What History calls it: the app's name, or the program's file name.</param>
/// <param name="Services">The app's running services. They are stopped properly before any process is ended.</param>
public sealed record PlannedEnd(
    string AppName, string ItemName, IReadOnlyList<EndRequest> Processes, IReadOnlyList<AutostartItem> Services);

public sealed record EndOutcome(
    JournalResult Result, int Ended, int Total, string? Error, bool AdminPromptCancelled, IReadOnlyList<string> ServiceIds);

/// <summary>
/// Ends processes without elevation where it can and through the helper where it must, behind
/// at most one admin prompt, and records the result. Ending cannot be undone.
/// </summary>
public sealed class EndCoordinator
{
    private readonly Func<IReadOnlyList<EndRequest>, IReadOnlyList<EndResult>> _endAll;
    private readonly IHelperLauncher _helper;
    private readonly ChangeJournal _journal;

    public EndCoordinator(
        Func<IReadOnlyList<EndRequest>, IReadOnlyList<EndResult>> endAll, IHelperLauncher helper, ChangeJournal journal)
    {
        _endAll = endAll;
        _helper = helper;
        _journal = journal;
    }

    public async Task<EndOutcome> EndAsync(PlannedEnd end, CancellationToken cancellation = default)
    {
        var results = new Dictionary<int, EndResult>();
        var notes = new List<string>();
        var stops = end.Services
            .Select(s => new ChangeRequest(s.Id, new ItemState(s.Enabled, s.ServiceStartType, Running: false)))
            .ToList();

        // Services need the helper anyway, and must stop before their processes are ended, so
        // with services everything goes through the one prompt.
        IReadOnlyList<EndRequest> forHelper = end.Processes;
        if (stops.Count == 0)
        {
            foreach (var result in await Task.Run(() => _endAll(end.Processes), cancellation))
                results[result.Pid] = result;
            forHelper = end.Processes.Where(p => results.GetValueOrDefault(p.Pid) is { AccessDenied: true }).ToList();
        }

        var promptCancelled = false;
        if (forHelper.Count > 0 || stops.Count > 0)
        {
            var run = await _helper.EndAsync(stops, forHelper, cancellation);
            if (run.Cancelled)
            {
                promptCancelled = true;
                foreach (var process in forHelper)
                    results[process.Pid] = new EndResult(process.Pid, false, "The administrator prompt was cancelled.");
            }
            else
            {
                foreach (var result in run.EndResults)
                    results[result.Pid] = result;
                foreach (var process in forHelper.Where(p => !run.EndResults.Any(r => r.Pid == p.Pid)))
                    results[process.Pid] = new EndResult(process.Pid, false, run.Error ?? "The helper did not report a result.");
                notes.AddRange(run.Results.Where(r => r.Error is not null).Select(r => r.Error!));
            }
        }

        var ended = end.Processes.Count(p => results.GetValueOrDefault(p.Pid) is { Success: true });
        var failures = end.Processes
            .Select(p => results.GetValueOrDefault(p.Pid))
            .Where(r => r is { Success: false })
            .Select(r => r!.Error ?? "It could not be ended.")
            .Concat(notes)
            .Distinct()
            .ToList();

        var outcome = ended == end.Processes.Count
            ? JournalResult.Applied
            : promptCancelled && ended == 0 ? JournalResult.Cancelled : JournalResult.Failed;
        var error = failures.Count == 0 ? null : string.Join(" ", failures);
        if (outcome == JournalResult.Failed && end.Processes.Count > 1)
            error = $"{ended} of {end.Processes.Count} ended. {error}";

        _journal.Append(
        [
            new JournalEntry
            {
                Id = Guid.NewGuid(),
                BatchId = Guid.NewGuid(),
                Time = DateTimeOffset.Now,
                ItemId = $"process:{end.ItemName}",
                Kind = AutostartKind.Process,
                ItemName = end.ItemName,
                AppName = end.AppName,
                Action = ChangeAction.End,
                Before = new ItemState(true, Running: true),
                Target = new ItemState(true, Running: false),
                Result = outcome,
                Error = error,
            },
        ]);
        return new EndOutcome(outcome, ended, end.Processes.Count, error, promptCancelled, stops.Select(s => s.ItemId).ToList());
    }
}
