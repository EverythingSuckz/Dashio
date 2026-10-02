using Dashio.Core.Attribution;
using Dashio.Core.Models;
using Dashio.Core.Processes;
using Xunit.Abstractions;

namespace Dashio.Core.Tests;

public class UsageCalculatorTests
{
    private static SystemSample Sample(long idle, long kernel, long user, params ProcessSample[] processes) =>
        new(processes, MemoryTotalBytes: 16L << 30, MemoryAvailableBytes: 4L << 30, idle, kernel, user);

    private static ProcessSample Process(int pid, long cpu, long start = 1) => new(pid, $"p{pid}.exe", 1 << 20, cpu, start);

    [Fact]
    public void First_sample_has_no_percentages()
    {
        var now = Sample(100, 200, 50, Process(10, 40));
        Assert.Equal(0, UsageCalculator.SystemCpuPercent(null, now));
        Assert.Empty(UsageCalculator.ProcessCpuPercent(null, now));
    }

    [Fact]
    public void System_percentage_is_the_share_that_was_not_idle()
    {
        // 1000 ticks passed across all cores (kernel includes idle); 750 of them idle.
        var before = Sample(idle: 0, kernel: 0, user: 0);
        var after = Sample(idle: 750, kernel: 800, user: 200);
        Assert.Equal(25, UsageCalculator.SystemCpuPercent(before, after), 3);
    }

    [Fact]
    public void Process_percentage_is_its_share_of_all_processor_time()
    {
        var before = Sample(0, 0, 0, Process(10, 100), Process(11, 0));
        var after = Sample(750, 800, 200, Process(10, 200), Process(11, 50));
        var cpu = UsageCalculator.ProcessCpuPercent(before, after);
        Assert.Equal(10, cpu[10], 3);
        Assert.Equal(5, cpu[11], 3);
    }

    [Fact]
    public void A_reused_process_id_is_not_compared_with_the_old_process()
    {
        var before = Sample(0, 0, 0, Process(10, 900, start: 1));
        var after = Sample(750, 800, 200, Process(10, 5, start: 2));
        Assert.False(UsageCalculator.ProcessCpuPercent(before, after).ContainsKey(10));
    }

    [Fact]
    public void An_idle_process_is_left_out()
    {
        var before = Sample(0, 0, 0, Process(10, 100));
        var after = Sample(750, 800, 200, Process(10, 100));
        Assert.Empty(UsageCalculator.ProcessCpuPercent(before, after));
    }
}

public class ProcessAttributorTests
{
    private static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static readonly string System32 = Environment.SystemDirectory;

    private static AutostartItem Service(string name, string path, string? company = null, bool windows = false) => new()
    {
        Id = $"service:machine:{name}",
        Kind = AutostartKind.Service,
        Name = name,
        DisplayName = name,
        TargetPath = path,
        Scope = ItemScope.Machine,
        Enabled = true,
        Trigger = StartTrigger.Boot,
        RequiresAdmin = true,
        IsProtected = windows,
        Evidence = new FileEvidence { Path = path, Exists = true, Company = company, IsWindowsComponent = windows },
    };

    private static AppSource Installed(string name, string publisher, string location) => new()
    {
        Kind = AppSourceKind.InstalledApp,
        Id = $"app:hklm64:{name}",
        Name = name,
        Publisher = publisher,
        InstallLocation = location,
    };

    private static ProcessAttributor Attributor(List<AutostartItem> items, List<AppSource> sources) =>
        new(new AttributionEngine().Group(items, sources), sources);

    private static RunningProcess Running(string? path, params string[] services) =>
        new(100, path is null ? "x" : Path.GetFileName(path), path, services);

    private static FileEvidence? NoEvidence(string path) => new() { Path = path, Exists = true };

    private static (List<AutostartItem> Items, List<AppSource> Sources) Machine()
    {
        var contoso = $@"{ProgramFiles}\Contoso\Sync";
        return (
            [
                Service("ContosoSync", $@"{contoso}\syncsvc.exe", "Contoso Ltd"),
                Service("Fabrikam Helper", $@"{ProgramFiles}\Fabrikam Tools\helper.exe", "Fabrikam"),
                Service("Dhcp", $@"{System32}\dhcpcore.dll", "Microsoft Corporation", windows: true),
            ],
            [
                Installed("Contoso Sync", "Contoso Ltd", contoso),
                Installed("Northwind Editor", "Northwind", $@"{ProgramFiles}\Northwind Editor"),
            ]);
    }

    [Fact]
    public void A_process_in_the_folder_of_a_grouped_app_joins_that_group()
    {
        var (items, sources) = Machine();
        var owner = Attributor(items, sources).Assign(Running($@"{ProgramFiles}\Contoso\Sync\ui\tray.exe"), NoEvidence);
        Assert.Equal("Contoso Sync", owner.Name);
        Assert.NotNull(owner.Group);
    }

    [Fact]
    public void A_process_of_an_app_that_starts_nothing_gets_a_group_named_after_the_app()
    {
        var (items, sources) = Machine();
        var owner = Attributor(items, sources).Assign(Running($@"{ProgramFiles}\Northwind Editor\editor.exe"), NoEvidence);
        Assert.Equal("Northwind Editor", owner.Name);
        Assert.Equal("app:hklm64:Northwind Editor", owner.GroupId);
        Assert.Null(owner.Group);
        Assert.False(owner.IsWindows);
    }

    [Fact]
    public void A_service_host_belongs_to_the_app_of_its_service()
    {
        var (items, sources) = Machine();
        var owner = Attributor(items, sources).Assign(Running($@"{System32}\svchost.exe", "ContosoSync"), NoEvidence);
        Assert.Equal("Contoso Sync", owner.Name);
    }

    [Fact]
    public void A_host_shared_evenly_between_Windows_and_an_app_counts_as_Windows()
    {
        var (items, sources) = Machine();
        var owner = Attributor(items, sources).Assign(Running($@"{System32}\svchost.exe", "ContosoSync", "Dhcp"), NoEvidence);
        Assert.True(owner.IsWindows);
    }

    [Fact]
    public void A_process_without_a_file_is_Windows()
    {
        var (items, sources) = Machine();
        var owner = Attributor(items, sources).Assign(new RunningProcess(4, "System", null, []), NoEvidence);
        Assert.True(owner.IsWindows);
        Assert.Equal(AttributionEngine.WindowsGroupId, owner.GroupId);
    }

    [Fact]
    public void Virtual_machine_memory_gets_its_own_row()
    {
        var (items, sources) = Machine();
        var owner = Attributor(items, sources).Assign(new RunningProcess(9, "vmmemWSL", null, []), NoEvidence);
        Assert.Equal(ProcessAttributor.VirtualMachinesGroupId, owner.GroupId);
        Assert.False(owner.IsWindows);
    }

    [Fact]
    public void A_file_signed_as_part_of_Windows_is_Windows_wherever_it_is()
    {
        var (items, sources) = Machine();
        var path = $@"{ProgramFiles}\Windows Defender\MsMpEng.exe";
        var owner = Attributor(items, sources).Assign(
            Running(path), p => new FileEvidence { Path = p, Exists = true, IsWindowsComponent = true });
        Assert.True(owner.IsWindows);
    }

    [Fact]
    public void A_file_in_the_Windows_folder_is_Windows_until_its_signature_says_otherwise()
    {
        var (items, sources) = Machine();
        var attributor = Attributor(items, sources);
        var path = $@"{System32}\OemTray.exe";

        Assert.True(attributor.Assign(Running(path), _ => null).IsWindows);

        var owner = attributor.Assign(
            Running(path), p => new FileEvidence { Path = p, Exists = true, Company = "Fabrikam", Description = "OEM Tray" });
        Assert.False(owner.IsWindows);
        Assert.Equal("OEM Tray", owner.Name);
    }

    [Fact]
    public void A_process_in_the_same_product_folder_as_a_grouped_item_joins_that_group()
    {
        var (items, sources) = Machine();
        var owner = Attributor(items, sources).Assign(Running($@"{ProgramFiles}\Fabrikam Tools\bin\ui.exe"), NoEvidence);
        Assert.NotNull(owner.Group);
        Assert.Contains(owner.Group!.Items, i => i.Item.Name == "Fabrikam Helper");
    }

    [Fact]
    public void An_unknown_program_gets_a_group_of_its_own_named_by_its_file()
    {
        var (items, sources) = Machine();
        var owner = Attributor(items, sources).Assign(
            Running(@"D:\Portable\Tool\tool.exe"),
            p => new FileEvidence { Path = p, Exists = true, Company = "Tailspin Toys Inc.", Description = "Tailspin Tool" });
        Assert.Null(owner.Group);
        Assert.StartsWith("dir:", owner.GroupId);
        Assert.Equal("Tool", owner.Name);
    }

    [Fact]
    public void A_folder_named_exactly_like_one_app_joins_that_app()
    {
        var (items, sources) = Machine();
        var owner = Attributor(items, sources).Assign(
            Running(@"D:\Data\Northwind Editor\plugins\helper.exe"),
            p => new FileEvidence { Path = p, Exists = true, Company = "Northwind" });
        Assert.Equal("app:hklm64:Northwind Editor", owner.GroupId);
    }

    [Fact]
    public void A_folder_named_like_an_app_of_another_maker_stays_separate()
    {
        var (items, sources) = Machine();
        var owner = Attributor(items, sources).Assign(
            Running(@"D:\Data\Northwind Editor\plugins\helper.exe"),
            p => new FileEvidence { Path = p, Exists = true, Company = "Tailspin Toys" });
        Assert.StartsWith("dir:", owner.GroupId);
    }

    [Fact]
    public void Tools_in_an_apps_own_data_folder_join_the_app_whoever_made_them()
    {
        var (items, sources) = Machine();
        var data = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var owner = Attributor(items, sources).Assign(
            Running($@"{data}\Northwind Editor\languages\server.exe"),
            p => new FileEvidence { Path = p, Exists = true, Company = "Tailspin Toys" });
        Assert.Equal("app:hklm64:Northwind Editor", owner.GroupId);
    }

    [Fact]
    public void An_app_registered_twice_under_one_name_is_one_row()
    {
        var (items, sources) = Machine();
        sources.Add(new AppSource
        {
            Kind = AppSourceKind.StorePackage,
            Id = "pkg:Northwind.Editor_abc",
            Name = "Northwind Editor",
            Publisher = "Northwind",
            InstallLocation = $@"{ProgramFiles}\WindowsApps\Northwind.Editor_1.0.0.0_x64__abc",
            PackageFamilyName = "Northwind.Editor_abc",
        });
        var attributor = Attributor(items, sources);

        var installed = attributor.Assign(Running($@"{ProgramFiles}\Northwind Editor\editor.exe"), NoEvidence);
        var packaged = attributor.Assign(
            Running($@"{ProgramFiles}\WindowsApps\Northwind.Editor_1.0.0.0_x64__abc\helper.exe"), NoEvidence);
        Assert.Equal(installed.GroupId, packaged.GroupId);
    }

    [Fact]
    public void A_dot_folder_does_not_name_an_app()
    {
        var (items, sources) = Machine();
        var owner = Attributor(items, sources).Assign(
            Running(@"D:\Home\.local\bin\tool.exe"),
            p => new FileEvidence { Path = p, Exists = true, Description = "Tailspin Tool" });
        Assert.StartsWith("exe:", owner.GroupId);
        Assert.Equal("Tailspin Tool", owner.Name);
    }

    [Fact]
    public void A_shared_word_in_the_name_is_not_a_match()
    {
        var (items, sources) = Machine();
        var owner = Attributor(items, sources).Assign(
            Running(@"D:\Other\Contoso Sync Viewer\viewer.exe"),
            p => new FileEvidence { Path = p, Exists = true, Company = "Contoso Ltd" });
        Assert.Null(owner.Group);
    }
}

/// <summary>Read-only checks of process sampling on the machine the tests run on.</summary>
[Trait("Category", "Live")]
public class LiveProcessTests(ITestOutputHelper output)
{
    [Fact]
    public void The_sampler_sees_this_process_and_its_file()
    {
        var sampler = new ProcessSampler();
        var sample = sampler.Sample();
        var self = Assert.Single(sample.Processes, p => p.Pid == Environment.ProcessId);

        Assert.True(self.PrivateBytes > 0);
        Assert.True(self.CpuTicks > 0);
        Assert.Equal(Environment.ProcessPath, sampler.ImagePath(self.Pid), ignoreCase: true);
        Assert.True(sample.MemoryTotalBytes > sample.MemoryAvailableBytes);
    }

    [Fact]
    public void Most_processes_can_be_named_without_admin_rights()
    {
        var sampler = new ProcessSampler();
        var processes = sampler.Sample().Processes;
        var named = processes.Count(p => sampler.ImagePath(p.Pid) is not null);
        output.WriteLine($"{named} of {processes.Count} processes have a readable file");
        Assert.True(named >= processes.Count * 0.9);
    }

    [Fact]
    public void Running_services_are_mapped_to_processes()
    {
        var map = ServiceProcessMap.Read();
        output.WriteLine($"{map.Sum(p => p.Value.Count)} services in {map.Count} processes");
        Assert.NotEmpty(map);
        Assert.All(map.Values, services => Assert.All(services, name => Assert.False(string.IsNullOrWhiteSpace(name))));
    }

    [Fact]
    public void Processor_use_adds_up_to_no_more_than_the_machine()
    {
        var sampler = new ProcessSampler();
        var before = sampler.Sample();
        Thread.Sleep(500);
        var after = sampler.Sample();

        var system = UsageCalculator.SystemCpuPercent(before, after);
        var sum = UsageCalculator.ProcessCpuPercent(before, after).Values.Sum();
        output.WriteLine($"system {system:F1}%, sum of processes {sum:F1}%");
        Assert.InRange(system, 0, 100);
        Assert.InRange(sum, 0, 105);
    }
}

/// <summary>Prints how running processes are grouped on this machine, for a person to read.</summary>
[Trait("Category", "Report")]
public class LiveProcessReport(LiveSnapshotFixture live, ITestOutputHelper output) : IClassFixture<LiveSnapshotFixture>
{
    [Fact]
    public void Apps_by_memory()
    {
        var groups = new AttributionEngine().Group(live.Snapshot.Items, live.Snapshot.Sources);
        var attributor = new ProcessAttributor(groups, live.Snapshot.Sources);
        var evidence = new Evidence.FileEvidenceReader();
        var sampler = new ProcessSampler();
        var services = ServiceProcessMap.Read();

        var apps = sampler.Sample().Processes
            .Select(p =>
            {
                var running = new RunningProcess(
                    p.Pid, p.Name, sampler.ImagePath(p.Pid), services.GetValueOrDefault(p.Pid, []));
                return (Process: p, Running: running, Owner: attributor.Assign(running, evidence.Read));
            })
            .GroupBy(x => x.Owner.GroupId)
            .OrderByDescending(g => g.Sum(x => x.Process.PrivateBytes));

        foreach (var app in apps)
        {
            var owner = app.First().Owner;
            var kind = owner.Group is not null ? "scan" : owner.GroupId.Split(':')[0];
            output.WriteLine($"{app.Sum(x => x.Process.PrivateBytes) >> 20,6} MB  {owner.Name}  [{kind}]  ({app.Count()})  {owner.GroupId}");
            foreach (var name in app.Select(x => x.Process.Name).Distinct().Take(6))
                output.WriteLine($"             {name}");
        }
    }
}
