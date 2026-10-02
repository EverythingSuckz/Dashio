using System.Diagnostics;
using Dashio.Core.Changes;
using Dashio.Core.Journal;
using Dashio.Core.Models;
using Dashio.Core.Processes;

namespace Dashio.Core.Tests;

public sealed class EndCoordinatorTests : IDisposable
{
    private readonly string _journalPath = Path.Combine(Path.GetTempPath(), $"dashio-end-{Guid.NewGuid():N}.jsonl");

    public void Dispose() => File.Delete(_journalPath);

    /// <summary>A pretend unelevated attempt: the given ids need admin rights, the rest end.</summary>
    private (EndCoordinator Coordinator, ChangeJournal Journal, List<int> EndedLocally) Create(
        FakeSystem system, params int[] needAdmin)
    {
        var journal = new ChangeJournal(_journalPath);
        var local = new List<int>();
        IReadOnlyList<EndResult> EndAll(IReadOnlyList<EndRequest> requests) => requests.Select(r =>
        {
            if (needAdmin.Contains(r.Pid))
                return new EndResult(r.Pid, false, "Administrator rights are needed.", AccessDenied: true);
            local.Add(r.Pid);
            return new EndResult(r.Pid, true);
        }).ToList();
        return (new EndCoordinator(EndAll, system, journal), journal, local);
    }

    private static PlannedEnd End(int[] pids, params AutostartItem[] services) =>
        new("App", "App", pids.Select(p => new EndRequest(p, 1)).ToList(), services);

    [Fact]
    public async Task Processes_the_user_owns_end_without_the_helper()
    {
        var system = new FakeSystem();
        var (coordinator, journal, local) = Create(system);

        var outcome = await coordinator.EndAsync(End([10, 11]));

        Assert.Equal(JournalResult.Applied, outcome.Result);
        Assert.Equal([10, 11], local);
        Assert.Empty(system.HelperCalls);
        var entry = Assert.Single(journal.Load().Entries);
        Assert.Equal(ChangeAction.End, entry.Action);
        Assert.Equal(AutostartKind.Process, entry.Kind);
    }

    [Fact]
    public async Task Only_what_needs_admin_rights_goes_to_the_helper_in_one_call()
    {
        var system = new FakeSystem();
        var (coordinator, _, local) = Create(system, needAdmin: 11);

        var outcome = await coordinator.EndAsync(End([10, 11, 12]));

        Assert.Equal(JournalResult.Applied, outcome.Result);
        Assert.Equal([10, 12], local);
        Assert.Equal([11], system.EndedByHelper);
        Assert.Single(system.HelperCalls);
    }

    [Fact]
    public async Task With_services_everything_goes_through_one_prompt_and_services_stop_first()
    {
        var service = Items.Service("Svc");
        var system = new FakeSystem(service);
        var (coordinator, _, local) = Create(system);

        var outcome = await coordinator.EndAsync(End([10, 11], service));

        Assert.Equal(JournalResult.Applied, outcome.Result);
        Assert.Empty(local);
        Assert.Single(system.HelperCalls);
        Assert.Equal([10, 11], system.EndedByHelper);
        var stopped = system.Items[service.Id];
        Assert.False(stopped.IsRunning);
        Assert.Equal(ServiceStartType.Automatic, stopped.ServiceStartType);
        Assert.Equal([service.Id], outcome.ServiceIds);
    }

    [Fact]
    public async Task A_cancelled_prompt_ends_nothing_and_is_recorded_as_cancelled()
    {
        var system = new FakeSystem { CancelPrompt = true };
        var (coordinator, journal, _) = Create(system, needAdmin: 10);

        var outcome = await coordinator.EndAsync(End([10]));

        Assert.Equal(JournalResult.Cancelled, outcome.Result);
        Assert.True(outcome.AdminPromptCancelled);
        Assert.Equal(JournalResult.Cancelled, journal.Load().Entries.Single().Result);
    }

    [Fact]
    public async Task What_could_not_be_ended_is_reported_with_a_count()
    {
        var system = new FakeSystem();
        system.Unendable.Add(11);
        var (coordinator, _, _) = Create(system, needAdmin: 11);

        var outcome = await coordinator.EndAsync(End([10, 11]));

        Assert.Equal(JournalResult.Failed, outcome.Result);
        Assert.Equal(1, outcome.Ended);
        Assert.StartsWith("1 of 2 ended.", outcome.Error);
    }

    [Fact]
    public async Task An_end_cannot_be_undone()
    {
        var (coordinator, journal, _) = Create(new FakeSystem());
        await coordinator.EndAsync(End([10]));

        var entries = journal.Load().Entries;
        Assert.False(UndoPlanner.CanUndo(entries.Single(), entries));
    }
}

public sealed class StopNowTests : IDisposable
{
    private readonly string _journalPath = Path.Combine(Path.GetTempPath(), $"dashio-stop-{Guid.NewGuid():N}.jsonl");

    public void Dispose() => File.Delete(_journalPath);

    private (ChangeCoordinator Coordinator, ChangeJournal Journal) Create(FakeSystem system)
    {
        var journal = new ChangeJournal(_journalPath);
        return (new ChangeCoordinator(system.Find, system.Apply, system, journal), journal);
    }

    [Fact]
    public async Task Stopping_a_service_leaves_its_start_type_alone()
    {
        var service = Items.Service("A", ServiceStartType.AutomaticDelayed);
        var system = new FakeSystem(service);
        var (coordinator, journal) = Create(system);

        var outcome = await coordinator.ApplyAsync([ChangePlanner.StopNow(service, "App")]);

        Assert.Equal(1, outcome.AppliedCount);
        Assert.False(system.Items[service.Id].IsRunning);
        Assert.True(system.Items[service.Id].Enabled);
        Assert.Equal(ServiceStartType.AutomaticDelayed, system.Items[service.Id].ServiceStartType);
        Assert.Equal(ChangeAction.Stop, journal.Load().Entries.Single().Action);
    }

    [Fact]
    public async Task Undoing_a_stop_starts_the_service_again()
    {
        var service = Items.Service("A");
        var system = new FakeSystem(service);
        var (coordinator, journal) = Create(system);
        await coordinator.ApplyAsync([ChangePlanner.StopNow(service, "App")]);

        var entry = journal.Load().Entries.Single();
        var undo = UndoPlanner.CreateUndo(entry, system.Items[service.Id]);
        var outcome = await coordinator.ApplyAsync([undo]);

        Assert.Equal(ChangeAction.Start, undo.Action);
        Assert.Equal(1, outcome.AppliedCount);
        Assert.True(system.Items[service.Id].IsRunning);
    }

    [Fact]
    public async Task A_service_that_keeps_running_is_reported_as_failed()
    {
        var service = Items.Service("A");
        var system = new FakeSystem(service);
        system.IgnoreChangesTo.Add(service.Id);
        var (coordinator, _) = Create(system);

        var outcome = await coordinator.ApplyAsync([ChangePlanner.StopNow(service, "App")]);

        Assert.Equal(1, outcome.FailedCount);
        Assert.Equal("The service is still running.", outcome.Outcomes.Single().Error);
    }
}

public class HelperEndTests
{
    [Fact]
    public void A_request_with_only_processes_is_passed_to_the_ender()
    {
        var asked = new List<EndRequest>();
        var system = new FakeSystem();
        var processor = new HelperRequestProcessor(system.Find, (item, _) => new ChangeResult(item.Id, true), requests =>
        {
            asked.AddRange(requests);
            return requests.Select(r => new EndResult(r.Pid, true)).ToList();
        });

        var response = processor.Process(new HelperRequest(HelperRequest.CurrentVersion, [], [new EndRequest(10, 5)]));

        Assert.Null(response.Error);
        Assert.Equal([new EndRequest(10, 5)], asked);
        Assert.True(response.EndResults!.Single().Success);
    }

    [Fact]
    public void A_helper_that_cannot_end_programs_says_so()
    {
        var system = new FakeSystem();
        var processor = new HelperRequestProcessor(system.Find, (item, _) => new ChangeResult(item.Id, true));
        var response = processor.Process(new HelperRequest(HelperRequest.CurrentVersion, [], [new EndRequest(10, 5)]));
        Assert.NotNull(response.Error);
    }
}

/// <summary>Ends throwaway processes this test starts itself. Nothing else on the machine is touched.</summary>
[Trait("Category", "Integration")]
public sealed class ProcessEnderTests : IDisposable
{
    private readonly List<Process> _started = [];
    private readonly ProcessSampler _sampler = new();

    public void Dispose()
    {
        foreach (var process in _started)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch (InvalidOperationException)
            {
            }
            process.Dispose();
        }
    }

    /// <summary>PowerShell 7 is a program that is not part of Windows; null where it is not installed.</summary>
    private Process? StartOrdinaryProgram()
    {
        var pwsh = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')
            .Select(folder => Path.Combine(folder.Trim(), "pwsh.exe"))
            .FirstOrDefault(File.Exists);
        return pwsh is null ? null : Start(pwsh, "-NoProfile -Command Start-Sleep 120");
    }

    private Process Start(string file, string arguments)
    {
        var process = Process.Start(new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
        })!;
        _started.Add(process);
        return process;
    }

    private EndRequest RequestFor(Process process)
    {
        var sample = _sampler.Sample().Processes.Single(p => p.Pid == process.Id);
        return new EndRequest(process.Id, sample.StartTicks);
    }

    [Fact]
    public void An_ordinary_program_is_ended()
    {
        if (StartOrdinaryProgram() is not { } process)
            return;

        var result = new ProcessEnder(_sampler).EndAll([RequestFor(process)], TimeSpan.FromSeconds(1)).Single();

        Assert.True(result.Success, result.Error);
        Assert.True(process.WaitForExit(5000));
    }

    [Fact]
    public void A_different_start_time_ends_nothing()
    {
        if (StartOrdinaryProgram() is not { } process)
            return;

        var stale = RequestFor(process) with { StartTicks = 12345 };
        var result = new ProcessEnder(_sampler).EndAll([stale], TimeSpan.FromSeconds(1)).Single();

        Assert.True(result.Success);
        Assert.False(process.HasExited);
    }

    [Fact]
    public void A_Windows_program_is_refused()
    {
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var process = Start(cmd, "/k");

        var result = new ProcessEnder(_sampler).EndAll([RequestFor(process)], TimeSpan.FromSeconds(1)).Single();

        Assert.False(result.Success);
        Assert.Contains("part of Windows", result.Error);
        Assert.False(process.HasExited);
    }
}
