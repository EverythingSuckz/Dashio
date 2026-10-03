using Dashio.Core;
using Dashio.Core.Storage;
using Microsoft.UI.Dispatching;

namespace Dashio.App.Services;

/// <summary>The reading of one drive's folders: still running, finished, or brought back from the last session.</summary>
public sealed class DriveScan
{
    private readonly CancellationTokenSource _cancellation = new();

    /// <summary>A reading that is about to start.</summary>
    public DriveScan(string root)
    {
        Root = root;
        Tree = DiskScanner.CreateRoot(root);
    }

    /// <summary>A reading that is already complete: kept from before, or made by the helper.</summary>
    public DriveScan(string root, SavedReading reading)
    {
        Root = root;
        Tree = reading.Tree;
        Finished = reading.ReadAt;
        ReadEverything = reading.ReadEverything;
    }

    public string Root { get; }
    public FolderNode Tree { get; }
    public DateTimeOffset? Finished { get; internal set; }

    /// <summary>Read with administrator rights, so nothing was left out as protected.</summary>
    public bool ReadEverything { get; }

    public bool IsRunning => Finished is null && !_cancellation.IsCancellationRequested;
    internal CancellationToken Cancellation => _cancellation.Token;

    public void Cancel() => _cancellation.Cancel();
}

/// <summary>
/// Reads the folders of a drive in the background, one reading per drive at a time, and keeps
/// the last reading of each drive on disk so it is there the next time Dashio opens.
/// </summary>
public sealed class DiskScans
{
    private readonly Dictionary<string, DriveScan> _scans = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised on the UI thread when a reading has gone through the whole drive.</summary>
    public event EventHandler<DriveScan>? Finished;

    public DriveScan? Find(string root) => _scans.GetValueOrDefault(root);

    /// <summary>The reading kept from an earlier session, if there is one and nothing newer. Call on the UI thread.</summary>
    public async Task<DriveScan?> LoadKeptAsync(string root)
    {
        if (Find(root) is { } known)
            return known;
        var kept = await Task.Run(() => FolderTreeFile.Load(DashioPaths.DriveReading(root[0])));
        if (Find(root) is { } started)
            return started;
        return kept is null ? null : _scans[root] = new DriveScan(root, kept);
    }

    /// <summary>Starts reading the drive afresh. Call on the UI thread.</summary>
    public DriveScan Start(string root)
    {
        Find(root)?.Cancel();
        var scan = _scans[root] = new DriveScan(root);
        var dispatcher = DispatcherQueue.GetForCurrentThread();

        // A drive holds millions of files, so this runs apart from everything else and gives way.
        new Thread(() =>
        {
            DiskScanner.Scan(scan.Tree, scan.Cancellation);
            if (scan.Cancellation.IsCancellationRequested)
                return;
            var finished = DateTimeOffset.Now;
            Keep(scan.Tree, finished, readEverything: false);
            dispatcher.TryEnqueue(() =>
            {
                if (scan.Cancellation.IsCancellationRequested)
                    return;
                scan.Finished = finished;
                Finished?.Invoke(this, scan);
            });
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "Dashio disk scan",
        }.Start();
        return scan;
    }

    /// <summary>
    /// Reads the drive through the elevated helper, which is faster and leaves nothing out.
    /// Call on the UI thread; Windows asks for administrator permission first.
    /// </summary>
    /// <returns>A sentence about what went wrong, or null when it worked or was cancelled.</returns>
    public async Task<(bool Cancelled, string? Error)> ReadAsAdminAsync(string root, Action<double> progress)
    {
        var result = await AppServices.Helper.ReadDriveAsync(root[0], progress);
        if (result.Cancelled)
            return (true, null);
        if (result.Reading is not { } reading)
            return (false, result.Error ?? "The drive could not be read.");

        Find(root)?.Cancel();
        var scan = _scans[root] = new DriveScan(root, reading);
        _ = Task.Run(() => Keep(reading.Tree, reading.ReadAt, readEverything: true));
        Finished?.Invoke(this, scan);
        return (false, null);
    }

    /// <summary>Saves a finished reading again after something in it was deleted.</summary>
    public void KeepAgain(DriveScan scan)
    {
        if (scan.Finished is { } finished)
            _ = Task.Run(() => Keep(scan.Tree, finished, scan.ReadEverything));
    }

    private static void Keep(FolderNode tree, DateTimeOffset readAt, bool readEverything)
    {
        try
        {
            FolderTreeFile.Save(DashioPaths.DriveReading(tree.Path[0]), tree, readAt, readEverything);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Keeping the reading is a convenience; the page still has it for this session.
        }
    }
}
