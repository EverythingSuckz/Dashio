using System.IO.Compression;
using System.Text;

namespace Dashio.Core.Storage;

/// <summary>A reading of a drive that was kept: the tree, when it was read, and how thoroughly.</summary>
/// <param name="ReadEverything">Read with administrator rights, so nothing was left out as protected.</param>
public sealed record SavedReading(FolderNode Tree, DateTimeOffset ReadAt, bool ReadEverything);

/// <summary>
/// Keeps a reading of a drive on disk, so it need not be read again every time Dashio opens.
/// The file lists folder names and sizes; it stays in Dashio's own data folder.
/// </summary>
public static class FolderTreeFile
{
    private const uint Magic = 0x54485344;  // "DSHT"
    private const int Version = 1;

    /// <summary>A tree deeper than this is not something a drive produces; the file is taken to be damaged.</summary>
    private const int DeepestTree = 512;

    public static void Save(string path, FolderNode tree, DateTimeOffset readAt, bool readEverything)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Written beside the target and moved into place, so a half-written file is never read back.
        var temporary = path + ".writing";
        using (var file = File.Create(temporary))
        using (var packed = new GZipStream(file, CompressionLevel.Fastest))
        using (var writer = new BinaryWriter(packed, Encoding.UTF8))
        {
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(readAt.UtcTicks);
            writer.Write(readEverything);
            writer.Write(tree.Path);
            Write(writer, tree);
        }
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>The kept reading, or null when there is none or it cannot be read.</summary>
    public static SavedReading? Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            using var file = File.OpenRead(path);
            using var packed = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new BinaryReader(packed, Encoding.UTF8);
            if (reader.ReadUInt32() != Magic || reader.ReadInt32() != Version)
                return null;
            var readAt = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
            var readEverything = reader.ReadBoolean();
            var rootPath = reader.ReadString();
            var tree = Read(reader, rootPath, null, 0);
            return new SavedReading(tree, readAt, readEverything);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static void Write(BinaryWriter writer, FolderNode node)
    {
        writer.Write(node.Name);
        writer.Write(node.Bytes);
        writer.Write(node.Files);
        writer.Write(node.OwnBytes);
        writer.Write(node.OwnFiles);
        writer.Write(node.DeniedFolders);
        writer.Write(node.WasDenied);

        var files = node.LargestFiles;
        writer.Write(files.Count);
        foreach (var file in files)
        {
            writer.Write(file.Name);
            writer.Write(file.Bytes);
        }

        var children = node.Children;
        writer.Write(children.Count);
        foreach (var child in children)
            Write(writer, child);
    }

    private static FolderNode Read(BinaryReader reader, string? rootPath, FolderNode? parent, int depth)
    {
        if (depth > DeepestTree)
            throw new InvalidDataException("The kept reading is damaged.");

        var name = reader.ReadString();
        var node = new FolderNode(name, parent is null ? rootPath! : Path.Combine(parent.Path, name), parent);
        node.Restore(
            reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt32(), reader.ReadBoolean());

        var files = new FileEntry[Count(reader)];
        for (var i = 0; i < files.Length; i++)
            files[i] = new FileEntry(reader.ReadString(), reader.ReadInt64());
        node.SetLargestFiles(files);

        var children = new FolderNode[Count(reader)];
        for (var i = 0; i < children.Length; i++)
            children[i] = Read(reader, null, node, depth + 1);
        node.SetChildren(children);
        return node;
    }

    private static int Count(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        return count is >= 0 and < 10_000_000 ? count : throw new InvalidDataException("The kept reading is damaged.");
    }
}

/// <summary>A large file somewhere on the drive, with the folder it is in.</summary>
public sealed record LargeFile(string Name, string Folder, long Bytes)
{
    public string Path => System.IO.Path.Combine(Folder, Name);
}

public static class LargestFiles
{
    /// <summary>The largest files anywhere under the folder, largest first.</summary>
    public static IReadOnlyList<LargeFile> Under(FolderNode root, int count)
    {
        // The smallest of the best so far sits on top, ready to be pushed out by a larger one.
        var best = new PriorityQueue<LargeFile, long>();
        var pending = new Stack<FolderNode>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            foreach (var file in folder.LargestFiles)
            {
                if (best.Count < count)
                    best.Enqueue(new LargeFile(file.Name, folder.Path, file.Bytes), file.Bytes);
                else if (best.TryPeek(out _, out var smallest) && file.Bytes > smallest)
                    best.EnqueueDequeue(new LargeFile(file.Name, folder.Path, file.Bytes), file.Bytes);
            }
            foreach (var child in folder.Children)
                pending.Push(child);
        }

        var result = new List<LargeFile>(best.Count);
        while (best.TryDequeue(out var file, out _))
            result.Add(file);
        result.Reverse();
        return result;
    }
}
