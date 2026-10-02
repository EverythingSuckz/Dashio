using System.Collections.Concurrent;
using Dashio.Core.Collectors;
using Dashio.Core.Evidence;
using Dashio.Core.Models;

namespace Dashio.Core.Scanning;

/// <summary>Runs every collector and returns one snapshot. A failing collector is reported, not fatal.</summary>
public sealed class SystemScanner
{
    private readonly FileEvidenceReader _evidence;

    public SystemScanner(FileEvidenceReader? evidence = null) => _evidence = evidence ?? new FileEvidenceReader();

    public Task<Snapshot> ScanAsync(IProgress<string>? progress = null, CancellationToken cancellation = default) =>
        Task.Run(() => Scan(progress, cancellation), cancellation);

    public Snapshot Scan(IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        var errors = new ConcurrentBag<CollectorError>();
        var items = new ConcurrentBag<AutostartItem>();
        var sources = new ConcurrentBag<AppSource>();

        void Run(string name, Action collect)
        {
            try
            {
                collect();
            }
            catch (Exception e)
            {
                errors.Add(new CollectorError(name, e.Message));
            }
        }

        void AddItems(IItemCollector collector) => Run(collector.Name, () =>
        {
            foreach (var item in collector.Collect())
                items.Add(item);
        });

        progress?.Report("Reading services, tasks and startup entries");
        var packages = new PackageCollector();
        var installedApps = new InstalledAppCollector();
        Parallel.Invoke(
            new ParallelOptions { CancellationToken = cancellation },
            () => AddItems(new ServiceCollector()),
            () => AddItems(new ScheduledTaskCollector()),
            () => AddItems(new RunKeyCollector()),
            () => AddItems(new StartupFolderCollector()),
            () => Run(packages.Name, () =>
            {
                var result = packages.Scan();
                foreach (var source in result.Sources)
                    sources.Add(source);
                foreach (var item in result.Items)
                    items.Add(item);
            }),
            () => Run(installedApps.Name, () =>
            {
                foreach (var source in installedApps.Collect())
                    sources.Add(source);
            }));

        progress?.Report("Checking file signatures");
        var finished = new ConcurrentBag<AutostartItem>();
        Parallel.ForEach(
            items,
            new ParallelOptions { CancellationToken = cancellation, MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) },
            item => finished.Add(Finish(item)));

        var drivers = new DriverPackageCollector();
        Run(drivers.Name, () =>
        {
            foreach (var source in drivers.Collect(finished.Select(i => i.TargetPath)))
                sources.Add(source);
        });

        return new Snapshot(
            finished.OrderBy(i => i.Id, StringComparer.OrdinalIgnoreCase).ToList(),
            sources.OrderBy(s => s.Id, StringComparer.OrdinalIgnoreCase).ToList(),
            errors.ToList(),
            DateTimeOffset.Now);
    }

    /// <summary>Adds file evidence, decides protection, and gives startup entries a readable name.</summary>
    public AutostartItem Finish(AutostartItem item)
    {
        var evidence = item.TargetPath is null ? null : _evidence.Read(item.TargetPath);
        var withEvidence = item with { Evidence = evidence };
        var displayName = item.Kind is AutostartKind.RunKey or AutostartKind.StartupFolder
            ? evidence?.Description ?? item.DisplayName
            : item.DisplayName;
        return withEvidence with
        {
            DisplayName = displayName,
            IsProtected = ProtectionPolicy.IsProtected(withEvidence),
        };
    }

    /// <summary>Re-reads the current state of specific items straight from Windows.</summary>
    public IReadOnlyDictionary<string, AutostartItem> Find(IEnumerable<string> itemIds)
    {
        var wanted = new HashSet<string>(itemIds, StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, AutostartItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var prefix in wanted.Select(KindPrefix).Distinct())
        {
            if (CollectorFor(prefix) is not { } collector)
                continue;
            // A collector that throws is a real fault and is left to surface; swallowing it here
            // would make every item it covers look as if it no longer existed.
            foreach (var item in collector.Collect().Where(i => wanted.Contains(i.Id)))
                found[item.Id] = Finish(item);
        }
        return found;
    }

    private static string KindPrefix(string id)
    {
        var colon = id.IndexOf(':');
        return colon < 0 ? id : id[..colon];
    }

    private static IItemCollector? CollectorFor(string prefix) => prefix.ToLowerInvariant() switch
    {
        "service" => new ServiceCollector(),
        "task" => new ScheduledTaskCollector(),
        "runkey" => new RunKeyCollector(),
        "startupfolder" => new StartupFolderCollector(),
        "packagedtask" => new PackageCollector(),
        _ => null,
    };
}
