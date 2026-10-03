using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Dashio.App.Services;
using Dashio.Core.Storage;
using Microsoft.UI.Xaml;

namespace Dashio.App.ViewModels;

/// <summary>One fixed drive, for the cards at the top of the Storage page.</summary>
public sealed partial class DriveViewModel : ObservableObject
{
    /// <summary>From this share in use a drive is called nearly full.</summary>
    private const double NearlyFullPercent = 90;

    public DriveViewModel(string root) => Root = root;

    public string Root { get; }
    public long UsedBytes { get; private set; }

    [ObservableProperty]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    public partial string FreeText { get; set; } = "";

    [ObservableProperty]
    public partial string Detail { get; set; } = "";

    [ObservableProperty]
    public partial double UsedPercent { get; set; }

    [ObservableProperty]
    public partial bool IsNearlyFull { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial string AccessibleName { get; set; } = "";

    public void Update(DriveInfo drive)
    {
        var letter = Root.TrimEnd('\\');
        Title = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? $"Drive {letter}" : $"{drive.VolumeLabel} ({letter})";
        UsedBytes = drive.TotalSize - drive.AvailableFreeSpace;
        FreeText = $"{UsageText.Memory(drive.AvailableFreeSpace)} free";
        Detail = $"{UsageText.Memory(UsedBytes)} used of {UsageText.Memory(drive.TotalSize)}";
        UsedPercent = drive.TotalSize <= 0 ? 0 : 100.0 * UsedBytes / drive.TotalSize;
        IsNearlyFull = UsedPercent >= NearlyFullPercent;
        AccessibleName = $"{Title}, {FreeText}, {Detail}{(IsNearlyFull ? ", nearly full" : "")}";
    }
}

public enum FolderRowKind
{
    Folder,
    File,
    /// <summary>The files of a folder that are not listed one by one.</summary>
    OtherFiles,
    /// <summary>What the drive reports as used but the scan could not reach.</summary>
    Unreadable,
}

/// <summary>
/// Something inside the folder being shown: a subfolder, a large file, or what is left over.
/// The list and the map show the same entries, so both are built from this.
/// </summary>
public sealed partial class FolderEntryViewModel : ObservableObject
{
    public FolderEntryViewModel(string key, FolderRowKind kind, string? path, FolderNode? node, StorageColumns columns)
    {
        Key = key;
        Kind = kind;
        Path = path;
        Node = node;
        Columns = columns;
    }

    /// <summary>Stays the same while the figures change, so the same line and tile are updated rather than replaced.</summary>
    public string Key { get; }
    public FolderRowKind Kind { get; }
    public string? Path { get; }
    public FolderNode? Node { get; }
    public StorageColumns Columns { get; }
    public long Bytes { get; private set; }
    public long Files { get; private set; }

    public bool IsFolder => Kind == FolderRowKind.Folder;
    public bool HasPath => Path is not null;
    public string Glyph => Kind switch
    {
        FolderRowKind.Folder => "\uE8B7",
        FolderRowKind.File => "\uE7C3",
        FolderRowKind.Unreadable => "\uE72E",
        _ => "\uE8A5",
    };

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string SizeText { get; set; } = "";

    [ObservableProperty]
    public partial string PercentText { get; set; } = "";

    /// <summary>The bar's length, 0 to 100: the share of the folder this entry is in.</summary>
    [ObservableProperty]
    public partial double Share { get; set; }

    [ObservableProperty]
    public partial string FilesText { get; set; } = "";

    [ObservableProperty]
    public partial string Note { get; set; } = "";

    [ObservableProperty]
    public partial string Tip { get; set; } = "";

    [ObservableProperty]
    public partial string AccessibleName { get; set; } = "";

    public string RevealName => $"Show {Name} in File Explorer";

    // ---- As a tile of the map ----

    /// <summary>How strongly the tile is coloured, 0 to 1: larger entries stand out.</summary>
    [ObservableProperty]
    public partial double Tint { get; set; }

    /// <summary>A tile too small to hold its name shows none.</summary>
    [ObservableProperty]
    public partial bool HasLabel { get; set; }

    public void Update(string name, long bytes, long files, long parentBytes, long largestSibling, string note, string explanation)
    {
        Name = name;
        Bytes = bytes;
        Files = files;
        SizeText = UsageText.Memory(bytes);
        var share = parentBytes <= 0 ? 0 : 100.0 * bytes / parentBytes;
        Share = Math.Min(100, share);
        PercentText = share switch
        {
            <= 0 => "0%",
            < 0.1 => "<0.1%",
            < 10 => $"{share:F1}%",
            _ => $"{share:F0}%",
        };
        FilesText = Kind == FolderRowKind.Folder ? $"{files:N0}" : "";
        Note = note;
        Tip = $"{name}: {SizeText}, {PercentText} of this folder" + (explanation.Length > 0 ? $". {explanation}" : "");
        AccessibleName = $"{name}, {SizeText}, {PercentText} of this folder" +
                         (FilesText.Length > 0 ? $", {FilesText} files" : "") +
                         (note.Length > 0 ? $", {note}" : "");
        Tint = largestSibling <= 0 ? 0.25 : 0.25 + 0.6 * Math.Sqrt((double)bytes / largestSibling);
        HasLabel = share >= 3;
    }
}

/// <summary>One of the largest files on the drive, wherever it is.</summary>
public sealed record LargeFileRow(string Name, string Folder, string SizeText, string Path, double Share)
{
    public string AccessibleName => $"{Name}, {SizeText}, in {Folder}";
    public string RevealName => $"Show {Name} in File Explorer";
}

/// <summary>One step of the path shown above the folder list.</summary>
public sealed record Crumb(FolderNode Folder, string Label);

/// <summary>Which columns of the folder list fit the page. Shared by the headings and every row.</summary>
public sealed partial class StorageColumns : ObservableObject
{
    /// <summary>The bar of the folder list, which goes when the map takes part of the width.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BarWidth))]
    public partial bool ShowBar { get; set; } = true;

    public GridLength BarWidth => Ui.ColumnWidth(ShowBar, 200);
}

public enum FolderSort
{
    Size,
    Name,
    Files,
}

public sealed partial class StorageViewModel : ObservableObject
{
    /// <summary>The map shows this many entries of a folder; a folder with more is mostly small ones.</summary>
    private const int MostTiles = 40;

    /// <summary>How many of the drive's largest files are listed.</summary>
    private const int MostLargeFiles = 200;

    private readonly List<FolderNode> _back = [];
    private readonly List<FolderNode> _forward = [];
    private DriveScan? _scan;

    public ObservableCollection<DriveViewModel> Drives { get; } = [];
    public ObservableCollection<FolderEntryViewModel> FolderRows { get; } = [];
    public ObservableCollection<FolderEntryViewModel> Tiles { get; } = [];
    public ObservableCollection<Crumb> Trail { get; } = [];
    public ObservableCollection<LargeFileRow> LargeFiles { get; } = [];

    /// <summary>Raised when the tiles of the map have changed, so the map can be laid out again.</summary>
    public event EventHandler? MapChanged;
    public StorageColumns Columns { get; } = new();

    public FolderSort FolderSort { get; private set; }
    public bool FolderSortReversed { get; private set; }
    public DriveViewModel? SelectedDrive { get; private set; }
    public FolderNode? Current { get; private set; }

    /// <summary>The chosen drive has not been read yet, so there is only the offer to read it.</summary>
    [ObservableProperty]
    public partial bool CanStartScan { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFolders), nameof(ShowFiles))]
    public partial bool HasScan { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    public partial bool IsScanning { get; set; }

    /// <summary>The helper is reading the drive behind the administrator prompt.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    public partial bool IsAdminReading { get; set; }

    public bool IsBusy => IsScanning || IsAdminReading;

    /// <summary>The list of largest files is showing instead of the folders.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFolders), nameof(ShowFiles))]
    public partial bool ShowingFiles { get; set; }

    public bool ShowFolders => HasScan && !ShowingFiles;
    public bool ShowFiles => HasScan && ShowingFiles;

    [ObservableProperty]
    public partial bool HasNoLargeFiles { get; set; }

    /// <summary>How much of what the drive holds has been read, 0 to 100.</summary>
    [ObservableProperty]
    public partial double ScanPercent { get; set; }

    [ObservableProperty]
    public partial string ScanPercentText { get; set; } = "";

    [ObservableProperty]
    public partial string ScanStatus { get; set; } = "";

    [ObservableProperty]
    public partial string StartTitle { get; set; } = "";

    [ObservableProperty]
    public partial bool IsFolderEmpty { get; set; }

    [ObservableProperty]
    public partial bool CanGoBack { get; set; }

    [ObservableProperty]
    public partial bool CanGoForward { get; set; }

    [ObservableProperty]
    public partial bool CanGoUp { get; set; }

    // ---- Drives ----

    public void RefreshDrives()
    {
        var found = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .ToList();
        foreach (var drive in found)
        {
            var row = Drives.FirstOrDefault(d => d.Root.Equals(drive.Name, StringComparison.OrdinalIgnoreCase));
            if (row is null)
                Drives.Add(row = new DriveViewModel(drive.Name));
            try
            {
                row.Update(drive);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        foreach (var gone in Drives.Where(d => found.All(f => !f.Name.Equals(d.Root, StringComparison.OrdinalIgnoreCase))).ToList())
            Drives.Remove(gone);

        if (SelectedDrive is null || !Drives.Contains(SelectedDrive))
        {
            // The Windows drive first: it is the one that fills up.
            var windows = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            Select(Drives.FirstOrDefault(d => d.Root.Equals(windows, StringComparison.OrdinalIgnoreCase)) ?? Drives.FirstOrDefault());
        }
    }

    public void Select(DriveViewModel? drive)
    {
        SelectedDrive = drive;
        foreach (var each in Drives)
            each.IsSelected = each == drive;
        _scan = drive is null ? null : AppServices.Disk.Find(drive.Root);
        StartTitle = drive is null ? "" : $"See what is filling {drive.Title}";
        ForgetHistory();
        Show(_scan?.Tree);
        _ = RefreshLargeFilesAsync();
        if (drive is not null && _scan is null)
            _ = ShowKeptAsync(drive);
    }

    /// <summary>The reading kept from the last session shows up as soon as it has been loaded.</summary>
    private async Task ShowKeptAsync(DriveViewModel drive)
    {
        var kept = await AppServices.Disk.LoadKeptAsync(drive.Root);
        if (kept is null || SelectedDrive != drive || _scan is not null)
            return;
        _scan = kept;
        Show(kept.Tree);
        _ = RefreshLargeFilesAsync();
    }

    public void StartScan()
    {
        if (SelectedDrive is null || IsAdminReading)
            return;
        _scan = AppServices.Disk.Start(SelectedDrive.Root);
        ForgetHistory();
        Show(_scan.Tree);
    }

    /// <summary>Reads the drive through the helper. Returns what to tell the user when it did not work.</summary>
    public async Task<string?> ReadAsAdminAsync()
    {
        if (SelectedDrive is not { } drive || IsAdminReading)
            return null;

        IsAdminReading = true;
        ScanPercent = 0;
        ScanPercentText = "";
        try
        {
            var (_, error) = await AppServices.Disk.ReadAsAdminAsync(drive.Root, fraction =>
            {
                ScanPercent = 100 * fraction;
                ScanPercentText = $"{ScanPercent:F0}%";
            });
            return error;
        }
        finally
        {
            IsAdminReading = false;
            if (SelectedDrive == drive)
            {
                _scan = AppServices.Disk.Find(drive.Root);
                ForgetHistory();
                Show(_scan?.Tree);
                // The page heard that the scan was done before it was put in place here.
                _ = RefreshLargeFilesAsync();
            }
        }
    }

    /// <summary>Why the file or folder may not be deleted from here, or null when it may.</summary>
    public string? WhyNotDelete(string? path) =>
        IsBusy || _scan is null ? "The drive is being scanned." : FileRemover.WhyNot(path);

    /// <summary>
    /// Deletes a file or folder, takes it out of the figures and records it.
    /// Returns what to tell the user when it did not work.
    /// </summary>
    public async Task<string?> DeleteAsync(string path, bool permanently)
    {
        if (_scan is not { } scan || WhyNotDelete(path) is not null)
            return null;

        var name = Path.GetFileName(path.TrimEnd('\\'));
        var folder = Path.GetDirectoryName(path.TrimEnd('\\')) ?? "";
        string? error = null;
        try
        {
            await Task.Run(() => FileRemover.Delete(path, permanently));
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception e)
        {
            error = e.Message;
        }

        AppServices.Journal.Append(
        [
            new Dashio.Core.Journal.JournalEntry
            {
                Id = Guid.NewGuid(),
                BatchId = Guid.NewGuid(),
                Time = DateTimeOffset.Now,
                ItemId = $"file:{path}",
                Kind = Dashio.Core.Models.AutostartKind.File,
                ItemName = name,
                AppName = folder,
                Action = Dashio.Core.Changes.ChangeAction.Delete,
                Before = new Dashio.Core.Changes.ItemState(true),
                Target = new Dashio.Core.Changes.ItemState(false),
                Result = error is null ? Dashio.Core.Changes.JournalResult.Applied : Dashio.Core.Changes.JournalResult.Failed,
                Error = error,
                Note = permanently ? "Deleted for good" : "In the Recycle Bin",
            },
        ]);
        await AppServices.State.ReloadJournalAsync();
        if (error is not null)
            return error;

        if (scan.Tree.Find(folder)?.Forget(name) == true)
            AppServices.Disk.KeepAgain(scan);
        RefreshDrives();
        if (_scan == scan)
        {
            RefreshFolder(resort: false);
            await RefreshLargeFilesAsync();
        }
        return null;
    }

    /// <summary>Finds the drive's largest files. The whole tree is walked, so it happens off the UI thread.</summary>
    public async Task RefreshLargeFilesAsync()
    {
        if (_scan is not { } scan || !ShowingFiles)
        {
            LargeFiles.Clear();
            return;
        }
        var files = await Task.Run(() => LargestFiles.Under(scan.Tree, MostLargeFiles));
        if (_scan != scan)
            return;

        var largest = files.Count == 0 ? 1 : Math.Max(1, files[0].Bytes);
        LargeFiles.Clear();
        foreach (var file in files)
            LargeFiles.Add(new LargeFileRow(file.Name, file.Folder, UsageText.Memory(file.Bytes), file.Path, 100.0 * file.Bytes / largest));
        HasNoLargeFiles = LargeFiles.Count == 0 && !IsScanning;
    }

    // ---- Moving through the folders ----

    /// <summary>Goes into a folder, remembering where from so Back returns there.</summary>
    public void Open(FolderNode folder)
    {
        if (folder == Current)
            return;
        if (Current is not null)
            _back.Add(Current);
        _forward.Clear();
        Show(folder);
    }

    public void GoBack() => Step(_back, _forward);

    public void GoForward() => Step(_forward, _back);

    public void GoUp()
    {
        if (Current?.Parent is { } parent)
            Open(parent);
    }

    private void Step(List<FolderNode> from, List<FolderNode> to)
    {
        if (from.Count == 0 || Current is null)
            return;
        to.Add(Current);
        var folder = from[^1];
        from.RemoveAt(from.Count - 1);
        Show(folder);
    }

    private void ForgetHistory()
    {
        _back.Clear();
        _forward.Clear();
    }

    private void Show(FolderNode? folder)
    {
        Current = folder;
        Trail.Clear();
        var path = new List<FolderNode>();
        for (var node = folder; node is not null; node = node.Parent)
            path.Add(node);
        path.Reverse();
        foreach (var node in path)
            Trail.Add(new Crumb(node, node.Parent is null ? SelectedDrive?.Title ?? node.Name : node.Name));

        CanGoBack = _back.Count > 0;
        CanGoForward = _forward.Count > 0;
        CanGoUp = folder?.Parent is not null;

        // Another folder: nothing of the last one carries over.
        FolderRows.Clear();
        Tiles.Clear();
        RefreshFolder(resort: true);
    }

    public void SortFoldersBy(FolderSort sort)
    {
        FolderSortReversed = FolderSort == sort && !FolderSortReversed;
        FolderSort = sort;
        RefreshFolder(resort: true);
    }

    /// <param name="resort">
    /// True when the user asked for an order or the reading has finished. While a drive is being
    /// read the list follows the sizes anyway: it starts out in no useful order.
    /// </param>
    public void RefreshFolder(bool resort)
    {
        HasScan = _scan is not null && Current is not null;
        CanStartScan = !HasScan;
        IsScanning = _scan is { IsRunning: true };
        ShowScanStatus();
        if (!HasScan)
        {
            FolderRows.Clear();
            Tiles.Clear();
            MapChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var folder = Current!;
        var isRoot = folder.Parent is null;
        // The drive knows how much is in use; a reading without administrator rights can only count
        // what it was allowed to read, and the rest is shown as one line.
        var whole = isRoot && !IsScanning && _scan is { ReadEverything: false }
            ? Math.Max(folder.Bytes, SelectedDrive?.UsedBytes ?? 0)
            : folder.Bytes;

        var lines = new List<(string Key, FolderRowKind Kind, string Name, string? Path, FolderNode? Node, long Bytes, long Files, string Note, string Explanation)>();
        foreach (var child in folder.Children)
        {
            var note = child.WasDenied ? "Could not be scanned" : !child.IsComplete && IsScanning ? "Scanning" : "";
            lines.Add(($"folder:{child.Name}", FolderRowKind.Folder, child.Name, child.Path, child, child.Bytes, child.Files, note, ""));
        }

        long listedBytes = 0;
        foreach (var file in folder.LargestFiles)
        {
            listedBytes += file.Bytes;
            lines.Add(($"file:{file.Name}", FolderRowKind.File, file.Name, Path.Combine(folder.Path, file.Name), null, file.Bytes, 1, "", ""));
        }

        // The largest files are listed by name; the rest of the folder's own files are one entry.
        var otherBytes = folder.OwnBytes - listedBytes;
        var otherFiles = folder.OwnFiles - folder.LargestFiles.Count;
        if (otherBytes > 0 && otherFiles > 0)
        {
            lines.Add((
                "other", FolderRowKind.OtherFiles, $"{otherFiles:N0} other {(otherFiles == 1 ? "file" : "files")}", null, null,
                otherBytes, otherFiles, "",
                "Files directly in this folder that are too small or too many to list one by one"));
        }
        if (isRoot && !IsScanning && whole > folder.Bytes)
        {
            lines.Add((
                "unreadable", FolderRowKind.Unreadable, "Protected by Windows", null, null,
                whole - folder.Bytes, 0, "System files and folders Dashio may not scan",
                "The drive reports this much more in use than Dashio was allowed to scan"));
        }

        var largest = lines.Count == 0 ? 0 : lines.Max(l => l.Bytes);
        var existing = FolderRows.ToDictionary(r => r.Key);
        foreach (var line in lines)
        {
            if (!existing.Remove(line.Key, out var row))
                FolderRows.Add(row = new FolderEntryViewModel(line.Key, line.Kind, line.Path, line.Node, Columns));
            row.Update(line.Name, line.Bytes, line.Files, whole, largest, line.Note, line.Explanation);
        }
        foreach (var gone in existing.Values)
            FolderRows.Remove(gone);

        if (resort || (IsScanning && FolderSort == FolderSort.Size && !FolderSortReversed))
            PutInOrder();

        IsFolderEmpty = FolderRows.Count == 0 && !IsScanning;
        ShowTiles();
    }

    /// <summary>Moves lines to where the order wants them. Nothing is rebuilt, so the list glides instead of flashing.</summary>
    private void PutInOrder()
    {
        var names = StringComparer.CurrentCultureIgnoreCase;
        var sorted = FolderSort switch
        {
            FolderSort.Name => FolderRows.OrderBy(r => r.Name, names),
            FolderSort.Files => FolderRows.OrderByDescending(r => r.Files).ThenBy(r => r.Name, names),
            _ => FolderRows.OrderByDescending(r => r.Bytes).ThenBy(r => r.Name, names),
        };
        var inOrder = (FolderSortReversed ? sorted.Reverse() : sorted).ToList();
        for (var i = 0; i < inOrder.Count; i++)
        {
            var from = FolderRows.IndexOf(inOrder[i]);
            if (from != i)
                FolderRows.Move(from, i);
        }
    }

    /// <summary>The map shows the largest entries. Tiles stay the same objects while sizes change, so the map can glide.</summary>
    private void ShowTiles()
    {
        var wanted = FolderRows.Where(r => r.Bytes > 0).OrderByDescending(r => r.Bytes).Take(MostTiles).ToHashSet();
        foreach (var tile in Tiles.Where(t => !wanted.Contains(t)).ToList())
            Tiles.Remove(tile);
        foreach (var row in FolderRows.Where(r => wanted.Contains(r) && !Tiles.Contains(r)))
            Tiles.Add(row);
        MapChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShowScanStatus()
    {
        if (_scan is null || SelectedDrive is null)
        {
            ScanStatus = "";
            return;
        }

        var tree = _scan.Tree;
        if (_scan.IsRunning)
        {
            // What Windows will not let Dashio read is never counted, so the reading ends a little short of everything.
            var used = Math.Max(1, SelectedDrive.UsedBytes);
            ScanPercent = Math.Min(99, 100.0 * tree.Bytes / used);
            ScanPercentText = $"{ScanPercent:F0}%";
            ScanStatus = $"Scanning {SelectedDrive.Title}: {tree.Files:N0} files, {UsageText.Memory(tree.Bytes)} of {UsageText.Memory(used)} so far";
            return;
        }

        if (!IsAdminReading)
        {
            ScanPercent = 100;
            ScanPercentText = "100%";
        }
        var when = _scan.Finished?.LocalDateTime ?? DateTime.Now;
        var read = when.Date == DateTime.Today ? $"today at {when:t}"
            : when.Date == DateTime.Today.AddDays(-1) ? $"yesterday at {when:t}"
            : $"on {when:d MMMM}";
        var denied = tree.DeniedFolders;
        ScanStatus = $"{tree.Files:N0} files in {UsageText.Memory(tree.Bytes)}, scanned {read}" +
                     (_scan.ReadEverything ? " with administrator rights." : ".") +
                     (denied > 0 && !_scan.ReadEverything
                         ? $" {denied:N0} folders could not be scanned without administrator rights."
                         : "");
    }
}
