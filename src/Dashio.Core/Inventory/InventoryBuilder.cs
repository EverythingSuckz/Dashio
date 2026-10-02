using Dashio.Core.Collectors;
using Dashio.Core.Models;
using Dashio.Core.Processes;

namespace Dashio.Core.Inventory;

/// <summary>
/// Turns what Windows lists as installed into one entry per app, with its folders and what opens it.
/// Pure: the folders and shortcuts are handed in, so it can be tested with made-up data.
/// </summary>
public static class InventoryBuilder
{
    /// <param name="topFolders">Top-level folders in the user's app data and ProgramData.</param>
    /// <param name="subfolders">Lists a folder's subfolders, used when a top folder is named after a maker.</param>
    /// <param name="folderExists">Whether an install folder is really there.</param>
    public static IReadOnlyList<InstalledApp> Build(
        IReadOnlyList<AppSource> sources,
        ProcessAttributor attributor,
        IReadOnlyList<Shortcut> shortcuts,
        IReadOnlyList<string> topFolders,
        Func<string, IReadOnlyList<string>> subfolders,
        Func<string, bool> folderExists,
        string packageDataRoot)
    {
        var drafts = new Dictionary<string, Draft>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources.Where(s => s.Kind != AppSourceKind.DriverPackage && !s.IsSystem && !s.IsHiddenComponent))
        {
            var owner = attributor.OwnerOfSource(source);
            if (owner.IsWindows)
                continue;
            if (!drafts.TryGetValue(owner.GroupId, out var draft))
                drafts[owner.GroupId] = draft = new Draft(owner);

            draft.Sources.Add(source);
            draft.AppIds.AddRange(source.AppIds);
            draft.Programs.AddRange(source.AppPrograms);
            if (source.InstallLocation is { } location && folderExists(location))
                draft.Add(location, FolderRole.Program);
            else
                draft.ReportedBytes += source.EstimatedBytes ?? 0;

            if (source.PackageFamilyName is { } family)
            {
                var data = Path.Combine(packageDataRoot, family);
                if (folderExists(data))
                    draft.Add(data, FolderRole.Data);
            }
        }

        var programFolders = drafts.Values
            .SelectMany(d => d.Folders.Where(f => f.Role == FolderRole.Program).Select(f => f.Path))
            .ToList();
        bool IsInsideAProgramFolder(string folder) => programFolders.Any(p =>
            folder.Equals(p, StringComparison.OrdinalIgnoreCase) || InstallFolders.Contains(p, folder + "\\x"));

        // A folder named after a maker ("Google") holds one folder per product.
        var candidates = topFolders.SelectMany(f => attributor.IsVendorFolder(f) ? subfolders(f) : [f]);
        foreach (var folder in candidates.Where(f => !IsInsideAProgramFolder(f)))
        {
            if (attributor.OwnerOfFolder(folder) is { } owner && drafts.TryGetValue(owner.GroupId, out var draft))
                draft.Add(folder, FolderRole.Data);
        }

        foreach (var shortcut in shortcuts)
        {
            if (attributor.OwnerOfPath(shortcut.TargetPath) is { } owner && drafts.TryGetValue(owner.GroupId, out var draft))
            {
                draft.Shortcuts.Add(shortcut);
                if (shortcut.TargetPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    draft.Programs.Add(shortcut.TargetPath);
            }
        }

        return drafts.Values
            .Select(d => new InstalledApp(
                d.Owner, d.Sources, WithoutNested(d.Folders), d.ReportedBytes,
                d.Sources.Select(s => s.InstalledOn).Where(t => t is not null).Min(),
                d.Shortcuts,
                d.AppIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                d.Programs.Distinct(StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderBy(a => a.Owner.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>A folder inside another folder of the same app would be counted twice.</summary>
    private static List<AppFolder> WithoutNested(List<AppFolder> folders) => folders
        .Where(f => !folders.Any(other => other != f && InstallFolders.Contains(other.Path, f.Path + "\\x")))
        .OrderBy(f => f.Role).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private sealed class Draft(ProcessOwner owner)
    {
        public ProcessOwner Owner { get; } = owner;
        public List<AppSource> Sources { get; } = [];
        public List<AppFolder> Folders { get; } = [];
        public List<Shortcut> Shortcuts { get; } = [];
        public List<string> AppIds { get; } = [];
        public List<string> Programs { get; } = [];
        public long ReportedBytes { get; set; }

        public void Add(string path, FolderRole role)
        {
            path = path.TrimEnd('\\');
            if (!Folders.Any(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                Folders.Add(new AppFolder(path, role));
        }
    }
}

/// <summary>Decides when each app was last opened, and whether it is fair to say it has not been.</summary>
public sealed class UsageResolver
{
    /// <summary>"Lately" never reaches further back than this, however old the records are.</summary>
    public static readonly TimeSpan Lately = TimeSpan.FromDays(90);

    /// <summary>Records that reach back less than this say too little to call anything unused.</summary>
    public static readonly TimeSpan ShortestUsefulReach = TimeSpan.FromDays(30);

    private readonly bool _launchesAreCurrent;
    private readonly Dictionary<string, DateTimeOffset> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _byAppId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _byOwner = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset>? _prefetch;
    private readonly Dictionary<string, int> _appsPerExeName = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<string, DateTimeOffset> _sightings;

    /// <param name="now">Passed in so tests can fix the date.</param>
    public UsageResolver(
        UsageEvidence evidence, IReadOnlyList<InstalledApp> apps, ProcessAttributor attributor, DateTimeOffset now)
    {
        _sightings = evidence.Sightings;
        _launchesAreCurrent = UserAssistReader.IsCurrent(evidence.Launches, now);
        Now = now;

        foreach (var launch in evidence.Launches)
        {
            if (launch.Target.Contains('!'))
                Keep(_byAppId, launch.Target, launch.LastRun);
            else
                Keep(_byPath, launch.Target, launch.LastRun);
        }
        // A launch of a program that is not behind any shortcut still counts for the app it belongs to.
        foreach (var (path, time) in _byPath.Where(p => p.Key.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
        {
            if (attributor.OwnerOfPath(path) is { } owner)
                Keep(_byOwner, owner.GroupId, time);
        }

        if (evidence.Prefetch is { } prefetch)
        {
            _prefetch = prefetch.ToDictionary(p => p.ExeName, p => p.LastRun, StringComparer.OrdinalIgnoreCase);
            foreach (var names in apps.Select(ExeNames))
            {
                foreach (var name in names)
                    _appsPerExeName[name] = _appsPerExeName.GetValueOrDefault(name) + 1;
            }
        }

        // How far back a "no" can be trusted: Prefetch only remembers the last thousand or so programs.
        DateTimeOffset? reach = null;
        if (evidence.Prefetch is { Count: > 0 } entries)
            reach = entries.Min(e => e.LastRun);
        if (_launchesAreCurrent)
        {
            var oldest = evidence.Launches.Min(l => l.LastRun);
            reach = reach is null || oldest < reach ? oldest : reach;
        }
        ReachesBackTo = reach;
        CoversFrom = reach is null || now - reach < ShortestUsefulReach
            ? null
            : reach < now - Lately ? now - Lately : reach;
    }

    /// <summary>How far back the records go at all, before any cap. Null when there are none.</summary>
    public DateTimeOffset? ReachesBackTo { get; }

    public DateTimeOffset Now { get; }

    /// <summary>
    /// The earliest moment the records can vouch for, capped at <see cref="Lately"/> ago.
    /// Null when nothing on this PC can show that an app was not opened.
    /// </summary>
    public DateTimeOffset? CoversFrom { get; }

    public AppUsageHistory Resolve(InstalledApp app, bool runningNow)
    {
        var last = LastOpened(app);
        if (runningNow)
            return new AppUsageHistory(UsageVerdict.RunningNow, Now, null);
        if (!app.IsLaunchable)
            return new AppUsageHistory(UsageVerdict.NotJudged, last, null);
        if (CoversFrom is not { } since || !CanVouchFor(app))
            return new AppUsageHistory(last is null ? UsageVerdict.Unknown : UsageVerdict.OpenedLately, last, null);
        if (last >= since)
            return new AppUsageHistory(UsageVerdict.OpenedLately, last, null);
        // Something installed after the records begin has not had its chance yet.
        if (app.InstalledOn > since)
            return new AppUsageHistory(UsageVerdict.NotJudged, last, null);
        return new AppUsageHistory(UsageVerdict.NotOpenedLately, last, since);
    }

    /// <summary>
    /// Whether the records would have noticed this app being opened. Prefetch can only speak for
    /// an app that has a program whose file name no other app shares.
    /// </summary>
    private bool CanVouchFor(InstalledApp app) =>
        _launchesAreCurrent ||
        (_prefetch is not null && ExeNames(app).Any(n => _appsPerExeName.GetValueOrDefault(n) == 1));

    private DateTimeOffset? LastOpened(InstalledApp app)
    {
        DateTimeOffset? last = null;
        void Consider(DateTimeOffset? time)
        {
            if (time is not null && (last is null || time > last))
                last = time;
        }

        Consider(_sightings.TryGetValue(app.Id, out var seen) ? seen : null);
        Consider(_byOwner.TryGetValue(app.Id, out var launched) ? launched : null);
        foreach (var id in app.AppIds)
            Consider(_byAppId.TryGetValue(id, out var time) ? time : null);
        foreach (var shortcut in app.Shortcuts)
        {
            Consider(_byPath.TryGetValue(shortcut.LinkPath, out var byLink) ? byLink : null);
            Consider(_byPath.TryGetValue(shortcut.TargetPath, out var byTarget) ? byTarget : null);
        }

        if (_prefetch is not null)
        {
            // Prefetch knows a program by file name only, so a name two apps share proves nothing.
            foreach (var name in ExeNames(app).Where(n => _appsPerExeName.GetValueOrDefault(n) == 1))
                Consider(_prefetch.TryGetValue(name, out var ran) ? ran : null);
        }
        return last;
    }

    private static IEnumerable<string> ExeNames(InstalledApp app) => app.Programs
        .Select(PrefetchReader.NameFor)
        .Distinct(StringComparer.OrdinalIgnoreCase);

    private static void Keep(Dictionary<string, DateTimeOffset> latest, string key, DateTimeOffset time)
    {
        if (!latest.TryGetValue(key, out var known) || time > known)
            latest[key] = time;
    }
}
