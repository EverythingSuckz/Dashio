using Dashio.Core.Changes;
using Dashio.Core.Journal;
using Dashio.Core.Uninstall;
using Dashio.Core.Attribution;
using Dashio.Core.Inventory;
using Dashio.Core.Models;
using Dashio.Core.Processes;
using Xunit.Abstractions;

namespace Dashio.Core.Tests;

public class InventoryBuilderTests
{
    private static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private const string PackageData = @"D:\Fake\Packages";

    private static AppSource Installed(string name, string publisher, string? location, long? bytes = null) => new()
    {
        Kind = AppSourceKind.InstalledApp,
        Id = $"app:hklm64:{name}",
        Name = name,
        Publisher = publisher,
        InstallLocation = location,
        EstimatedBytes = bytes,
    };

    private static AppSource Package(string family, string name, bool system = false) => new()
    {
        Kind = AppSourceKind.StorePackage,
        Id = $"pkg:{family}",
        Name = name,
        Publisher = "Fabrikam",
        InstallLocation = $@"{ProgramFiles}\WindowsApps\{family}_1.0",
        PackageFamilyName = family,
        IsSystem = system,
        AppIds = [$"{family}!App"],
    };

    private static IReadOnlyList<InstalledApp> Build(
        List<AppSource> sources, List<Shortcut>? shortcuts = null, List<string>? topFolders = null,
        Dictionary<string, string[]>? subfolders = null, HashSet<string>? missing = null)
    {
        var attributor = new ProcessAttributor(new AttributionEngine().Group([], sources), sources);
        return InventoryBuilder.Build(
            sources, attributor, shortcuts ?? [], topFolders ?? [],
            folder => subfolders?.GetValueOrDefault(folder) ?? [],
            folder => missing?.Contains(folder) != true,
            PackageData);
    }

    [Fact]
    public void An_installed_app_gets_its_install_folder_as_a_program_folder()
    {
        var app = Assert.Single(Build([Installed("Contoso Sync", "Contoso", $@"{ProgramFiles}\Contoso Sync")]));
        Assert.Equal("Contoso Sync", app.Owner.Name);
        Assert.Equal([new AppFolder($@"{ProgramFiles}\Contoso Sync", FolderRole.Program)], app.Folders);
        Assert.Equal(0, app.ReportedBytes);
    }

    [Fact]
    public void An_app_without_a_folder_falls_back_to_the_size_Windows_recorded()
    {
        var app = Assert.Single(Build([Installed("Contoso Sync", "Contoso", null, bytes: 5_000_000)]));
        Assert.Empty(app.Folders);
        Assert.Equal(5_000_000, app.ReportedBytes);
    }

    [Fact]
    public void A_Store_package_gets_its_data_folder_and_is_something_to_open()
    {
        var app = Assert.Single(Build([Package("Fabrikam.Notes_abc", "Fabrikam Notes")]));
        Assert.Contains(app.Folders, f => f is { Role: FolderRole.Data, Path: PackageData + @"\Fabrikam.Notes_abc" });
        Assert.True(app.IsLaunchable);
    }

    [Fact]
    public void Entries_with_the_same_name_and_maker_are_one_app_even_without_a_folder()
    {
        // An SDK registers one entry for every version it has installed.
        var app = Assert.Single(Build(
        [
            Installed("Contoso Development Kit 10.0.19041", "Contoso", null, bytes: 1_000),
            Installed("Contoso Development Kit 10.0.26100", "Contoso", null, bytes: 2_000),
        ]));

        Assert.Equal("Contoso Development Kit", app.Owner.Name);
        Assert.Equal(2, app.Sources.Count);
        Assert.Equal(3_000, app.ReportedBytes);
    }

    [Fact]
    public void Entries_with_the_same_name_from_different_makers_stay_apart()
    {
        var apps = Build(
        [
            Installed("Notes", "Contoso", null) with { Id = "app:hklm64:ContosoNotes" },
            Installed("Notes", "Fabrikam", null) with { Id = "app:hklm64:FabrikamNotes" },
        ]);

        Assert.Equal(2, apps.Count);
    }

    [Theory]
    [InlineData("Contoso Visual Runtime 2015 Redistributable (x64) - 14.0", true)]
    [InlineData("Contoso Desktop Runtime - 8.0.1 (x64)", true)]
    [InlineData("Contoso Software Development Kit - 10.0.26100", true)]
    [InlineData("Contoso SDK AddOn", true)]
    [InlineData("Contoso Audio Driver", true)]
    [InlineData("Contoso Graphics Software & Drivers", true)]
    // A game opened from its launcher, a command-line tool: nothing to open, but chosen by a person.
    [InlineData("Fabrikam Quest", false)]
    [InlineData("fabrikam-cli", false)]
    [InlineData("Screwdriver Simulator", false)]
    public void An_entry_with_nothing_to_open_is_a_component_only_when_its_name_says_so(string name, bool component)
    {
        var app = Assert.Single(Build([Installed(name, "Contoso", null)]));

        Assert.False(app.IsLaunchable);
        Assert.Equal(component, app.IsComponent);
    }

    [Fact]
    public void Something_to_open_is_never_a_component_whatever_it_is_called()
    {
        var folder = $@"{ProgramFiles}\Contoso Driver Studio";
        var app = Assert.Single(Build(
            [Installed("Contoso Driver Studio", "Contoso", folder)],
            shortcuts: [new Shortcut("Contoso Driver Studio", @"C:\Menu\Studio.lnk", $@"{folder}\studio.exe")]));

        Assert.False(app.IsComponent);
    }

    [Fact]
    public void A_Store_package_with_nothing_to_open_is_a_component()
    {
        var codec = Package("Fabrikam.VideoExtension_abc", "Fabrikam Video Extension") with { AppIds = [] };

        Assert.True(Assert.Single(Build([codec])).IsComponent);
        Assert.False(Assert.Single(Build([Package("Fabrikam.Notes_abc", "Fabrikam Notes")])).IsComponent);
    }

    [Fact]
    public void An_app_is_not_a_component_because_one_of_its_entries_is()
    {
        // A product and its runtime in one folder are one app, and the product is no component.
        var folder = $@"{ProgramFiles}\Contoso";
        var app = Assert.Single(Build(
        [
            Installed("Contoso Quest", "Contoso", folder),
            Installed("Contoso Quest Runtime", "Contoso", folder),
        ]));

        Assert.False(app.IsComponent);
    }

    [Fact]
    public void Parts_of_Windows_are_left_out()
    {
        Assert.Empty(Build([Package("Microsoft.Windows.Shell_abc", "Shell", system: true)]));
    }

    [Fact]
    public void A_data_folder_named_exactly_like_the_app_is_claimed()
    {
        var folder = $@"{LocalAppData}\Contoso Sync";
        var app = Assert.Single(Build(
            [Installed("Contoso Sync", "Contoso", $@"{ProgramFiles}\Contoso Sync")], topFolders: [folder]));
        Assert.Contains(new AppFolder(folder, FolderRole.Data), app.Folders);
    }

    [Fact]
    public void A_data_folder_nobody_is_named_after_is_left_unclaimed()
    {
        var app = Assert.Single(Build(
            [Installed("Contoso Sync", "Contoso", $@"{ProgramFiles}\Contoso Sync")],
            topFolders: [$@"{LocalAppData}\Contoso Sync Viewer", $@"{LocalAppData}\cache"]));
        Assert.DoesNotContain(app.Folders, f => f.Role == FolderRole.Data);
    }

    [Fact]
    public void A_folder_named_after_a_maker_is_searched_one_level_down()
    {
        var maker = $@"{LocalAppData}\Contoso";
        var app = Assert.Single(Build(
            [Installed("Contoso Sync", "Contoso", $@"{ProgramFiles}\Contoso Sync")],
            topFolders: [maker],
            subfolders: new() { [maker] = [$@"{maker}\Sync", $@"{maker}\Other"] }));
        Assert.Contains(new AppFolder($@"{maker}\Sync", FolderRole.Data), app.Folders);
        Assert.DoesNotContain(app.Folders, f => f.Path.EndsWith("Other"));
    }

    [Fact]
    public void An_install_folder_in_app_data_is_not_counted_again_as_data()
    {
        var folder = $@"{LocalAppData}\Contoso Sync";
        var app = Assert.Single(Build([Installed("Contoso Sync", "Contoso", folder)], topFolders: [folder]));
        Assert.Equal([new AppFolder(folder, FolderRole.Program)], app.Folders);
    }

    [Fact]
    public void A_shortcut_into_the_install_folder_makes_the_app_something_to_open()
    {
        var folder = $@"{ProgramFiles}\Contoso Sync";
        var apps = Build(
            [Installed("Contoso Sync", "Contoso", folder), Installed("Contoso Runtime", "Contoso", $@"{ProgramFiles}\Contoso Runtime")],
            shortcuts: [new Shortcut("Contoso Sync", @"C:\Menu\Contoso Sync.lnk", $@"{folder}\sync.exe")]);

        var sync = apps.Single(a => a.Owner.Name == "Contoso Sync");
        Assert.True(sync.IsLaunchable);
        Assert.Equal([$@"{folder}\sync.exe"], sync.Programs);
        Assert.False(apps.Single(a => a.Owner.Name == "Contoso Runtime").IsLaunchable);
    }

    [Fact]
    public void A_missing_install_folder_is_not_listed()
    {
        var folder = $@"{ProgramFiles}\Contoso Sync";
        var app = Assert.Single(Build([Installed("Contoso Sync", "Contoso", folder, bytes: 1000)], missing: [folder]));
        Assert.Empty(app.Folders);
        Assert.Equal(1000, app.ReportedBytes);
    }
}

public class UsageResolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    private static InstalledApp App(string name, string exe = "app.exe", DateTimeOffset? installed = null, bool launchable = true)
    {
        var folder = $@"{ProgramFiles}\{name}";
        var source = new AppSource
        {
            Kind = AppSourceKind.InstalledApp, Id = $"app:hklm64:{name}", Name = name, Publisher = "Contoso", InstallLocation = folder,
        };
        var owner = new ProcessOwner(source.Id, name, "Contoso", null, false, null, source);
        return new InstalledApp(
            owner, [source], [new AppFolder(folder, FolderRole.Program)], 0, installed,
            launchable ? [new Shortcut(name, $@"C:\Menu\{name}.lnk", $@"{folder}\{exe}")] : [],
            [], launchable ? [$@"{folder}\{exe}"] : []);
    }

    private static UsageResolver Resolver(
        InstalledApp[] apps, LaunchRecord[]? launches = null, PrefetchEntry[]? prefetch = null,
        Dictionary<string, DateTimeOffset>? sightings = null)
    {
        var sources = apps.SelectMany(a => a.Sources).ToList();
        var attributor = new ProcessAttributor([], sources);
        return new UsageResolver(
            new UsageEvidence(launches ?? [], prefetch, sightings ?? []), apps, attributor, Now);
    }

    private static PrefetchEntry[] Prefetch(params (string Name, int DaysAgo)[] entries) =>
        entries.Select(e => new PrefetchEntry(e.Name, Now.AddDays(-e.DaysAgo))).ToArray();

    [Fact]
    public void Without_any_record_nothing_is_called_unused()
    {
        var app = App("Alpha");
        var resolver = Resolver([app]);
        Assert.Null(resolver.CoversFrom);
        Assert.Equal(UsageVerdict.Unknown, resolver.Resolve(app, runningNow: false).Verdict);
    }

    [Fact]
    public void A_running_app_is_running_whatever_the_records_say()
    {
        var app = App("Alpha");
        Assert.Equal(UsageVerdict.RunningNow, Resolver([app]).Resolve(app, runningNow: true).Verdict);
    }

    [Fact]
    public void Prefetch_shows_an_app_was_opened_or_not()
    {
        var used = App("Alpha", "alpha.exe");
        var unused = App("Beta", "beta.exe");
        var resolver = Resolver([used, unused], prefetch: Prefetch(("ALPHA.EXE", 3), ("OLDEST.EXE", 200)));

        Assert.Equal(Now - UsageResolver.Lately, resolver.CoversFrom);
        var opened = resolver.Resolve(used, false);
        Assert.Equal(UsageVerdict.OpenedLately, opened.Verdict);
        Assert.Equal(Now.AddDays(-3), opened.LastOpened);
        var notOpened = resolver.Resolve(unused, false);
        Assert.Equal(UsageVerdict.NotOpenedLately, notOpened.Verdict);
        Assert.Equal(Now - UsageResolver.Lately, notOpened.NotSince);
    }

    [Fact]
    public void The_claim_reaches_back_only_as_far_as_the_records_do()
    {
        var app = App("Beta", "beta.exe");
        var resolver = Resolver([app], prefetch: Prefetch(("OLDEST.EXE", 45)));
        Assert.Equal(Now.AddDays(-45), resolver.Resolve(app, false).NotSince);
    }

    [Fact]
    public void Records_that_reach_back_only_a_few_days_call_nothing_unused()
    {
        var app = App("Beta", "beta.exe");
        var resolver = Resolver([app], prefetch: Prefetch(("OLDEST.EXE", 5)));
        Assert.Null(resolver.CoversFrom);
        Assert.Equal(Now.AddDays(-5), resolver.ReachesBackTo);
        Assert.Equal(UsageVerdict.Unknown, resolver.Resolve(app, false).Verdict);
    }

    [Fact]
    public void A_program_name_two_apps_share_proves_nothing_for_either()
    {
        var a = App("Alpha", "launcher.exe");
        var b = App("Beta", "launcher.exe");
        var resolver = Resolver([a, b], prefetch: Prefetch(("LAUNCHER.EXE", 2), ("OLDEST.EXE", 200)));
        Assert.Equal(UsageVerdict.Unknown, resolver.Resolve(a, false).Verdict);
        Assert.Equal(UsageVerdict.Unknown, resolver.Resolve(b, false).Verdict);
    }

    [Fact]
    public void Something_with_nothing_to_open_is_not_judged()
    {
        var runtime = App("Runtime", launchable: false);
        var resolver = Resolver([runtime], prefetch: Prefetch(("OLDEST.EXE", 200)));
        Assert.Equal(UsageVerdict.NotJudged, resolver.Resolve(runtime, false).Verdict);
    }

    [Fact]
    public void An_app_installed_after_the_records_begin_is_not_judged()
    {
        var fresh = App("Beta", "beta.exe", installed: Now.AddDays(-10));
        var resolver = Resolver([fresh], prefetch: Prefetch(("OLDEST.EXE", 200)));
        Assert.Equal(UsageVerdict.NotJudged, resolver.Resolve(fresh, false).Verdict);
    }

    [Fact]
    public void Being_seen_running_by_Dashio_counts_as_opened()
    {
        var app = App("Beta", "beta.exe");
        var resolver = Resolver(
            [app], prefetch: Prefetch(("OLDEST.EXE", 200)),
            sightings: new() { [app.Id] = Now.AddDays(-7) });
        Assert.Equal(UsageVerdict.OpenedLately, resolver.Resolve(app, false).Verdict);
    }

    [Fact]
    public void A_stale_launch_list_cannot_vouch_for_anything()
    {
        var app = App("Beta", "beta.exe");
        var folder = $@"{ProgramFiles}\Alpha";
        var resolver = Resolver([app], launches: [new LaunchRecord($@"{folder}\alpha.exe", Now.AddDays(-400))]);
        Assert.Null(resolver.CoversFrom);
        Assert.Equal(UsageVerdict.Unknown, resolver.Resolve(app, false).Verdict);
    }

    [Fact]
    public void A_current_launch_list_vouches_and_matches_by_shortcut()
    {
        var used = App("Alpha", "alpha.exe");
        var unused = App("Beta", "beta.exe");
        var resolver = Resolver(
            [used, unused],
            launches:
            [
                new LaunchRecord(@"C:\Menu\Alpha.lnk", Now.AddDays(-1)),
                new LaunchRecord(@"C:\Other\old.exe", Now.AddDays(-300)),
            ]);
        Assert.Equal(UsageVerdict.OpenedLately, resolver.Resolve(used, false).Verdict);
        Assert.Equal(UsageVerdict.NotOpenedLately, resolver.Resolve(unused, false).Verdict);
    }
}

public class UsageRecordTests
{
    [Fact]
    public void UserAssist_names_are_shifted_thirteen_letters()
    {
        Assert.Equal(@"C:\Tools\App.exe", UserAssistReader.Rot13(@"P:\Gbbyf\Ncc.rkr"));
    }

    [Fact]
    public void An_entry_that_was_never_launched_is_skipped()
    {
        Assert.Null(UserAssistReader.Decode("Ncc.rkr", 0));
    }

    [Fact]
    public void A_known_folder_id_becomes_a_real_path()
    {
        // {6D809377-...} is Program Files.
        var record = UserAssistReader.Decode(
            UserAssistReader.Rot13(@"{6D809377-6AF0-444B-8957-A3773F02200E}\Contoso\app.exe"),
            new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc());
        Assert.NotNull(record);
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Contoso\app.exe"),
            record!.Target, ignoreCase: true);
    }

    [Fact]
    public void Prefetch_files_are_summarised_per_program_with_the_latest_time()
    {
        var early = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var late = early.AddDays(5);
        var entries = PrefetchReader.Summarise(
        [
            ("ZEN.EXE-1A2B3C4D.pf", early),
            ("ZEN.EXE-99887766.pf", late),
            ("Layout.ini", late),
        ]);
        Assert.Equal([new PrefetchEntry("ZEN.EXE", late)], entries);
    }

    [Fact]
    public void Prefetch_names_are_capitals_cut_at_29_characters()
    {
        Assert.Equal("ZEN.EXE", PrefetchReader.NameFor(@"C:\Apps\zen.exe"));
        Assert.Equal(29, PrefetchReader.NameFor(@"C:\Apps\a-very-long-program-name-indeed.exe").Length);
    }
}

public sealed class SizeCacheTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"dashio-sizes-{Guid.NewGuid():N}.json");
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void A_size_is_remembered_across_runs_and_goes_stale()
    {
        var cache = new SizeCache(_path);
        cache.Set(@"C:\A", 100, Now);
        cache.Set(@"C:\Gone", 5, Now);
        cache.Save([@"C:\A"]);

        var reloaded = new SizeCache(_path);
        Assert.Equal(100, reloaded.Last(@"c:\a"));
        Assert.Null(reloaded.Last(@"C:\Gone"));
        Assert.True(reloaded.IsFresh(@"C:\A", Now.AddHours(1)));
        Assert.False(reloaded.IsFresh(@"C:\A", Now + SizeCache.MaxAge + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void A_folder_is_measured_as_the_sum_of_its_files()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"dashio-size-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        try
        {
            File.WriteAllBytes(Path.Combine(folder, "a.bin"), new byte[1000]);
            File.WriteAllBytes(Path.Combine(folder, "sub", "b.bin"), new byte[234]);
            Assert.Equal(1234, FolderSizer.Measure(folder));
            Assert.Equal(0, FolderSizer.Measure(Path.Combine(folder, "missing")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

/// <summary>Read-only checks of the inventory on the machine the tests run on.</summary>
[Trait("Category", "Live")]
public class LiveInventoryTests(LiveSnapshotFixture live, ITestOutputHelper output) : IClassFixture<LiveSnapshotFixture>
{
    private IReadOnlyList<InstalledApp> Build()
    {
        var groups = new AttributionEngine().Group(live.Snapshot.Items, live.Snapshot.Sources);
        var attributor = new ProcessAttributor(groups, live.Snapshot.Sources);
        return InventoryBuilder.Build(
            live.Snapshot.Sources, attributor, StartMenuCollector.Collect(), DataFolderCollector.TopFolders(),
            DataFolderCollector.Subfolders, Directory.Exists, DataFolderCollector.PackageDataRoot);
    }

    [Fact]
    public void Installed_apps_are_found_with_folders_and_things_to_open()
    {
        var apps = Build();
        output.WriteLine(
            $"{apps.Count} apps, {apps.Count(a => a.Folders.Count > 0)} with folders, " +
            $"{apps.Count(a => a.Folders.Any(f => f.Role == FolderRole.Data))} with data folders, " +
            $"{apps.Count(a => a.IsLaunchable)} with something to open");
        Assert.True(apps.Count > 10);
        Assert.Contains(apps, a => a.IsLaunchable);
        Assert.Equal(apps.Count, apps.Select(a => a.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void No_folder_is_claimed_by_two_apps()
    {
        var shared = Build().SelectMany(a => a.Folders.Select(f => (f.Path, a.Id)))
            .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(x => x.Id).Distinct().Count() > 1)
            .Select(g => g.Key).ToList();
        output.WriteLine($"folders claimed twice: {shared.Count}");
        Assert.Empty(shared);
    }

    [Fact]
    public void The_launch_list_can_be_read()
    {
        var launches = UserAssistReader.Read();
        output.WriteLine($"{launches.Count} launches, current: {UserAssistReader.IsCurrent(launches, DateTimeOffset.Now)}");
        Assert.All(launches, l => Assert.False(string.IsNullOrWhiteSpace(l.Target)));
    }
}

/// <summary>Prints the installed apps with their folders, for a person to read.</summary>
[Trait("Category", "Report")]
public class LiveInventoryReport(LiveSnapshotFixture live, ITestOutputHelper output) : IClassFixture<LiveSnapshotFixture>
{
    [Fact]
    public void Installed_apps()
    {
        var groups = new AttributionEngine().Group(live.Snapshot.Items, live.Snapshot.Sources);
        var attributor = new ProcessAttributor(groups, live.Snapshot.Sources);
        var apps = InventoryBuilder.Build(
            live.Snapshot.Sources, attributor, StartMenuCollector.Collect(), DataFolderCollector.TopFolders(),
            DataFolderCollector.Subfolders, Directory.Exists, DataFolderCollector.PackageDataRoot);

        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var sized = apps
            .Select(a => (App: a, Bytes: a.ReportedBytes + a.Folders.Sum(f => FolderSizer.Measure(f.Path))))
            .OrderByDescending(x => x.Bytes).ToList();
        output.WriteLine($"measured {apps.Sum(a => a.Folders.Count)} folders in {watch.Elapsed.TotalSeconds:F1} s");

        foreach (var (app, bytes) in sized.Take(40))
        {
            output.WriteLine($"{bytes >> 20,7} MB  {app.Owner.Name}  [{(app.IsLaunchable ? "opens" : "no shortcut")}]");
            foreach (var folder in app.Folders)
                output.WriteLine($"             {folder.Role,-7} {folder.Path.Replace(user, "~")}");
        }
    }

    /// <summary>Every row the Apps page would show, with what is known about each.</summary>
    [Fact]
    public void Apps_list()
    {
        var groups = new AttributionEngine().Group(live.Snapshot.Items, live.Snapshot.Sources);
        var attributor = new ProcessAttributor(groups, live.Snapshot.Sources);
        var installed = InventoryBuilder.Build(
            live.Snapshot.Sources, attributor, StartMenuCollector.Collect(), DataFolderCollector.TopFolders(),
            DataFolderCollector.Subfolders, Directory.Exists, DataFolderCollector.PackageDataRoot)
            .ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);

        var evidence = new Evidence.FileEvidenceReader();
        var sampler = new ProcessSampler();
        var services = ServiceProcessMap.Read();
        var running = sampler.Sample().Processes
            .Select(p => attributor.Assign(
                new RunningProcess(p.Pid, p.Name, sampler.ImagePath(p.Pid), services.GetValueOrDefault(p.Pid, [])), evidence.Read))
            .Where(o => !o.IsWindows)
            .GroupBy(o => o.GroupId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var rows = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups.Where(g => !g.IsWindows))
            rows[group.Id] = group.Name;
        foreach (var app in installed.Values)
            rows.TryAdd(app.Id, app.Owner.Name);
        foreach (var owner in running.Values)
            rows.TryAdd(owner.GroupId, owner.Name);

        var byId = groups.ToDictionary(g => g.Id, StringComparer.OrdinalIgnoreCase);
        var hidden = 0;
        foreach (var (id, name) in rows.OrderBy(r => r.Value, StringComparer.OrdinalIgnoreCase))
        {
            var app = installed.GetValueOrDefault(id);
            var items = byId.GetValueOrDefault(id)?.Items.Count ?? 0;
            var component = items == 0 && (app is null || app.IsComponent);
            if (component)
                hidden++;
            var marks = string.Join(" ",
                component ? "(left out)" : "LISTED",
                app is null ? "-" : app.IsLaunchable ? "OPENS" : "noshortcut",
                items > 0 ? $"items={items}" : "-",
                running.ContainsKey(id) ? "RUNNING" : "-");
            output.WriteLine($"{name,-58} {marks,-44} {id}");
            foreach (var source in app?.Sources ?? [])
                output.WriteLine($"      {source.Kind,-13} {source.Name}  |  {source.Publisher}  |  {(source.UninstallCommand is null ? "no uninstaller" : "uninstaller")}");
        }
        output.WriteLine($"{rows.Count} rows, {rows.Count - hidden} listed, {hidden} left out");
    }
}

public class UninstallerTests
{
    private static AppSource App(string? command, bool system = false) => new()
    {
        Kind = AppSourceKind.InstalledApp,
        Id = "app:hklm64:Fabrikam",
        Name = "Fabrikam Editor",
        UninstallCommand = command,
        IsSystem = system,
    };

    private static bool Exists(string path) =>
        path is @"C:\Program Files\Fabrikam Editor\unins000.exe" or @"C:\Tools\remove.exe";

    [Theory]
    [InlineData(@"""C:\Program Files\Fabrikam Editor\unins000.exe"" /SILENT", @"C:\Program Files\Fabrikam Editor\unins000.exe", "/SILENT")]
    [InlineData(@"C:\Program Files\Fabrikam Editor\unins000.exe /S", @"C:\Program Files\Fabrikam Editor\unins000.exe", "/S")]
    [InlineData(@"C:\Program Files\Fabrikam Editor\unins000.exe", @"C:\Program Files\Fabrikam Editor\unins000.exe", "")]
    [InlineData(@"C:\Tools\remove.exe --app ""Fabrikam Editor""", @"C:\Tools\remove.exe", @"--app ""Fabrikam Editor""")]
    [InlineData("msiexec.exe /x{0A1B2C3D-0000-1111-2222-333344445555}", "msiexec.exe", "/x{0A1B2C3D-0000-1111-2222-333344445555}")]
    [InlineData("MsiExec.exe /I{0A1B2C3D-0000-1111-2222-333344445555}", "MsiExec.exe", "/I{0A1B2C3D-0000-1111-2222-333344445555}")]
    public void A_registered_command_is_split_into_program_and_arguments(string command, string program, string arguments)
    {
        var split = Uninstaller.Split(command, Exists);

        Assert.Equal((program, arguments), split);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData(@"C:\Gone\uninstall.exe /S")]
    [InlineData(@"""C:\Unclosed\uninstall.exe /S")]
    public void A_command_that_names_no_program_gives_no_plan(string? command)
    {
        Assert.Null(Uninstaller.PlanFor("Fabrikam Editor", App(command), Exists));
    }

    [Fact]
    public void Parts_of_Windows_and_drivers_are_never_offered()
    {
        Assert.Null(Uninstaller.PlanFor("Windows", App(@"C:\Tools\remove.exe", system: true), Exists));
        var driver = App(@"C:\Tools\remove.exe") with { Kind = AppSourceKind.DriverPackage };
        Assert.Null(Uninstaller.PlanFor("Driver", driver, Exists));
    }

    [Fact]
    public void A_Store_app_is_removed_as_a_package()
    {
        var package = new AppSource
        {
            Kind = AppSourceKind.StorePackage, Id = "pkg:Fabrikam.Notes_abc", Name = "Fabrikam Notes",
            PackageFamilyName = "Fabrikam.Notes_abc",
        };

        var plan = Uninstaller.PlanFor("Fabrikam Notes", package, Exists);

        Assert.NotNull(plan);
        Assert.True(plan!.IsPackage);
        Assert.Null(plan.Program);
    }

    [Fact]
    public void An_uninstall_is_recorded_and_cannot_be_undone()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dashio-journal-{Guid.NewGuid():N}.jsonl");
        try
        {
            var journal = new ChangeJournal(path);
            var plan = Uninstaller.PlanFor("Fabrikam Editor", App(@"C:\Tools\remove.exe"), Exists)!;

            Uninstaller.Record(journal, plan.AppName, plan.Source, JournalResult.Applied, null);

            var entry = Assert.Single(journal.Load().Entries);
            Assert.Equal(ChangeAction.Uninstall, entry.Action);
            Assert.Equal("Fabrikam Editor", entry.ItemName);
            Assert.False(UndoPlanner.CanUndo(entry, [entry]));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
