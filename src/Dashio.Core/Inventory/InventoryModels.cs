using Dashio.Core.Models;
using Dashio.Core.Processes;

namespace Dashio.Core.Inventory;

/// <summary>A Start menu shortcut: something a person opens.</summary>
public sealed record Shortcut(string Name, string LinkPath, string TargetPath);

public enum FolderRole
{
    /// <summary>Where the app's own files are installed.</summary>
    Program,

    /// <summary>Settings, caches and other data the app keeps elsewhere.</summary>
    Data,
}

public sealed record AppFolder(string Path, FolderRole Role);

/// <summary>One app Windows lists as installed, with where its files are and how it is opened.</summary>
/// <param name="Owner">The same identity processes and startup items use, so ids line up across pages.</param>
/// <param name="ReportedBytes">The size Windows recorded, for the parts that have no folder to measure.</param>
/// <param name="AppIds">Store apps in it that can be opened, as <c>family!AppId</c>.</param>
/// <param name="Programs">The program files a person starts: shortcut targets and Store app programs.</param>
public sealed record InstalledApp(
    ProcessOwner Owner,
    IReadOnlyList<AppSource> Sources,
    IReadOnlyList<AppFolder> Folders,
    long ReportedBytes,
    DateTimeOffset? InstalledOn,
    IReadOnlyList<Shortcut> Shortcuts,
    IReadOnlyList<string> AppIds,
    IReadOnlyList<string> Programs)
{
    public string Id => Owner.GroupId;

    /// <summary>Has something a person opens. Drivers and runtimes do not, so they are never called unused.</summary>
    public bool IsLaunchable => Shortcuts.Count > 0 || AppIds.Count > 0;

    /// <summary>
    /// A part that other software needs, not an app in its own right: a runtime, a redistributable,
    /// a driver, a codec. Only said when every entry says so itself, because hiding a real app is
    /// worse than listing a part: a game opened from its launcher has nothing to open either.
    /// </summary>
    public bool IsComponent => !IsLaunchable && Sources.Count > 0 && Sources.All(ComponentRule.IsComponent);
}

/// <summary>
/// Tells the parts other software needs from the apps a person chose to install. The words a part
/// calls itself by are in <c>attribution-overrides.json</c> ("componentWords").
/// </summary>
public static class ComponentRule
{
    private static readonly Lazy<Dashio.Core.Attribution.AttributionOverrides> Overrides =
        new(Dashio.Core.Attribution.AttributionOverrides.LoadDefault);

    public static bool IsComponent(AppSource source) => source.Kind switch
    {
        // A package with nothing to open: a codec, a language pack, a framework, a context menu.
        AppSourceKind.StorePackage => source.AppIds.Count == 0,
        AppSourceKind.InstalledApp => Overrides.Value.IsComponentName(source.Name),
        _ => true,
    };
}

/// <summary>One program Windows remembers launching from Start, the taskbar or Explorer.</summary>
/// <param name="Target">A full path to a program or shortcut, or a Store app id (<c>family!AppId</c>).</param>
public sealed record LaunchRecord(string Target, DateTimeOffset LastRun);

/// <summary>One program in the Prefetch folder: its file name in capitals, and when it last ran.</summary>
public sealed record PrefetchEntry(string ExeName, DateTimeOffset LastRun);

public sealed record PrefetchScanResponse(IReadOnlyList<PrefetchEntry> Entries, string? Error = null);

/// <summary>Everything known about when apps were opened.</summary>
/// <param name="Prefetch">Null until the administrator check has been run.</param>
/// <param name="Sightings">App id to the last time Dashio itself saw it running.</param>
public sealed record UsageEvidence(
    IReadOnlyList<LaunchRecord> Launches,
    IReadOnlyList<PrefetchEntry>? Prefetch,
    IReadOnlyDictionary<string, DateTimeOffset> Sightings);

public enum UsageVerdict
{
    /// <summary>Not something a person opens, or installed too recently to judge.</summary>
    NotJudged,
    RunningNow,
    OpenedLately,
    NotOpenedLately,

    /// <summary>Nothing on this PC can vouch for the app not having been opened.</summary>
    Unknown,
}

/// <param name="LastOpened">The latest sign of the app being opened, if there is one.</param>
/// <param name="NotSince">For <see cref="UsageVerdict.NotOpenedLately"/>: no sign of use since this moment.</param>
public sealed record AppUsageHistory(UsageVerdict Verdict, DateTimeOffset? LastOpened, DateTimeOffset? NotSince);
