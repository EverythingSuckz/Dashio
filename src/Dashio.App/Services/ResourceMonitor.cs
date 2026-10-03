using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Dashio.Core.Evidence;
using Dashio.Core.Models;
using Dashio.Core.Processes;
using Microsoft.UI.Dispatching;

namespace Dashio.App.Services;

/// <summary>One reading of the whole machine, kept for the graphs.</summary>
public sealed record UsagePoint(DateTimeOffset Time, double CpuPercent, double MemoryPercent);

/// <summary>
/// Measures what every app is using, on a background loop, and hands each result to the UI thread.
/// It measures only while the window is showing, at the interval chosen in Settings.
/// </summary>
public sealed class ResourceMonitor
{
    public static readonly TimeSpan HistorySpan = TimeSpan.FromSeconds(60);

    /// <summary>The group every running copy of Dashio is shown under.</summary>
    public const string OwnGroupId = "dashio";

    /// <summary>Kept beyond the span that is shown: one reading at the longest interval, and a bit.</summary>
    private static readonly TimeSpan HistorySlack = TimeSpan.FromSeconds(12);

    /// <summary>Two readings this far apart give the first processor figures.</summary>
    private static readonly TimeSpan PrimeDelay = TimeSpan.FromMilliseconds(600);

    private sealed record Known(ProcessOwner Owner, RunningProcess Process, bool IsFinal);

    private readonly ProcessSampler _sampler = new();
    private readonly FileEvidenceReader _evidence;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly ConcurrentDictionary<string, FileEvidence> _facts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _requested = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<string> _toRead = Channel.CreateUnbounded<string>();

    private DispatcherQueue? _dispatcher;
    private volatile ProcessAttributor? _attributor;
    private volatile bool _visible = true;
    private volatile bool _refreshOnce;

    // Touched only by the measuring loop.
    private Dictionary<(int Pid, long Start), Known> _known = [];
    private IReadOnlyDictionary<int, IReadOnlyList<string>> _services = new Dictionary<int, IReadOnlyList<string>>();
    private ProcessAttributor? _knownFor;
    private SystemSample? _previous;
    private readonly Stopwatch _sincePrevious = new();
    private int _ticksSinceServices;

    // Touched only on the UI thread.
    private readonly List<UsagePoint> _history = [];
    private readonly Dictionary<string, AppGroup> _processOnly = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, AppUsage> _byGroup = new(StringComparer.OrdinalIgnoreCase);

    public ResourceMonitor(FileEvidenceReader evidence) => _evidence = evidence;

    public UsageSnapshot? Latest { get; private set; }
    public IReadOnlyList<UsagePoint> History => _history;

    /// <summary>Raised on the UI thread after each measurement.</summary>
    public event EventHandler? Updated;

    public void Start(DispatcherQueue dispatcher)
    {
        if (_dispatcher is not null)
            return;
        _dispatcher = dispatcher;
        _ = Task.Run(MeasureLoopAsync);
        _ = Task.Run(ReadEvidenceLoopAsync);
    }

    /// <summary>Call after a scan, so processes are matched against the new groups.</summary>
    public void UseScan(ProcessAttributor attributor)
    {
        _attributor = attributor;
        RefreshNow();
    }

    /// <summary>Whether the window can be seen. Nothing is measured for a minimised window.</summary>
    public void SetVisible(bool visible)
    {
        if (_visible == visible)
            return;
        _visible = visible;
        _wake.Release();
    }

    /// <summary>Measures once now, even when paused. Also picks up a changed interval.</summary>
    public void RefreshNow()
    {
        _refreshOnce = true;
        _wake.Release();
    }

    public AppUsage? UsageOf(string groupId) => _byGroup.GetValueOrDefault(groupId);

    /// <summary>An app that is running, or was, but has set nothing to start by itself.</summary>
    public AppGroup? FindGroup(string groupId) => _processOnly.GetValueOrDefault(groupId);

    /// <summary>An app that has been uninstalled is not remembered for having run.</summary>
    public void Forget(string groupId) => _processOnly.Remove(groupId);

    private async Task MeasureLoopAsync()
    {
        while (true)
        {
            var seconds = AppServices.Settings.RefreshSeconds;
            var live = _visible && seconds > 0;
            try
            {
                if (live || _refreshOnce)
                {
                    _refreshOnce = false;
                    // After a pause the last reading is old, and a percentage over that stretch would mislead.
                    var stale = _previous is null ||
                                _sincePrevious.Elapsed > TimeSpan.FromSeconds(Math.Max(seconds * 3, 10));
                    if (stale)
                    {
                        _previous = _sampler.Sample();
                        _sincePrevious.Restart();
                    }
                    // Two readings taken too close together give meaningless percentages.
                    var wait = PrimeDelay - _sincePrevious.Elapsed;
                    if (wait > TimeSpan.Zero)
                        await Task.Delay(wait);
                    Measure();
                }
            }
            catch (Exception)
            {
                // A failed reading is skipped; the next one may work.
            }

            await _wake.WaitAsync(live ? TimeSpan.FromSeconds(seconds) : Timeout.InfiniteTimeSpan);
        }
    }

    private void Measure()
    {
        var sample = _sampler.Sample();
        var cpu = UsageCalculator.ProcessCpuPercent(_previous, sample);
        var system = new SystemUsage(
            sample.MemoryTotalBytes - sample.MemoryAvailableBytes,
            sample.MemoryTotalBytes,
            UsageCalculator.SystemCpuPercent(_previous, sample));
        _previous = sample;
        _sincePrevious.Restart();

        var apps = _attributor is { } attributor ? AppsOf(sample, cpu, attributor) : [];
        var snapshot = new UsageSnapshot(system, apps, DateTimeOffset.Now);
        _dispatcher?.TryEnqueue(() => Publish(snapshot));
    }

    private List<AppUsage> AppsOf(SystemSample sample, IReadOnlyDictionary<int, double> cpu, ProcessAttributor attributor)
    {
        if (_knownFor != attributor)
        {
            _known = [];
            _knownFor = attributor;
        }

        var hasNewProcess = sample.Processes.Any(p => !_known.ContainsKey((p.Pid, p.StartTicks)));
        if (hasNewProcess || ++_ticksSinceServices >= 15)
        {
            _services = ServiceProcessMap.Read();
            _ticksSinceServices = 0;
        }

        var known = new Dictionary<(int, long), Known>(sample.Processes.Count);
        var usages = new List<(ProcessOwner Owner, ProcessUsage Usage)>(sample.Processes.Count);
        foreach (var process in sample.Processes)
        {
            var key = (process.Pid, process.StartTicks);
            if (!_known.TryGetValue(key, out var entry) || !entry.IsFinal)
                entry = Identify(process, entry?.Process, attributor);
            known[key] = entry;

            usages.Add((entry.Owner, new ProcessUsage(
                process.Pid, process.StartTicks, process.Name, entry.Process.Path, entry.Process.Services,
                process.PrivateBytes, cpu.GetValueOrDefault(process.Pid))));
        }
        _known = known;

        return usages
            .GroupBy(u => u.Owner.GroupId, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AppUsage(
                g.First().Owner,
                g.Sum(u => u.Usage.MemoryBytes),
                g.Sum(u => u.Usage.CpuPercent),
                g.Select(u => u.Usage).OrderByDescending(u => u.MemoryBytes).ToList()))
            .ToList();
    }

    private Known Identify(ProcessSample process, RunningProcess? seen, ProcessAttributor attributor)
    {
        var running = seen ?? new RunningProcess(
            process.Pid, process.Name, _sampler.ImagePath(process.Pid),
            _services.GetValueOrDefault(process.Pid, []));

        // In a build folder Dashio would be named after that folder ("win-x64").
        if (process.Pid == Environment.ProcessId || IsDashio(running.Path))
            return new Known(new ProcessOwner(OwnGroupId, "Dashio", null, running.Path, false, null, null), running, true);

        // The signature check is slow, so it runs elsewhere; until it is done the answer is provisional.
        var isFinal = true;
        var owner = attributor.Assign(running, path =>
        {
            if (_facts.TryGetValue(path, out var facts))
                return facts;
            isFinal = false;
            if (_requested.TryAdd(path, 0))
                _toRead.Writer.TryWrite(path);
            return null;
        });
        return new Known(owner, running, isFinal);
    }

    private static readonly string OwnFileName = Path.GetFileName(Environment.ProcessPath ?? "Dashio.exe");

    private static bool IsDashio(string? path) =>
        path is not null && Path.GetFileName(path).Equals(OwnFileName, StringComparison.OrdinalIgnoreCase);

    private async Task ReadEvidenceLoopAsync()
    {
        await foreach (var path in _toRead.Reader.ReadAllAsync())
        {
            try
            {
                _facts[path] = _evidence.Read(path);
            }
            catch (Exception)
            {
                _facts[path] = new FileEvidence { Path = path, Exists = false };
            }
        }
    }

    private void Publish(UsageSnapshot snapshot)
    {
        Latest = snapshot;
        _byGroup = snapshot.Apps.ToDictionary(a => a.GroupId, StringComparer.OrdinalIgnoreCase);

        foreach (var owner in snapshot.Apps.Select(a => a.Owner).Where(o => o.Group is null))
        {
            if (_processOnly.TryGetValue(owner.GroupId, out var existing) &&
                existing.Name == owner.Name && existing.IconPath == owner.IconPath && existing.Publisher == owner.Publisher)
                continue;
            _processOnly[owner.GroupId] = new AppGroup
            {
                Id = owner.GroupId,
                Name = owner.Name,
                Publisher = owner.Publisher,
                Items = [],
                Sources = owner.Source is null ? [] : [owner.Source],
                IconPath = owner.IconPath,
                IsWindows = owner.IsWindows,
            };
        }

        _history.Add(new UsagePoint(snapshot.TakenAt, snapshot.System.CpuPercent, snapshot.System.MemoryPercent));
        _history.RemoveAll(p => snapshot.TakenAt - p.Time > HistorySpan + HistorySlack);

        Updated?.Invoke(this, EventArgs.Empty);
    }
}
