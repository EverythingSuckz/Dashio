using System.Text.Json;
using Dashio.Core;
using Dashio.Core.Inventory;
using Dashio.Core.Models;
using Dashio.Core.Processes;
using Microsoft.UI.Dispatching;

namespace Dashio.App.Services;

/// <summary>One installed app with its measured size and when it was last opened.</summary>
/// <param name="FolderBytes">The size of each folder, or null while it has not been measured yet.</param>
public sealed record InstalledAppView(
    InstalledApp App, IReadOnlyList<(AppFolder Folder, long? Bytes)> FolderBytes, AppUsageHistory Usage)
{
    public string Id => App.Id;
    public long ProgramBytes => Sum(FolderRole.Program);
    public long DataBytes => Sum(FolderRole.Data);
    public long TotalBytes => ProgramBytes + DataBytes + App.ReportedBytes;

    /// <summary>Some folder is still waiting to be measured, so the total will grow.</summary>
    public bool IsMeasuring => FolderBytes.Any(f => f.Bytes is null);

    /// <summary>Nothing could be measured; the figure is the one Windows recorded at install time.</summary>
    public bool IsReportedOnly => App.Folders.Count == 0 && App.ReportedBytes > 0;

    private long Sum(FolderRole role) => FolderBytes.Where(f => f.Folder.Role == role).Sum(f => f.Bytes ?? 0);
}

/// <summary>
/// Everything Windows lists as installed, with sizes measured in the background and a verdict on
/// when each app was last opened. Rebuilt after each full scan.
/// </summary>
public sealed class InventoryState
{
    private static readonly TimeSpan PublishEvery = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan SaveSightingsEvery = TimeSpan.FromMinutes(5);

    private readonly SizeCache _sizes = new(DashioPaths.Sizes);
    private readonly Dictionary<string, DateTimeOffset> _sightings = LoadSightings();
    private readonly Dictionary<string, AppGroup> _groups = new(StringComparer.OrdinalIgnoreCase);

    private DispatcherQueue? _dispatcher;
    private CancellationTokenSource? _building;
    private IReadOnlyList<InstalledApp> _apps = [];
    private Dictionary<string, HashSet<string>> _programs = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<LaunchRecord> _launches = [];
    private IReadOnlyList<PrefetchEntry>? _prefetch;
    private ProcessAttributor? _attributor;
    private DateTimeOffset _sightingsSaved = DateTimeOffset.Now;
    private Dictionary<string, InstalledAppView> _byId = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<InstalledAppView> Apps { get; private set; } = [];

    /// <summary>False until the list has been built once.</summary>
    public bool IsReady { get; private set; }

    /// <summary>Folders are still being measured.</summary>
    public bool IsMeasuring { get; private set; }

    /// <summary>The earliest moment the records can vouch for. Null when nothing can show an app was not opened.</summary>
    public DateTimeOffset? CoversFrom { get; private set; }

    /// <summary>How far back the records go, even when that is too short to be useful.</summary>
    public DateTimeOffset? ReachesBackTo { get; private set; }

    /// <summary>The administrator check has been run since Dashio started.</summary>
    public bool HasAdminCheck => _prefetch is not null;

    /// <summary>Raised on the UI thread when the list, a size or a verdict changes.</summary>
    public event EventHandler? Changed;

    public void Start(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        AppServices.Monitor.Updated += (_, _) => NoteWhatIsOpen();
    }

    public InstalledAppView? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>An installed app that neither starts anything nor is running still gets a page.</summary>
    public AppGroup? FindGroup(string id)
    {
        if (_groups.TryGetValue(id, out var group))
            return group;
        if (Find(id) is not { } view)
            return null;
        var owner = view.App.Owner;
        return _groups[id] = new AppGroup
        {
            Id = owner.GroupId,
            Name = owner.Name,
            Publisher = owner.Publisher,
            Items = [],
            Sources = view.App.Sources,
            IconPath = owner.IconPath ?? view.App.Programs.FirstOrDefault(),
        };
    }

    /// <summary>Whether the program a person opens is running, as opposed to a background service of the app.</summary>
    public bool IsOpenNow(string id) =>
        _programs.TryGetValue(id, out var programs) &&
        AppServices.Monitor.UsageOf(id) is { } usage &&
        usage.Processes.Any(p => p.Path is not null && programs.Contains(p.Path));

    /// <summary>Call on the UI thread after a full scan.</summary>
    public void Rebuild(IReadOnlyList<AppSource> sources, ProcessAttributor attributor)
    {
        _building?.Cancel();
        var building = _building = new CancellationTokenSource();
        _attributor = attributor;

        // Measuring reads hundreds of thousands of files, so it runs apart from everything else and gives way.
        new Thread(() => Build(sources, attributor, building.Token))
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "Dashio inventory",
        }.Start();
    }

    private void Build(IReadOnlyList<AppSource> sources, ProcessAttributor attributor, CancellationToken cancellation)
    {
        try
        {
            var apps = InventoryBuilder.Build(
                sources, attributor, StartMenuCollector.Collect(), DataFolderCollector.TopFolders(),
                DataFolderCollector.Subfolders, Directory.Exists, DataFolderCollector.PackageDataRoot);
            var launches = UserAssistReader.Read();
            if (cancellation.IsCancellationRequested)
                return;

            Post(() =>
            {
                _apps = apps;
                _launches = launches;
                _programs = apps.ToDictionary(
                    a => a.Id, a => a.Programs.ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
                _groups.Clear();
                IsMeasuring = true;
                Publish();
            }, cancellation);

            var folders = apps.SelectMany(a => a.Folders).Select(f => f.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var lastPublish = DateTimeOffset.Now;
            foreach (var folder in folders.Where(f => !_sizes.IsFresh(f, DateTimeOffset.Now)))
            {
                var bytes = FolderSizer.Measure(folder, cancellation);
                _sizes.Set(folder, bytes, DateTimeOffset.Now);
                if (DateTimeOffset.Now - lastPublish >= PublishEvery)
                {
                    lastPublish = DateTimeOffset.Now;
                    Post(Publish, cancellation);
                }
            }

            _sizes.Save(folders);
            Post(() =>
            {
                IsMeasuring = false;
                Publish();
            }, cancellation);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // The installed list is an extra; a failure here must not take the app down.
            Post(() =>
            {
                IsMeasuring = false;
                IsReady = true;
                Changed?.Invoke(this, EventArgs.Empty);
            }, cancellation);
        }
    }

    private void Post(Action action, CancellationToken cancellation) => _dispatcher?.TryEnqueue(() =>
    {
        if (!cancellation.IsCancellationRequested)
            action();
    });

    /// <summary>Recomputes sizes and verdicts from what is known right now. UI thread only.</summary>
    private void Publish()
    {
        if (_attributor is null)
            return;

        var resolver = new UsageResolver(
            new UsageEvidence(_launches, _prefetch, _sightings), _apps, _attributor, DateTimeOffset.Now);
        CoversFrom = resolver.CoversFrom;
        ReachesBackTo = resolver.ReachesBackTo;

        Apps = _apps
            .Select(app => new InstalledAppView(
                app,
                app.Folders.Select(f => (f, _sizes.Last(f.Path))).ToList(),
                resolver.Resolve(app, IsOpenNow(app.Id))))
            .ToList();
        _byId = Apps.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        IsReady = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Asks the helper when each program last ran. Needs the administrator prompt.</summary>
    public async Task<(string Message, bool IsError)> RunAdminCheckAsync()
    {
        var result = await AppServices.Helper.ScanPrefetchAsync();
        if (result.Cancelled)
            return ("The administrator prompt was cancelled. Nothing was checked.", false);
        if (result.Error is not null)
            return ($"The check failed: {result.Error}", true);
        if (result.Entries.Count == 0)
            return ("Windows is not keeping a record of the programs it runs on this PC, so there is nothing to check.", false);

        _prefetch = result.Entries;
        Publish();

        var unused = Apps.Count(a => a.Usage.Verdict == UsageVerdict.NotOpenedLately);
        return CoversFrom is { } since
            ? ($"Windows remembers the programs it has run since {since:d MMMM}. " +
               $"{(unused == 1 ? "1 app has" : $"{unused} apps have")} not been opened in that time.", false)
            : ("Windows only remembers the last few days of programs on this PC, which is too short to call any app unused.", false);
    }

    /// <summary>Remembers which apps a person has open, so "last opened" improves the longer Dashio is used.</summary>
    private void NoteWhatIsOpen()
    {
        if (_apps.Count == 0)
            return;
        var now = DateTimeOffset.Now;
        var firstSighting = false;
        foreach (var app in _apps.Where(a => IsOpenNow(a.Id)))
        {
            firstSighting |= !_sightings.ContainsKey(app.Id);
            _sightings[app.Id] = now;
        }

        // A first sighting is written at once, so it survives Dashio being closed a minute later.
        if (!firstSighting && now - _sightingsSaved < SaveSightingsEvery)
            return;
        _sightingsSaved = now;
        var json = JsonSerializer.Serialize(_sightings);
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DashioPaths.Sightings)!);
                File.WriteAllText(DashioPaths.Sightings, json);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        });
    }

    private static Dictionary<string, DateTimeOffset> LoadSightings()
    {
        try
        {
            if (File.Exists(DashioPaths.Sightings) &&
                JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(DashioPaths.Sightings)) is { } loaded)
                return new Dictionary<string, DateTimeOffset>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
    }
}
