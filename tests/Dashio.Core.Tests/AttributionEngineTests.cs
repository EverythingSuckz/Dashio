using Dashio.Core.Attribution;
using Dashio.Core.Models;

namespace Dashio.Core.Tests;

public class AttributionEngineTests
{
    private static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static readonly string ProgramFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    private static readonly string System32 = Environment.SystemDirectory;
    private const string HardwareSigner = "Microsoft Windows Hardware Compatibility Publisher";

    private readonly AttributionEngine _engine = new();

    private static AutostartItem Service(
        string name, string? path, string? company = null, string? signer = null,
        ServiceStartType start = ServiceStartType.Automatic, bool running = false, string? display = null) => new()
    {
        Id = $"service:machine:{name}",
        Kind = AutostartKind.Service,
        Name = name,
        DisplayName = display ?? name,
        TargetPath = path,
        Scope = ItemScope.Machine,
        Enabled = start != ServiceStartType.Disabled,
        Trigger = start is ServiceStartType.Automatic ? StartTrigger.Boot : StartTrigger.OnDemand,
        IsRunning = running,
        RequiresAdmin = true,
        ServiceStartType = start,
        Evidence = path is null ? null : new FileEvidence
        {
            Path = path,
            Exists = true,
            Company = company,
            Signer = signer,
            SignerIsGeneric = signer == HardwareSigner,
        },
    };

    private static AppSource Installed(string name, string? publisher, string? location) => new()
    {
        Kind = AppSourceKind.InstalledApp,
        Id = $"app:hklm64:{name}",
        Name = name,
        Publisher = publisher,
        InstallLocation = location,
    };

    private static AppSource Package(string family, string name, string? publisher) => new()
    {
        Kind = AppSourceKind.StorePackage,
        Id = $"pkg:{family}",
        Name = name,
        Publisher = publisher,
        InstallLocation = $@"{ProgramFiles}\WindowsApps\{family.Split('_')[0]}_1.0.0.0_x64__{family.Split('_')[1]}",
        PackageFamilyName = family,
    };

    private static (List<AutostartItem> Items, List<AppSource> Sources) KillerMachine()
    {
        var folder = $@"{System32}\drivers\RivetNetworks\Killer";
        const string family = "RivetNetworks.KillerControlCenter_rh07ty8m5nkag";
        var package = Package(family, "Intel® Killer™ Performance Suite", "INTEL CORP");

        var items = new List<AutostartItem>
        {
            Service("KAPSService", $@"{folder}\KAPSService.exe", "Intel® Corporation", HardwareSigner,
                ServiceStartType.Manual, display: "Killer Smart AP Selection Service"),
            Service("Killer Analytics Service", $@"{folder}\KillerAnalyticsService.exe", "Intel", HardwareSigner, running: true),
            Service("Killer Network Service", $@"{folder}\KillerNetworkService.exe", "Intel", HardwareSigner,
                ServiceStartType.Disabled),
            Service("Killer Provider Data Helper Service", $@"{folder}\KillerProviderDataHelperService.exe", "Intel", HardwareSigner),
            Service("KNDBWM", $@"{folder}\KNDBWMService.exe", "Intel® Corporation", HardwareSigner,
                ServiceStartType.Manual, display: "Killer Dynamic Bandwidth Management"),
            Service("Intel Network Helper Service", $@"{folder}\IntelNetworkHelper.exe", "Intel", HardwareSigner,
                ServiceStartType.Manual),
            new()
            {
                Id = $"packagedtask:user:{family}!KillerControlCenterTask",
                Kind = AutostartKind.PackagedStartupTask,
                Name = "KillerControlCenterTask",
                DisplayName = "Intel® Killer™ Performance Suite",
                TargetPath = $@"{package.InstallLocation}\KillerControlCenter_v2\KillerTray.exe",
                Scope = ItemScope.User,
                Enabled = true,
                Trigger = StartTrigger.Logon,
                PackageFamilyName = family,
            },
        };
        var sources = new List<AppSource>
        {
            package,
            Installed("Killer Performance Driver Suite UWD", "Rivet Networks",
                $@"{ProgramFiles}\Killer Networking\Killer Control Center"),
            // Unrelated apps that must not be pulled in.
            Installed("Intel Driver & Support Assistant", "Intel", $@"{ProgramFilesX86}\Intel\Driver and Support Assistant"),
            Installed("Killer Instinct", "Some Game Studio", @"D:\Games\Killer Instinct"),
        };
        return (items, sources);
    }

    [Fact]
    public void Killer_forms_one_group_from_services_package_and_installed_app()
    {
        var (items, sources) = KillerMachine();

        var groups = _engine.Group(items, sources);

        var killer = Assert.Single(groups);
        Assert.Equal("Intel Killer", killer.Name);
        Assert.Equal("Intel", killer.Publisher);
        Assert.Equal(7, killer.Items.Count);
        Assert.Equal(6, killer.Items.Count(i => i.Item.Kind == AutostartKind.Service));
        Assert.Contains(killer.Sources, s => s.Kind == AppSourceKind.StorePackage);
        Assert.Contains(killer.Sources, s => s.Name == "Killer Performance Driver Suite UWD");
        Assert.DoesNotContain(killer.Sources, s => s.Name == "Killer Instinct");
        Assert.True(killer.HiddenFromTaskManager);
        Assert.Equal(1, killer.RunningCount);
    }

    [Fact]
    public void Result_does_not_depend_on_input_order()
    {
        var (items, sources) = KillerMachine();
        var forward = _engine.Group(items, sources);
        items.Reverse();
        sources.Reverse();
        var backward = _engine.Group(items, sources);

        Assert.Equal(forward.Select(g => g.Id), backward.Select(g => g.Id));
        Assert.Equal(forward[0].Items.Select(i => i.Item.Id), backward[0].Items.Select(i => i.Item.Id));
    }

    [Fact]
    public void Two_products_of_one_vendor_stay_separate_cards()
    {
        var items = new[]
        {
            Service("EpicGamesUpdater", $@"{ProgramFilesX86}\Epic Games\Launcher\Portal\Binaries\Win64\EpicUpdater.exe", "Epic Games, Inc."),
            Service("EpicOnlineServices", $@"{ProgramFilesX86}\Epic Games\Epic Online Services\service\EpicOnlineServicesHost.exe", "Epic Games, Inc."),
        };

        var groups = _engine.Group(items, []);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.Contains("Epic Games", g.Publisher));
        Assert.Contains(groups, g => g.Name == "Epic Games Launcher");
        Assert.Contains(groups, g => g.Name == "Epic Online Services");
    }

    [Fact]
    public void Different_vendors_sharing_a_product_word_do_not_merge()
    {
        var items = new[]
        {
            Service("FooPhoenix", $@"{ProgramFiles}\Foo\Phoenix\a.exe", "Foo Inc."),
            Service("BarPhoenix", $@"{ProgramFiles}\Bar\Phoenix\b.exe", "Bar Ltd"),
        };

        Assert.Equal(2, _engine.Group(items, []).Count);
    }

    [Fact]
    public void Two_installed_apps_sharing_a_brand_word_do_not_merge()
    {
        var items = new[]
        {
            Service("ChromeElevation", $@"{ProgramFiles}\Google\Chrome\Application\elevation_service.exe", "Google LLC"),
            Service("chromoting", $@"{ProgramFilesX86}\Google\Chrome Remote Desktop\host.exe", "Google LLC"),
        };
        var sources = new[]
        {
            Installed("Google Chrome", "Google LLC", $@"{ProgramFiles}\Google\Chrome\Application"),
            Installed("Chrome Remote Desktop Host", "Google LLC", $@"{ProgramFilesX86}\Google\Chrome Remote Desktop"),
        };

        var groups = _engine.Group(items, sources);

        Assert.Equal(2, groups.Count);
        Assert.Contains(groups, g => g.Name == "Google Chrome");
        Assert.Contains(groups, g => g.Name == "Chrome Remote Desktop Host");
    }

    [Fact]
    public void Item_in_an_installed_apps_folder_joins_that_app_and_the_longest_folder_wins()
    {
        var items = new[] { Service("postgresql-x64-17", $@"{ProgramFiles}\PostgreSQL\17\bin\pg_ctl.exe") };
        var sources = new[]
        {
            Installed("PostgreSQL Tools", null, $@"{ProgramFiles}\PostgreSQL"),
            Installed("PostgreSQL 17", "PostgreSQL Global Development Group", $@"{ProgramFiles}\PostgreSQL\17"),
        };

        var group = Assert.Single(_engine.Group(items, sources));

        Assert.Equal("PostgreSQL 17", group.Name);
        var item = Assert.Single(group.Items);
        Assert.Equal(AttributionEngine.RuleInstalledAppFolder, item.Rule);
        Assert.Equal(Confidence.High, item.Confidence);
    }

    [Fact]
    public void Services_of_one_driver_package_share_a_group()
    {
        var folder = $@"{System32}\DriverStore\FileRepository\predatorservice.inf_amd64_c634eb8e856fb962";
        var items = new[]
        {
            Service("AASSvc", $@"{folder}\AASSvc.exe", "Acer Incorporated", "Acer Incorporated", display: "Predator Agent Service"),
            Service("AcerLightingService", $@"{folder}\AcerLightingService.exe", "Acer Incorporated", "Acer Incorporated",
                display: "Predator Lighting Service"),
        };
        var sources = new[]
        {
            new AppSource
            {
                Kind = AppSourceKind.DriverPackage,
                Id = "drv:predatorservice.inf_amd64_c634eb8e856fb962",
                Name = "Predator Service",
                Publisher = "Acer Incorporated",
                InstallLocation = folder,
            },
        };

        var group = Assert.Single(_engine.Group(items, sources));

        Assert.Equal(2, group.Items.Count);
        Assert.Equal("Predator Service", group.Name);
        Assert.Equal("Acer Incorporated", group.Publisher);
        Assert.All(group.Items, i => Assert.Equal(AttributionEngine.RuleDriverPackage, i.Rule));
    }

    [Fact]
    public void Driver_package_without_a_description_is_named_after_its_services()
    {
        var folder = $@"{System32}\DriverStore\FileRepository\foo.inf_amd64_0123456789abcdef";
        var items = new[]
        {
            Service("FooA", $@"{folder}\a.exe", "Foo Inc.", display: "Foo Audio Enhancer Service"),
            Service("FooB", $@"{folder}\b.exe", "Foo Inc.", display: "Foo Audio Helper"),
        };

        var group = Assert.Single(_engine.Group(items, []));

        Assert.Equal("Foo Audio", group.Name);
    }

    [Fact]
    public void Driver_packages_do_not_merge_on_descriptive_words()
    {
        var dal = $@"{System32}\DriverStore\FileRepository\dal.inf_amd64_af50fdb80983f7bc";
        var dtt = $@"{System32}\DriverStore\FileRepository\dtt_sw.inf_amd64_d6f52e3fdecf287d";
        var items = new[]
        {
            Service("jhi_service", $@"{dal}\jhi_service.exe", "Intel", display: "Intel(R) Dynamic Application Loader Host Interface Service"),
            Service("dptftcs", $@"{dtt}\dptf_helper.exe", "Intel", display: "Intel(R) Dynamic Tuning Technology Telemetry Service"),
            Service("XtuService", $@"{ProgramFiles}\Intel\Intel(R) Extreme Tuning Utility\Service\XtuService.exe", "Intel",
                display: "Intel(R) Extreme Tuning Utility"),
        };

        Assert.Equal(3, _engine.Group(items, []).Count);
    }

    [Fact]
    public void A_common_word_does_not_pull_unrelated_packages_into_a_folder_group()
    {
        var items = new[]
        {
            new AutostartItem
            {
                Id = @"task:machine:\screenwatch",
                Kind = AutostartKind.ScheduledTask,
                Name = "screenwatch",
                DisplayName = "screenwatch",
                TargetPath = @"E:\Projects\Screen\target\release\screenwatch.exe",
                Enabled = true,
                Trigger = StartTrigger.Logon,
            },
        };
        var sources = new[]
        {
            Package("Microsoft.ScreenSketch_8wekyb3d8bbwe", "Snipping Tool", "Microsoft Corporation"),
            Package("Microsoft.LockApp_cw5n1h2txyewy", "Windows Default Lock Screen", "Microsoft Corporation"),
        };

        var group = Assert.Single(_engine.Group(items, sources));

        Assert.Equal("Screen", group.Name);
        Assert.Empty(group.Sources);
    }

    [Fact]
    public void Same_product_in_two_program_folders_is_one_group()
    {
        var items = new[]
        {
            Service("FooSvc", $@"{ProgramFiles}\FooSuite\svc.exe"),
            Service("FooSvc32", $@"{ProgramFilesX86}\FooSuite\svc32.exe"),
        };

        var group = Assert.Single(_engine.Group(items, []));

        Assert.Equal(2, group.Items.Count);
    }

    [Fact]
    public void File_naming_a_contractor_goes_to_the_vendor_bucket_by_rule()
    {
        var items = new[] { Service("AcerCCAgentSvis", $@"{System32}\AcerCCAgent.exe", "I3D Technology Inc.") };

        var group = Assert.Single(_engine.Group(items, []));

        Assert.True(group.IsVendorBucket);
        Assert.Equal("Other Acer components", group.Name);
        Assert.Equal(Confidence.Low, group.Confidence);
    }

    [Fact]
    public void Item_with_no_target_and_no_matching_name_is_unmatched()
    {
        var items = new[]
        {
            new AutostartItem
            {
                Id = @"task:machine:\Temp",
                Kind = AutostartKind.ScheduledTask,
                Name = "Temp",
                DisplayName = "Temp",
                Command = "powershell.exe -Command Get-ChildItem",
                Enabled = true,
            },
        };

        var group = Assert.Single(_engine.Group(items, []));

        Assert.True(group.IsUnmatched);
        Assert.Equal(AttributionEngine.UnmatchedGroupId, group.Id);
    }

    [Fact]
    public void Item_whose_file_is_gone_is_still_grouped_by_its_folder()
    {
        var path = $@"{ProgramFiles}\Ghost\ghost.exe";
        var item = Service("GhostSvc", path) with { Evidence = new FileEvidence { Path = path, Exists = false } };

        var group = Assert.Single(_engine.Group([item], []));

        Assert.Equal("Ghost", group.Name);
        Assert.False(group.IsUnmatched);
    }

    [Fact]
    public void Protected_items_form_the_windows_group()
    {
        var item = Service("EventLog", $@"{System32}\wevtsvc.dll") with { IsProtected = true };

        var group = Assert.Single(_engine.Group([item], []));

        Assert.True(group.IsWindows);
        Assert.Equal(AttributionEngine.WindowsGroupId, group.Id);
    }

    [Fact]
    public void Same_name_in_user_and_machine_run_keys_stays_two_items()
    {
        var path = $@"{ProgramFiles}\Foo\foo.exe";
        AutostartItem Run(ItemScope scope) => new()
        {
            Id = $"runkey:{scope.ToString().ToLowerInvariant()}:Run:Foo",
            Kind = AutostartKind.RunKey,
            Name = "Foo",
            DisplayName = "Foo",
            TargetPath = path,
            Scope = scope,
            Enabled = scope == ItemScope.User,
            Trigger = StartTrigger.Logon,
        };

        var group = Assert.Single(_engine.Group([Run(ItemScope.User), Run(ItemScope.Machine)], []));

        Assert.Equal(2, group.Items.Count);
        Assert.Single(group.Items, i => i.Item.Enabled);
    }

    [Fact]
    public void Unmatched_item_joins_the_one_app_its_name_points_to()
    {
        var items = new[]
        {
            new AutostartItem
            {
                Id = @"task:machine:\Seelen UI Service",
                Kind = AutostartKind.ScheduledTask,
                Name = "Seelen UI Service",
                DisplayName = "Seelen UI Service",
                Enabled = true,
                Trigger = StartTrigger.Logon,
            },
        };
        var sources = new[] { Package("Seelen.SeelenUI_p6yyn03m1894e", "Seelen UI", "Seelen") };

        var group = Assert.Single(_engine.Group(items, sources));

        Assert.Equal("Seelen UI", group.Name);
        var item = Assert.Single(group.Items);
        Assert.Equal(AttributionEngine.RuleNameMatch, item.Rule);
        Assert.Equal(Confidence.Low, item.Confidence);
    }

    [Fact]
    public void Every_item_carries_a_reason()
    {
        var (items, sources) = KillerMachine();
        var groups = _engine.Group(items, sources);
        Assert.All(groups.SelectMany(g => g.Items), i => Assert.False(string.IsNullOrWhiteSpace(i.Reason)));
    }
}
