using System.Collections.Concurrent;
using System.IO.Enumeration;

namespace Dashio.Core.Storage;

/// <summary>One of the largest files directly inside a folder.</summary>
public sealed record FileEntry(string Name, long Bytes);

/// <summary>
/// A folder with the size of everything under it. The scanner fills it in while others read it:
/// the totals only grow, and the lists are replaced whole, so a reader never sees one half-built.
/// </summary>
public sealed class FolderNode
{
    private long _bytes;
    private long _files;
    private long _ownBytes;
    private long _ownFiles;
    private int _deniedFolders;
    private int _unfinished = 1;
    private volatile IReadOnlyList<FolderNode> _children = [];
    private volatile IReadOnlyList<FileEntry> _largestFiles = [];
    private volatile bool _isComplete;
    private volatile bool _wasDenied;

    public FolderNode(string name, string path, FolderNode? parent)
    {
        Name = name;
        Path = path;
        Parent = parent;
    }

    public string Name { get; }
    public string Path { get; }
    public FolderNode? Parent { get; }

    /// <summary>The size of every file under this folder, as far as the scan has got.</summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    public long Files => Interlocked.Read(ref _files);

    /// <summary>The size of the files directly in this folder, not in its subfolders.</summary>
    public long OwnBytes => Interlocked.Read(ref _ownBytes);

    /// <summary>How many files are directly in this folder.</summary>
    public long OwnFiles => Interlocked.Read(ref _ownFiles);

    public IReadOnlyList<FolderNode> Children => _children;
    public IReadOnlyList<FileEntry> LargestFiles => _largestFiles;

    /// <summary>Everything under this folder has been read.</summary>
    public bool IsComplete => _isComplete;

    /// <summary>Windows refused to list this folder, so its size is not known.</summary>
    public bool WasDenied => _wasDenied;

    /// <summary>How many folders here and below Windows refused to list.</summary>
    public int DeniedFolders => Volatile.Read(ref _deniedFolders);

    /// <summary>The folder itself counts as one piece of unfinished work, and so does each subfolder.</summary>
    internal void Expect(int subfolders) => Interlocked.Add(ref _unfinished, subfolders);

    /// <summary>One piece of work is done. True when that was the last.</summary>
    internal bool FinishOne() => Interlocked.Decrement(ref _unfinished) == 0;

    /// <summary>Puts back the figures of a reading that was kept on disk.</summary>
    internal void Restore(long bytes, long files, long ownBytes, long ownFiles, int deniedFolders, bool wasDenied)
    {
        _bytes = bytes;
        _files = files;
        _ownBytes = ownBytes;
        _ownFiles = ownFiles;
        _deniedFolders = deniedFolders;
        _wasDenied = wasDenied;
        _isComplete = true;
    }

    internal void SetChildren(IReadOnlyList<FolderNode> children) => _children = children;
    internal void SetLargestFiles(IReadOnlyList<FileEntry> files) => _largestFiles = files;
    internal void MarkComplete() => _isComplete = true;
    internal void MarkDenied()
    {
        _wasDenied = true;
        for (var node = this; node is not null; node = node.Parent)
            Interlocked.Increment(ref node._deniedFolders);
    }

    /// <summary>
    /// Takes a subfolder or a listed file out again after it was deleted, with its size, here and
    /// in every folder above. Only for a reading that is finished: while one runs, totals only grow.
    /// </summary>
    /// <returns>False when nothing of that name is listed here.</returns>
    public bool Forget(string name)
    {
        long bytes, files;
        if (_children.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } child)
        {
            (bytes, files) = (child.Bytes, child.Files);
            _children = _children.Where(c => c != child).ToList();
        }
        else if (_largestFiles.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } file)
        {
            (bytes, files) = (file.Bytes, 1);
            _largestFiles = _largestFiles.Where(f => f != file).ToList();
            Interlocked.Add(ref _ownBytes, -bytes);
            Interlocked.Add(ref _ownFiles, -1);
        }
        else
        {
            return false;
        }

        for (var node = this; node is not null; node = node.Parent)
        {
            Interlocked.Add(ref node._bytes, -bytes);
            Interlocked.Add(ref node._files, -files);
        }
        return true;
    }

    /// <summary>The folder with this path under this one, or null.</summary>
    public FolderNode? Find(string path)
    {
        var node = this;
        while (!node.Path.TrimEnd('\\').Equals(path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            var next = node.Children.FirstOrDefault(c =>
                path.Equals(c.Path, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(c.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
            if (next is null)
                return null;
            node = next;
        }
        return node;
    }

    /// <summary>Adds this folder's own files to it and to every folder above it.</summary>
    internal void AddOwn(long bytes, long files)
    {
        Interlocked.Add(ref _ownBytes, bytes);
        Interlocked.Add(ref _ownFiles, files);
        for (var node = this; node is not null; node = node.Parent)
        {
            Interlocked.Add(ref node._bytes, bytes);
            Interlocked.Add(ref node._files, files);
        }
    }
}

/// <summary>Reads the size of every folder under a root, without changing anything.</summary>
public static class DiskScanner
{
    /// <summary>How many of a folder's own files are remembered by name.</summary>
    public const int LargestFilesKept = 100;

    /// <summary>Files smaller than this are counted but not listed by name.</summary>
    public const long SmallestFileListed = 1024 * 1024;

    /// <summary>
    /// A finished folder smaller than this forgets its subfolders. A drive has hundreds of
    /// thousands of tiny folders nobody will open here, and keeping them all costs memory.
    /// </summary>
    public const long ForgetBelow = 1024 * 1024;

    // A file kept in the cloud takes no room on the disk until it is opened.
    private const FileAttributes NotOnDisk =
        FileAttributes.Offline | (FileAttributes)0x00040000 | (FileAttributes)0x00400000;

    private readonly record struct Entry(string Name, bool IsFolder, long Bytes, FileAttributes Attributes);

    private static readonly EnumerationOptions Options = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    public static FolderNode CreateRoot(string path)
    {
        // A drive root has no name of its own, so it goes by its path.
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return new FolderNode(name.Length > 0 ? name : path, path, null);
    }

    /// <summary>
    /// Fills in the tree under the root. Returns when everything has been read or the scan is cancelled.
    /// Several folders are read at once, which is what makes a whole drive take minutes rather than a quarter of an hour.
    /// </summary>
    /// <param name="threads">How many folders to read at once. Zero picks a number from the processor.</param>
    public static void Scan(FolderNode root, CancellationToken cancellation = default, int threads = 0)
    {
        // Most of the time goes on waiting for the disk, not on the processor, so more threads than
        // half the cores pays off: a drive that is not in Windows' cache is read several times faster.
        if (threads <= 0)
            threads = Math.Clamp(Environment.ProcessorCount, 4, 16);

        // Last in, first out: the scan goes deep before it goes wide, so few folders wait at any time.
        using var work = new BlockingCollection<FolderNode>(new ConcurrentStack<FolderNode>());
        work.Add(root);

        void Work()
        {
            foreach (var folder in work.GetConsumingEnumerable())
            {
                if (cancellation.IsCancellationRequested)
                {
                    work.CompleteAdding();
                    return;
                }

                var subfolders = ReadFolder(folder);
                folder.Expect(subfolders.Count);
                try
                {
                    foreach (var subfolder in subfolders)
                        work.Add(subfolder);
                }
                catch (InvalidOperationException)
                {
                    return;     // Cancelled by another thread while this one was adding.
                }
                Finish(folder, work);
            }
        }

        var others = Enumerable.Range(1, threads - 1)
            .Select(_ => new Thread(Work) { IsBackground = true, Priority = Thread.CurrentThread.Priority, Name = "Dashio disk scan" })
            .ToList();
        foreach (var thread in others)
            thread.Start();
        Work();
        foreach (var thread in others)
            thread.Join();
    }

    /// <summary>Marks the folder done when nothing under it is left, then tells the folder above.</summary>
    private static void Finish(FolderNode folder, BlockingCollection<FolderNode> work)
    {
        for (var node = folder; node is not null && node.FinishOne(); node = node.Parent)
        {
            node.MarkComplete();
            if (node.Parent is null)
                work.CompleteAdding();
            else if (node.Bytes < ForgetBelow)
                node.SetChildren([]);
        }
    }

    /// <summary>Counts the files directly in the folder and returns its subfolders.</summary>
    private static List<FolderNode> ReadFolder(FolderNode node)
    {
        long bytes = 0, files = 0;
        var largest = new List<FileEntry>();
        var children = new List<FolderNode>();
        try
        {
            var entries = new FileSystemEnumerable<Entry>(
                node.Path,
                (ref FileSystemEntry e) => new Entry(e.FileName.ToString(), e.IsDirectory, e.Length, e.Attributes),
                Options);
            foreach (var entry in entries)
            {
                if (entry.IsFolder)
                {
                    // A link to another folder would count that folder twice, or loop for ever.
                    if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
                        children.Add(new FolderNode(entry.Name, Path.Combine(node.Path, entry.Name), node));
                    continue;
                }

                files++;
                if ((entry.Attributes & NotOnDisk) != 0)
                    continue;
                bytes += entry.Bytes;
                if (entry.Bytes >= SmallestFileListed)
                    largest.Add(new FileEntry(entry.Name, entry.Bytes));
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            // What was read before the failure still counts.
            node.MarkDenied();
        }

        node.SetLargestFiles(largest.OrderByDescending(f => f.Bytes).Take(LargestFilesKept).ToList());
        node.SetChildren(children);
        node.AddOwn(bytes, files);
        return children;
    }
}
