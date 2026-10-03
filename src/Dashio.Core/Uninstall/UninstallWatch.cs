using System.Text.Json;
using Dashio.Core.Models;
using Dashio.Core.Processes;

namespace Dashio.Core.Uninstall;

/// <summary>
/// The uninstaller and whatever it started. An installer commonly copies itself somewhere else,
/// starts the copy and exits, so the first process alone says nothing about whether it is still open.
/// </summary>
public sealed class UninstallerProcesses
{
    private readonly int _root;
    private readonly long _launchedAt;
    private readonly Dictionary<int, long> _startedAt = [];

    /// <param name="launchedAt">Just before the uninstaller was started, as a Windows file time.</param>
    public UninstallerProcesses(int rootPid, long launchedAt)
    {
        _root = rootPid;
        _launchedAt = launchedAt;
    }

    /// <summary>
    /// Whether any of them is running in this reading. Call it often: a program that starts and
    /// ends between two readings is not seen, and neither is what it started.
    /// </summary>
    public bool AnyRunning(IReadOnlyList<ProcessSample> processes)
    {
        // Windows reuses ids, so a process only counts when it started after the one it descends from.
        if (!_startedAt.ContainsKey(_root) &&
            processes.FirstOrDefault(p => p.Pid == _root && p.StartTicks >= _launchedAt) is { } root)
            _startedAt[_root] = root.StartTicks;

        var found = true;
        while (found)
        {
            found = false;
            foreach (var process in processes)
            {
                if (_startedAt.ContainsKey(process.Pid))
                    continue;
                var parentStarted = _startedAt.TryGetValue(process.ParentPid, out var known) ? known
                    : process.ParentPid == _root ? _launchedAt
                    : (long?)null;
                if (parentStarted is { } after && process.StartTicks >= after)
                {
                    _startedAt[process.Pid] = process.StartTicks;
                    found = true;
                }
            }
        }
        return processes.Any(p => _startedAt.TryGetValue(p.Pid, out var started) && started == p.StartTicks);
    }
}

public enum UninstallEnd
{
    /// <summary>Windows no longer lists the app.</summary>
    Removed,

    /// <summary>The uninstaller was open for a while, is closed now, and the app is still listed.</summary>
    Closed,

    /// <summary>
    /// It cannot be told from here: the uninstaller was gone almost at once, which is what one
    /// does that hands over to a launcher, or its process is not known, or it is taking very long.
    /// </summary>
    Unknown,
}

/// <param name="ClosedFor">How long the uninstaller must have been gone before it counts as closed.</param>
/// <param name="OpenFor">An uninstaller that was open for less than this may have handed over to something else.</param>
/// <param name="HandOverWait">How long to keep looking when the uninstaller's process was never seen.</param>
public sealed record FollowTimes(
    TimeSpan LookEvery, TimeSpan ClosedFor, TimeSpan OpenFor, TimeSpan HandOverWait, TimeSpan GiveUpAfter)
{
    public static readonly FollowTimes Default = new(
        TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8),
        TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(30));
}

/// <summary>
/// Waits for an uninstaller to finish. The process that was started says little, so what counts
/// is whether Windows still lists the app once the uninstaller and its copies are gone.
/// </summary>
public static class UninstallFollower
{
    /// <param name="uninstallerRunning">Null when the uninstaller's process is not known.</param>
    /// <param name="delay">Passed in, with the clock, so tests do not wait.</param>
    public static async Task<UninstallEnd> FollowAsync(
        Func<bool> stillInstalled, Func<bool>? uninstallerRunning, FollowTimes times,
        Func<TimeSpan, Task>? delay = null, Func<DateTimeOffset>? clock = null)
    {
        delay ??= Task.Delay;
        clock ??= () => DateTimeOffset.Now;
        var started = clock();
        DateTimeOffset? firstSeen = null, lastSeen = null;

        while (true)
        {
            if (!stillInstalled())
                return UninstallEnd.Removed;

            var now = clock();
            if (uninstallerRunning?.Invoke() == true)
            {
                firstSeen ??= now;
                lastSeen = now;
            }
            else if (firstSeen is { } first && lastSeen is { } last)
            {
                // Every process of it has gone, so there is nothing left to wait for.
                if (now - last >= times.ClosedFor)
                    return last - first >= times.OpenFor ? UninstallEnd.Closed : UninstallEnd.Unknown;
            }
            else if (now - started >= times.HandOverWait)
            {
                return UninstallEnd.Unknown;
            }

            if (now - started >= times.GiveUpAfter)
                return UninstallEnd.Unknown;
            await delay(times.LookEvery);
        }
    }
}

/// <summary>An uninstall that was started and whose end has not been seen yet.</summary>
public sealed record PendingUninstall(
    string SourceId, AppSourceKind Kind, string Name, string AppName, string? PackageFamilyName, DateTimeOffset StartedAt)
{
    public static PendingUninstall Of(UninstallPlan plan, DateTimeOffset now) => new(
        plan.Source.Id, plan.Source.Kind, plan.Source.Name, plan.AppName, plan.Source.PackageFamilyName, now);

    /// <summary>Enough of the entry to ask Windows whether it is still listed.</summary>
    public AppSource Source => new() { Kind = Kind, Id = SourceId, Name = Name, PackageFamilyName = PackageFamilyName };
}

/// <summary>
/// Remembers the uninstalls whose end Dashio did not see, because it was closed first or the
/// uninstaller handed over to a launcher. A later scan that finds the app gone can then still
/// record it.
/// </summary>
public sealed class PendingUninstalls(string path)
{
    /// <summary>After this long an app that is still installed was evidently not removed.</summary>
    public static readonly TimeSpan KeptFor = TimeSpan.FromHours(6);

    private readonly Lock _gate = new();

    public IReadOnlyList<PendingUninstall> Load()
    {
        lock (_gate)
        {
            try
            {
                return File.Exists(path) && JsonSerializer.Deserialize<List<PendingUninstall>>(File.ReadAllText(path)) is { } list
                    ? list
                    : [];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                return [];
            }
        }
    }

    public void Add(PendingUninstall pending) => Change(list =>
    {
        list.RemoveAll(p => p.SourceId.Equals(pending.SourceId, StringComparison.OrdinalIgnoreCase));
        list.Add(pending);
    });

    public void Remove(string sourceId) =>
        Change(list => list.RemoveAll(p => p.SourceId.Equals(sourceId, StringComparison.OrdinalIgnoreCase)));

    private void Change(Action<List<PendingUninstall>> change)
    {
        lock (_gate)
        {
            var list = Load().ToList();
            change(list);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(list));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Sorts what is pending into the apps that have gone since and the ones given up on.
    /// Both are forgotten; the rest stays for the next look.
    /// </summary>
    public (IReadOnlyList<PendingUninstall> Gone, IReadOnlyList<PendingUninstall> GivenUp) Settle(
        Func<AppSource, bool> stillInstalled, DateTimeOffset now, Func<PendingUninstall, bool>? skip = null)
    {
        var gone = new List<PendingUninstall>();
        var givenUp = new List<PendingUninstall>();
        foreach (var pending in Load().Where(p => skip?.Invoke(p) != true))
        {
            if (!stillInstalled(pending.Source))
                gone.Add(pending);
            else if (now - pending.StartedAt > KeptFor)
                givenUp.Add(pending);
        }
        foreach (var done in gone.Concat(givenUp))
            Remove(done.SourceId);
        return (gone, givenUp);
    }
}
