using Dashio.Core.Changes;
using Dashio.Core.Journal;
using Dashio.Core.Models;
using Dashio.Core.Processes;

namespace Dashio.Core.Tests;

internal static class Items
{
    public static AutostartItem Service(
        string name, ServiceStartType start = ServiceStartType.Automatic, bool running = true, bool isProtected = false) => new()
    {
        Id = $"service:machine:{name}",
        Kind = AutostartKind.Service,
        Name = name,
        DisplayName = name,
        Scope = ItemScope.Machine,
        Enabled = start != ServiceStartType.Disabled,
        Trigger = StartTrigger.Boot,
        IsRunning = running,
        RequiresAdmin = true,
        IsProtected = isProtected,
        ServiceStartType = start,
    };

    public static AutostartItem UserRun(string name, bool enabled = true) => new()
    {
        Id = $"runkey:user:Run:{name}",
        Kind = AutostartKind.RunKey,
        Name = name,
        DisplayName = name,
        Scope = ItemScope.User,
        Enabled = enabled,
        Trigger = StartTrigger.Logon,
    };

    public static AutostartItem With(this AutostartItem item, ItemState state) => item with
    {
        Enabled = state.Enabled,
        ServiceStartType = item.Kind == AutostartKind.Service ? state.StartType ?? item.ServiceStartType : null,
        IsRunning = item.Kind == AutostartKind.Service ? state.Running ?? item.IsRunning : null,
    };
}

/// <summary>A pretend Windows: items held in memory, changed by the same calls the real one receives.</summary>
internal sealed class FakeSystem : IHelperLauncher
{
    public Dictionary<string, AutostartItem> Items { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool CancelPrompt { get; set; }
    public HashSet<string> IgnoreChangesTo { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<IReadOnlyList<ChangeRequest>> HelperCalls { get; } = [];
    public List<string> InProcessCalls { get; } = [];

    public FakeSystem(params AutostartItem[] items)
    {
        foreach (var item in items)
            Items[item.Id] = item;
    }

    public IReadOnlyDictionary<string, AutostartItem> Find(IEnumerable<string> ids) =>
        ids.Where(Items.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(id => id, id => Items[id]);

    public ChangeResult Apply(AutostartItem item, ItemState target)
    {
        InProcessCalls.Add(item.Id);
        if (item.RequiresAdmin)
            return new ChangeResult(item.Id, false, "Administrator rights are needed.", AccessDenied: true);
        return Change(item, target);
    }

    private ChangeResult Change(AutostartItem item, ItemState target)
    {
        if (!IgnoreChangesTo.Contains(item.Id))
            Items[item.Id] = item.With(target);
        return new ChangeResult(item.Id, true);
    }

    public Task<HelperRunResult> ApplyAsync(IReadOnlyList<ChangeRequest> changes, CancellationToken cancellation = default) =>
        EndAsync(changes, [], cancellation);

    /// <summary>Process ids the pretend helper was asked to end, in order.</summary>
    public List<int> EndedByHelper { get; } = [];

    /// <summary>Process ids nothing can end.</summary>
    public HashSet<int> Unendable { get; } = [];

    public Task<HelperRunResult> EndAsync(
        IReadOnlyList<ChangeRequest> stops, IReadOnlyList<EndRequest> ends, CancellationToken cancellation = default)
    {
        HelperCalls.Add(stops);
        if (CancelPrompt)
            return Task.FromResult(new HelperRunResult(true, []));
        var processor = new HelperRequestProcessor(Find, Change, requests => requests.Select(r =>
        {
            EndedByHelper.Add(r.Pid);
            return Unendable.Contains(r.Pid) ? new EndResult(r.Pid, false, "It is still running.") : new EndResult(r.Pid, true);
        }).ToList());
        var response = processor.Process(new HelperRequest(HelperRequest.CurrentVersion, stops, ends));
        return Task.FromResult(
            new HelperRunResult(false, response.Results, response.Error) { EndResults = response.EndResults ?? [] });
    }

    public Task<HelperScanResult> ScanTasksAsync(CancellationToken cancellation = default) =>
        Task.FromResult(new HelperScanResult(false, []));

    public Task<HelperPrefetchResult> ScanPrefetchAsync(CancellationToken cancellation = default) =>
        Task.FromResult(new HelperPrefetchResult(false, []));
}

public sealed class ChangeCoordinatorTests : IDisposable
{
    private readonly string _journalPath = Path.Combine(Path.GetTempPath(), $"dashio-test-{Guid.NewGuid():N}.jsonl");

    public void Dispose() => File.Delete(_journalPath);

    private (ChangeCoordinator Coordinator, ChangeJournal Journal) Create(FakeSystem system)
    {
        var journal = new ChangeJournal(_journalPath);
        return (new ChangeCoordinator(system.Find, system.Apply, system, journal), journal);
    }

    private static PlannedChange Disable(AutostartItem item) =>
        new(item, "App", ChangeAction.Disable, ChangePlanner.DisableTarget(item));

    [Fact]
    public async Task Admin_changes_go_through_the_helper_in_one_call_and_user_changes_do_not()
    {
        var a = Items.Service("A");
        var b = Items.Service("B");
        var run = Items.UserRun("R");
        var system = new FakeSystem(a, b, run);
        var (coordinator, journal) = Create(system);

        var outcome = await coordinator.ApplyAsync([Disable(a), Disable(b), Disable(run)]);

        Assert.Equal(3, outcome.AppliedCount);
        var helperCall = Assert.Single(system.HelperCalls);
        Assert.Equal([a.Id, b.Id], helperCall.Select(c => c.ItemId));
        Assert.Equal([run.Id], system.InProcessCalls);
        Assert.False(system.Items[a.Id].Enabled);
        Assert.Equal(ServiceStartType.Disabled, system.Items[a.Id].ServiceStartType);
        Assert.Equal(3, journal.Load().Entries.Count);
    }

    [Fact]
    public async Task Cancelling_the_prompt_changes_nothing_and_is_journalled_as_cancelled()
    {
        var a = Items.Service("A");
        var system = new FakeSystem(a) { CancelPrompt = true };
        var (coordinator, journal) = Create(system);

        var outcome = await coordinator.ApplyAsync([Disable(a)]);

        Assert.True(outcome.AdminPromptCancelled);
        Assert.Equal(JournalResult.Cancelled, Assert.Single(outcome.Outcomes).Result);
        Assert.True(system.Items[a.Id].Enabled);
        Assert.Equal(JournalResult.Cancelled, Assert.Single(journal.Load().Entries).Result);
    }

    [Fact]
    public async Task A_change_windows_did_not_take_is_reported_as_failed_even_if_the_changer_said_ok()
    {
        var a = Items.Service("A");
        var b = Items.Service("B");
        var system = new FakeSystem(a, b);
        system.IgnoreChangesTo.Add(a.Id);
        var (coordinator, _) = Create(system);

        var outcome = await coordinator.ApplyAsync([Disable(a), Disable(b)]);

        Assert.Equal(JournalResult.Failed, outcome.Outcomes.Single(o => o.Change.Item.Id == a.Id).Result);
        Assert.Equal(JournalResult.Applied, outcome.Outcomes.Single(o => o.Change.Item.Id == b.Id).Result);
    }

    [Fact]
    public async Task Before_state_is_read_at_apply_time_not_taken_from_the_stale_scan()
    {
        var scanned = Items.Service("A", ServiceStartType.Automatic);
        // Another tool changed the service to Manual after the scan.
        var system = new FakeSystem(scanned with { ServiceStartType = ServiceStartType.Manual, IsRunning = false });
        var (coordinator, journal) = Create(system);

        await coordinator.ApplyAsync([Disable(scanned)]);

        var entry = Assert.Single(journal.Load().Entries);
        Assert.Equal(ServiceStartType.Manual, entry.Before.StartType);
        Assert.False(entry.Before.Running);
    }

    [Fact]
    public async Task Protected_and_missing_items_fail_without_reaching_the_helper()
    {
        var windows = Items.Service("W", isProtected: true);
        var gone = Items.Service("Gone");
        var system = new FakeSystem(windows);
        var (coordinator, _) = Create(system);

        var outcome = await coordinator.ApplyAsync([Disable(windows), Disable(gone)]);

        Assert.All(outcome.Outcomes, o => Assert.Equal(JournalResult.Failed, o.Result));
        Assert.Empty(system.HelperCalls);
        Assert.True(system.Items[windows.Id].Enabled);
    }

    [Fact]
    public async Task Undo_restores_the_exact_start_type_and_running_state()
    {
        var a = Items.Service("A", ServiceStartType.AutomaticDelayed, running: true);
        var system = new FakeSystem(a);
        var (coordinator, journal) = Create(system);
        await coordinator.ApplyAsync([Disable(a)]);
        var entry = journal.Load().Entries.Single();
        Assert.False(system.Items[a.Id].IsRunning);

        var undo = UndoPlanner.CreateUndo(entry, system.Items[a.Id]);
        await coordinator.ApplyAsync([undo]);

        var restored = system.Items[a.Id];
        Assert.True(restored.Enabled);
        Assert.Equal(ServiceStartType.AutomaticDelayed, restored.ServiceStartType);
        Assert.True(restored.IsRunning);
        var entries = journal.Load().Entries;
        Assert.Equal(entry.Id, entries[1].UndoOf);
        Assert.False(UndoPlanner.CanUndo(entry, entries));
    }
}

public class ChangePlannerTests
{
    private static JournalEntry Entry(AutostartItem item, ItemState before, ItemState target, JournalResult result = JournalResult.Applied) => new()
    {
        Id = Guid.NewGuid(),
        BatchId = Guid.NewGuid(),
        Time = DateTimeOffset.Now,
        ItemId = item.Id,
        Kind = item.Kind,
        ItemName = item.DisplayName,
        AppName = "App",
        Action = target.Enabled ? ChangeAction.Enable : ChangeAction.Disable,
        Before = before,
        Target = target,
        After = target,
        Result = result,
    };

    [Fact]
    public void Disabling_a_service_disables_and_stops_it()
    {
        var target = ChangePlanner.DisableTarget(Items.Service("A"));
        Assert.Equal(new ItemState(false, ServiceStartType.Disabled, false), target);
    }

    [Fact]
    public void Enabling_a_service_dashio_disabled_restores_its_recorded_start_type()
    {
        var disabled = Items.Service("A", ServiceStartType.Disabled, running: false);
        var journal = new[]
        {
            Entry(disabled, new ItemState(true, ServiceStartType.AutomaticDelayed, true), ChangePlanner.DisableTarget(disabled)),
        };

        var target = ChangePlanner.EnableTarget(disabled, journal);

        Assert.Equal(ServiceStartType.AutomaticDelayed, target.StartType);
        Assert.Null(target.Running);
    }

    [Fact]
    public void Enabling_a_service_with_no_record_sets_manual()
    {
        var disabled = Items.Service("A", ServiceStartType.Disabled, running: false);
        Assert.Equal(ServiceStartType.Manual, ChangePlanner.EnableTarget(disabled, []).StartType);
    }

    [Fact]
    public void A_failed_disable_is_not_used_as_the_record()
    {
        var disabled = Items.Service("A", ServiceStartType.Disabled, running: false);
        var journal = new[]
        {
            Entry(disabled, new ItemState(true, ServiceStartType.Automatic, true),
                ChangePlanner.DisableTarget(disabled), JournalResult.Failed),
        };
        Assert.Equal(ServiceStartType.Manual, ChangePlanner.EnableTarget(disabled, journal).StartType);
    }

    [Fact]
    public void Drift_is_detected_when_something_else_changed_the_item()
    {
        var item = Items.Service("A", ServiceStartType.Disabled, running: false);
        var entry = Entry(item, new ItemState(true, ServiceStartType.Automatic, true), ChangePlanner.DisableTarget(item));

        Assert.False(UndoPlanner.HasDrifted(entry, item));
        Assert.True(UndoPlanner.HasDrifted(entry, item with { Enabled = true, ServiceStartType = ServiceStartType.Manual }));
    }

    [Fact]
    public void Only_applied_entries_can_be_undone()
    {
        var item = Items.UserRun("R");
        var failed = Entry(item, new ItemState(true), new ItemState(false), JournalResult.Failed);
        Assert.False(UndoPlanner.CanUndo(failed, [failed]));
    }
}

public sealed class JournalTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"dashio-test-{Guid.NewGuid():N}.jsonl");

    public void Dispose() => File.Delete(_path);

    private static JournalEntry Sample(string name) => new()
    {
        Id = Guid.NewGuid(),
        BatchId = Guid.NewGuid(),
        Time = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero),
        ItemId = $"service:machine:{name}",
        Kind = AutostartKind.Service,
        ItemName = name,
        AppName = "App",
        Action = ChangeAction.Disable,
        Before = new ItemState(true, ServiceStartType.Automatic, true),
        Target = new ItemState(false, ServiceStartType.Disabled, false),
        After = new ItemState(false, ServiceStartType.Disabled, false),
        Result = JournalResult.Applied,
    };

    [Fact]
    public void Entries_round_trip_in_order()
    {
        var journal = new ChangeJournal(_path);
        var first = Sample("A");
        var second = Sample("B");
        journal.Append([first]);
        journal.Append([second]);

        var load = journal.Load();

        Assert.Equal(0, load.SkippedLines);
        Assert.Equal([first, second], load.Entries);
    }

    [Fact]
    public void Missing_file_is_an_empty_journal()
    {
        var load = new ChangeJournal(_path).Load();
        Assert.Empty(load.Entries);
        Assert.Equal(0, load.SkippedLines);
    }

    [Fact]
    public void A_corrupt_line_is_skipped_and_counted()
    {
        var journal = new ChangeJournal(_path);
        journal.Append([Sample("A")]);
        File.AppendAllText(_path, "{ this is not json\n");
        journal.Append([Sample("B")]);

        var load = journal.Load();

        Assert.Equal(1, load.SkippedLines);
        Assert.Equal(["A", "B"], load.Entries.Select(e => e.ItemName));
    }
}

public class HelperRequestProcessorTests
{
    private static HelperRequestProcessor Processor(FakeSystem system, List<string> applied) =>
        new(system.Find, (item, _) =>
        {
            applied.Add(item.Id);
            return new ChangeResult(item.Id, true);
        });

    private static HelperRequest Request(params string[] ids) =>
        new(HelperRequest.CurrentVersion, ids.Select(id => new ChangeRequest(id, new ItemState(false))).ToList());

    [Fact]
    public void Refuses_an_unknown_schema_version()
    {
        var applied = new List<string>();
        var response = Processor(new FakeSystem(Items.Service("A")), applied)
            .Process(new HelperRequest(99, Request("service:machine:A").Changes));
        Assert.NotNull(response.Error);
        Assert.Empty(applied);
    }

    [Fact]
    public void Refuses_an_unreadable_or_empty_request()
    {
        var applied = new List<string>();
        var processor = Processor(new FakeSystem(), applied);
        Assert.NotNull(processor.Process(null).Error);
        Assert.NotNull(processor.Process(Request()).Error);
    }

    [Fact]
    public void Refuses_an_oversized_batch()
    {
        var applied = new List<string>();
        var ids = Enumerable.Range(0, HelperRequest.MaxChanges + 1).Select(i => $"service:machine:S{i}").ToArray();
        Assert.NotNull(Processor(new FakeSystem(), applied).Process(Request(ids)).Error);
        Assert.Empty(applied);
    }

    [Fact]
    public void Refuses_windows_components_and_missing_items_but_applies_the_rest()
    {
        var applied = new List<string>();
        var system = new FakeSystem(Items.Service("Ok"), Items.Service("Win", isProtected: true));

        var response = Processor(system, applied)
            .Process(Request("service:machine:Ok", "service:machine:Win", "service:machine:Gone"));

        Assert.Null(response.Error);
        Assert.Equal(["service:machine:Ok"], applied);
        Assert.True(response.Results.Single(r => r.ItemId == "service:machine:Ok").Success);
        Assert.False(response.Results.Single(r => r.ItemId == "service:machine:Win").Success);
        Assert.False(response.Results.Single(r => r.ItemId == "service:machine:Gone").Success);
    }

    [Fact]
    public void Protection_is_judged_from_what_the_helper_finds_not_from_the_request()
    {
        // The request can only name an id; whether it is protected comes from the helper's own lookup.
        var applied = new List<string>();
        var system = new FakeSystem(Items.Service("Win", isProtected: true));
        Processor(system, applied).Process(Request("service:machine:Win"));
        Assert.Empty(applied);
    }

    [Theory]
    [InlineData(@"C:\Users\me\AppData\Local\Dashio\requests\a.request.json", @"C:\Users\me\AppData\Local\Dashio\requests\a.response.json", true)]
    [InlineData(@"C:\Users\me\AppData\Local\Dashio\requests\a.request.json", @"C:\Users\me\elsewhere\a.response.json", false)]
    [InlineData(@"C:\Users\me\AppData\Local\Dashio\requests\a.request.json", @"C:\Users\me\AppData\Local\Dashio\requests\a.response.dll", false)]
    [InlineData(@"C:\Windows\System32\a.request.json", @"C:\Windows\System32\a.response.json", false)]
    [InlineData(@"C:\Users\me\x\a.request.json", @"C:\Users\me\x\..\..\..\Windows\a.response.json", false)]
    public void Response_path_must_be_a_new_json_file_next_to_the_request(string request, string response, bool safe) =>
        Assert.Equal(safe, HelperRequestProcessor.AreSafePaths(request, response, out _));

    [Fact]
    public void Response_path_must_not_already_exist()
    {
        var existing = Path.GetTempFileName();
        var json = Path.ChangeExtension(existing, ".json");
        File.Move(existing, json);
        try
        {
            var request = Path.Combine(Path.GetDirectoryName(json)!, "r.request.json");
            Assert.False(HelperRequestProcessor.AreSafePaths(request, json, out _));
        }
        finally
        {
            File.Delete(json);
        }
    }
}
