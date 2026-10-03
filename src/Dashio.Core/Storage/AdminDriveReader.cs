namespace Dashio.Core.Storage;

/// <summary>
/// Reads a whole drive with administrator rights, for the helper. It takes a drive letter and
/// nothing else, checks that the letter is one of this PC's fixed drives, and only reads.
/// </summary>
public static class AdminDriveReader
{
    /// <summary>The drive root for a letter the request gave, or null when it is not a fixed drive of this PC.</summary>
    public static string? RootFor(string? argument)
    {
        if (argument is not { Length: 1 } || !char.IsAsciiLetter(argument[0]))
            return null;
        var root = $@"{char.ToUpperInvariant(argument[0])}:\";
        return DriveInfo.GetDrives().Any(d => d.DriveType == DriveType.Fixed && d.IsReady &&
                                              d.Name.Equals(root, StringComparison.OrdinalIgnoreCase))
            ? root
            : null;
    }

    /// <param name="progress">Called with how far the reading has got, 0 to 1.</param>
    /// <returns>The tree, and whether it came from the file table (fast) or from listing every folder.</returns>
    public static (FolderNode Tree, bool FromTable) Read(string root, Action<double> progress)
    {
        var drive = new DriveInfo(root);
        if (drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var volume = new NtfsVolume(root[0]);
                return (MftReader.ReadTree(volume, root, progress), true);
            }
            catch (Exception)
            {
                // A table Dashio cannot make sense of is no reason to give up: the folders can
                // still be listed one by one, which is slower but asks nothing of the layout.
            }
        }

        // With this an administrator may list folders whatever their permissions say.
        BackupPrivilege.TryEnable();
        var tree = DiskScanner.CreateRoot(root);
        var used = Math.Max(1, drive.TotalSize - drive.AvailableFreeSpace);
        using var done = new ManualResetEventSlim();
        var scan = new Thread(() =>
        {
            DiskScanner.Scan(tree);
            done.Set();
        })
        { IsBackground = true };
        scan.Start();
        while (!done.Wait(TimeSpan.FromMilliseconds(400)))
            progress(Math.Min(0.99, (double)tree.Bytes / used));
        progress(1);
        return (tree, false);
    }
}
