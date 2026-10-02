using Dashio.Core.Attribution;
using Dashio.Core.Models;
using Dashio.Core.Scanning;
using Xunit.Abstractions;

namespace Dashio.Core.Tests;

/// <summary>Shares one scan of the real machine between the live tests.</summary>
public sealed class LiveSnapshotFixture
{
    public SystemScanner Scanner { get; } = new();
    public Snapshot Snapshot { get; }
    public TimeSpan Elapsed { get; }

    public LiveSnapshotFixture()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Snapshot = Scanner.Scan();
        Elapsed = watch.Elapsed;
    }
}

/// <summary>Read-only checks against the machine the tests run on.</summary>
[Trait("Category", "Live")]
public class LiveSystemTests(LiveSnapshotFixture live, ITestOutputHelper output) : IClassFixture<LiveSnapshotFixture>
{
    private Snapshot Snapshot => live.Snapshot;

    [Fact]
    public void Scan_has_no_collector_errors()
    {
        output.WriteLine($"Scan took {live.Elapsed.TotalSeconds:F1}s, {Snapshot.Items.Count} items, {Snapshot.Sources.Count} sources");
        Assert.Empty(Snapshot.Errors);
    }

    [Theory]
    [InlineData(AutostartKind.Service)]
    [InlineData(AutostartKind.ScheduledTask)]
    [InlineData(AutostartKind.RunKey)]
    [InlineData(AutostartKind.PackagedStartupTask)]
    public void Every_kind_is_found(AutostartKind kind)
    {
        var count = Snapshot.Items.Count(i => i.Kind == kind);
        output.WriteLine($"{kind}: {count}");
        Assert.True(count > 0);
    }

    [Theory]
    [InlineData(AppSourceKind.InstalledApp)]
    [InlineData(AppSourceKind.StorePackage)]
    public void App_sources_are_found(AppSourceKind kind) =>
        Assert.Contains(Snapshot.Sources, s => s.Kind == kind);

    [Fact]
    public void Item_ids_are_unique()
    {
        var duplicates = Snapshot.Items.GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(duplicates);
    }

    [Fact]
    public void Windows_own_services_are_protected()
    {
        // The Event Log and RPC services exist on every Windows installation.
        foreach (var name in new[] { "EventLog", "RpcSs" })
        {
            var item = Snapshot.Items.Single(i => i.Id == $"service:machine:{name}");
            Assert.True(item.IsProtected, $"{name}: target '{item.TargetPath}', signer '{item.Evidence?.Signer}'");
        }
    }

    [Fact]
    public void Most_windows_tasks_and_services_are_protected_and_third_party_ones_are_not()
    {
        var unprotected = Snapshot.Items.Where(i => !i.IsProtected).ToList();
        output.WriteLine($"Protected: {Snapshot.Items.Count - unprotected.Count}, not protected: {unprotected.Count}");
        foreach (var group in unprotected.GroupBy(i => i.Kind))
            output.WriteLine($"  {group.Key}: {group.Count()}");
        Assert.NotEmpty(unprotected);
        Assert.True(unprotected.Count < Snapshot.Items.Count);
    }

    [Fact]
    public void At_least_90_percent_of_third_party_items_are_placed_in_a_named_group()
    {
        var groups = new AttributionEngine().Group(Snapshot.Items, Snapshot.Sources);
        var thirdParty = groups.Where(g => !g.IsWindows).Sum(g => g.Items.Count);
        var unmatched = groups.Where(g => g.IsUnmatched).Sum(g => g.Items.Count);
        var rate = 1.0 - (double)unmatched / thirdParty;
        output.WriteLine($"Third-party items: {thirdParty}, unmatched: {unmatched}, placed: {rate:P1}");
        Assert.True(rate >= 0.9, $"Only {rate:P1} of third-party items were placed");
    }

    [Fact]
    public void Killer_is_one_group_when_installed()
    {
        var groups = new AttributionEngine().Group(Snapshot.Items, Snapshot.Sources);
        var withKiller = groups.Where(g => g.Items.Any(i => i.Item.TargetPath?.Contains(@"\RivetNetworks\Killer\", StringComparison.OrdinalIgnoreCase) == true)).ToList();
        if (withKiller.Count == 0)
            return; // Not a machine with Killer software.

        var killer = Assert.Single(withKiller);
        output.WriteLine($"{killer.Name} ({killer.Publisher}): {killer.Items.Count} items, sources: {string.Join(", ", killer.Sources.Select(s => s.Name))}");
        Assert.True(killer.Items.Count(i => i.Item.Kind == AutostartKind.Service) >= 5);
        Assert.Contains(killer.Sources, s => s.Kind == AppSourceKind.StorePackage);
        Assert.True(killer.HiddenFromTaskManager);
    }

    [Fact]
    public void Find_returns_the_same_item_as_the_scan()
    {
        var item = Snapshot.Items.First(i => i.Kind == AutostartKind.Service);
        var found = live.Scanner.Find([item.Id]);
        Assert.Equal(item.Enabled, found[item.Id].Enabled);
        Assert.Equal(item.TargetPath, found[item.Id].TargetPath);
    }
}

/// <summary>Not assertions: prints what the scan sees, for a developer to read.</summary>
[Trait("Category", "Report")]
public class LiveReport(LiveSnapshotFixture live, ITestOutputHelper output) : IClassFixture<LiveSnapshotFixture>
{
    [Fact]
    public void Groups()
    {
        var groups = new AttributionEngine().Group(live.Snapshot.Items, live.Snapshot.Sources);
        foreach (var group in groups.Where(g => !g.IsWindows))
        {
            output.WriteLine(
                $"## {group.Name} | {group.Publisher} | {group.Confidence} | boot:{group.StartsWithWindowsCount} run:{group.RunningCount} " +
                $"hidden:{group.HiddenFromTaskManager} | sources: {string.Join("; ", group.Sources.Select(s => s.Kind + ":" + s.Name))}");
            foreach (var item in group.Items)
                output.WriteLine($"   - {item.Item.Kind,-19} {item.Item.DisplayName,-45} [{item.Rule}] {item.Reason}");
        }
    }

    [Fact]
    public void Unprotected_items()
    {
        foreach (var item in live.Snapshot.Items.Where(i => !i.IsProtected).OrderBy(i => i.Kind).ThenBy(i => i.Name))
        {
            output.WriteLine(
                $"{item.Kind,-19} | {item.Name,-42} | {(item.Enabled ? "on " : "off")} {item.Trigger,-8} | " +
                $"{item.Evidence?.Signer ?? "-",-32} | {item.Evidence?.Company ?? "-",-24} | {item.TargetPath ?? "(none) " + item.Command}");
        }
    }
}
