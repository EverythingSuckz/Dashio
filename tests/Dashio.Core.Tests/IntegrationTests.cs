using Dashio.Core.Changes;
using Dashio.Core.Collectors;
using Dashio.Core.Journal;
using Dashio.Core.Models;
using Dashio.Core.Scanning;
using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;

namespace Dashio.Core.Tests;

/// <summary>
/// End-to-end checks against the real machine using throwaway, user-level entries.
/// Each test creates its own entry and removes it afterwards; nothing needs administrator rights.
/// </summary>
[Trait("Category", "Integration")]
public sealed class IntegrationTests : IDisposable
{
    private const string Name = "DashioIntegrationTest";
    // Points at a file that does not exist, so nothing would run even if cleanup were skipped.
    private const string Command = @"C:\DashioIntegrationTest\does-not-exist.exe";

    private readonly string _journalPath = Path.Combine(Path.GetTempPath(), $"dashio-it-{Guid.NewGuid():N}.jsonl");
    private readonly SystemScanner _scanner = new();
    private readonly ChangeExecutor _executor = new();

    public void Dispose()
    {
        File.Delete(_journalPath);
        using (var run = Registry.CurrentUser.OpenSubKey(RunKeyLocation.RunPath, writable: true))
            run?.DeleteValue(Name, throwOnMissingValue: false);
        using (var approved = Registry.CurrentUser.OpenSubKey($@"{RunKeyLocation.ApprovedPath}\Run", writable: true))
            approved?.DeleteValue(Name, throwOnMissingValue: false);
        using var service = new TaskService();
        service.RootFolder.DeleteTask(Name, exceptionOnNotExists: false);
    }

    private ChangeCoordinator Coordinator(IHelperLauncher helper) =>
        new(_scanner.Find, _executor.Apply, helper, new ChangeJournal(_journalPath));

    private sealed class NoHelper : IHelperLauncher
    {
        public int Calls { get; private set; }

        public System.Threading.Tasks.Task<HelperRunResult> ApplyAsync(
            IReadOnlyList<ChangeRequest> changes, CancellationToken cancellation = default)
        {
            Calls++;
            return System.Threading.Tasks.Task.FromResult(new HelperRunResult(true, []));
        }

        public System.Threading.Tasks.Task<HelperRunResult> EndAsync(
            IReadOnlyList<ChangeRequest> stops, IReadOnlyList<Dashio.Core.Processes.EndRequest> ends,
            CancellationToken cancellation = default)
        {
            Calls++;
            return System.Threading.Tasks.Task.FromResult(new HelperRunResult(true, []));
        }

        public System.Threading.Tasks.Task<HelperScanResult> ScanTasksAsync(CancellationToken cancellation = default) =>
            System.Threading.Tasks.Task.FromResult(new HelperScanResult(true, []));

        public System.Threading.Tasks.Task<HelperPrefetchResult> ScanPrefetchAsync(CancellationToken cancellation = default) =>
            System.Threading.Tasks.Task.FromResult(new HelperPrefetchResult(true, []));

        public System.Threading.Tasks.Task<HelperDriveResult> ReadDriveAsync(
            char letter, Action<double>? progress = null, CancellationToken cancellation = default) =>
            System.Threading.Tasks.Task.FromResult(new HelperDriveResult(true, null));
    }

    [Fact]
    public async System.Threading.Tasks.Task User_run_key_is_disabled_and_restored_without_the_helper()
    {
        using (var run = Registry.CurrentUser.CreateSubKey(RunKeyLocation.RunPath, writable: true))
            run.SetValue(Name, Command);
        var id = RunKeyCollector.IdFor(ItemScope.User, "Run", Name);
        var helper = new NoHelper();
        var coordinator = Coordinator(helper);

        var item = _scanner.Find([id])[id];
        Assert.True(item.Enabled);
        Assert.False(item.RequiresAdmin);

        var disable = await coordinator.ApplyAsync(
            [new PlannedChange(item, "Test", ChangeAction.Disable, ChangePlanner.DisableTarget(item))]);
        Assert.Equal(JournalResult.Applied, disable.Outcomes.Single().Result);
        Assert.False(_scanner.Find([id])[id].Enabled);
        // The command itself is untouched: only the on/off flag changed.
        using (var run = Registry.CurrentUser.OpenSubKey(RunKeyLocation.RunPath))
            Assert.Equal(Command, run!.GetValue(Name));

        var journal = new ChangeJournal(_journalPath).Load().Entries;
        var undo = UndoPlanner.CreateUndo(journal.Single(), _scanner.Find([id])[id]);
        var restore = await coordinator.ApplyAsync([undo]);
        Assert.Equal(JournalResult.Applied, restore.Outcomes.Single().Result);
        Assert.True(_scanner.Find([id])[id].Enabled);
        Assert.Equal(0, helper.Calls);
    }

    [Fact]
    public async System.Threading.Tasks.Task User_scheduled_task_is_disabled_and_restored_without_the_helper()
    {
        using (var service = new TaskService())
        {
            var definition = service.NewTask();
            definition.RegistrationInfo.Description = "Created by the Dashio integration tests.";
            definition.Actions.Add(new ExecAction(Command));
            service.RootFolder.RegisterTaskDefinition(Name, definition);
        }
        var id = ScheduledTaskCollector.IdFor($@"\{Name}");
        var helper = new NoHelper();
        var coordinator = Coordinator(helper);

        var item = _scanner.Find([id])[id];
        Assert.True(item.Enabled);
        Assert.False(item.RequiresAdmin);
        Assert.False(item.IsProtected);

        var disable = await coordinator.ApplyAsync(
            [new PlannedChange(item, "Test", ChangeAction.Disable, ChangePlanner.DisableTarget(item))]);
        Assert.Equal(JournalResult.Applied, disable.Outcomes.Single().Result);
        Assert.False(_scanner.Find([id])[id].Enabled);

        var entry = new ChangeJournal(_journalPath).Load().Entries.Single();
        var restore = await coordinator.ApplyAsync([UndoPlanner.CreateUndo(entry, _scanner.Find([id])[id])]);
        Assert.Equal(JournalResult.Applied, restore.Outcomes.Single().Result);
        Assert.True(_scanner.Find([id])[id].Enabled);
        Assert.Equal(0, helper.Calls);
    }

    [Fact]
    public async System.Threading.Tasks.Task Changing_a_service_without_admin_is_routed_to_the_helper_and_nothing_changes_when_cancelled()
    {
        // Any third-party service will do; the helper stand-in cancels, so the service is never touched.
        var service = _scanner.Scan().Items.FirstOrDefault(i => i.Kind == AutostartKind.Service && !i.IsProtected);
        if (service is null)
            return;
        var helper = new NoHelper();

        var outcome = await Coordinator(helper).ApplyAsync(
            [new PlannedChange(service, "Test", ChangeAction.Disable, ChangePlanner.DisableTarget(service))]);

        Assert.Equal(1, helper.Calls);
        Assert.True(outcome.AdminPromptCancelled);
        Assert.Equal(JournalResult.Cancelled, outcome.Outcomes.Single().Result);
        var after = _scanner.Find([service.Id])[service.Id];
        Assert.Equal(service.ServiceStartType, after.ServiceStartType);
    }
}
