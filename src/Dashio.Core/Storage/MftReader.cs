using System.Buffers.Binary;
using System.Text;

namespace Dashio.Core.Storage;

/// <summary>Something that can be read like a disk volume: whole sectors at an offset.</summary>
public interface IVolume
{
    /// <summary>Fills the buffer from the offset. Both are multiples of the volume's sector size.</summary>
    void Read(long offset, byte[] buffer, int count);
}

/// <summary>
/// Builds the folder tree of an NTFS drive from its master file table: the one file in which the
/// drive records every file's name, folder and size. Reading it in one sweep takes seconds where
/// listing each folder takes minutes, and it sees every folder whatever its permissions.
/// It needs administrator rights, so it runs in the helper. It only reads.
/// </summary>
public static class MftReader
{
    private const uint FileSignature = 0x454C4946;      // "FILE"
    private const uint EndOfAttributes = 0xFFFFFFFF;
    private const uint StandardInformation = 0x10;
    private const uint AttributeList = 0x20;
    private const uint FileName = 0x30;
    private const uint Data = 0x80;
    private const long RecordNumberMask = 0xFFFFFFFFFFFF;
    private const int RootRecord = 5;
    private const int FixupStride = 512;
    private const byte DosNameOnly = 2;

    // A file kept in the cloud takes no room on the disk until it is opened.
    private const uint NotOnDisk = 0x00001000 | 0x00040000 | 0x00400000;

    private const byte InUse = 1;
    private const byte Folder = 2;
    private const byte Named = 4;
    private const byte Offline = 8;

    /// <summary>How much of the table is read at a time. A multiple of every cluster and record size.</summary>
    private const int ChunkBytes = 4 * 1024 * 1024;

    private readonly record struct Run(long FirstCluster, long Clusters);

    private sealed record Layout(int ClusterBytes, int RecordBytes, long TableOffset);

    /// <param name="progress">Called with how far the reading has got, 0 to 1.</param>
    public static FolderNode ReadTree(
        IVolume volume, string rootPath, Action<double>? progress = null, CancellationToken cancellation = default)
    {
        var layout = ReadLayout(volume);
        var (runs, tableBytes) = TableRuns(volume, layout);
        var records = checked((int)(tableBytes / layout.RecordBytes));

        var sizes = new long[records];
        var parents = new int[records];
        var flags = new byte[records];
        var names = new Dictionary<int, string>();

        var chunk = new byte[ChunkBytes];
        for (long at = 0; at < tableBytes; at += ChunkBytes)
        {
            cancellation.ThrowIfCancellationRequested();
            var count = (int)Math.Min(ChunkBytes, tableBytes - at);
            // A drive is read in whole clusters, even when the table ends part of the way into one.
            ReadTable(volume, layout, runs, at, chunk, RoundUp(count, layout.ClusterBytes));
            for (var offset = 0; offset + layout.RecordBytes <= count; offset += layout.RecordBytes)
            {
                ReadRecord(
                    chunk.AsSpan(offset, layout.RecordBytes), (int)((at + offset) / layout.RecordBytes),
                    sizes, parents, flags, names);
            }
            progress?.Invoke(Math.Min(1, (double)(at + count) / tableBytes));
        }

        return BuildTree(rootPath, sizes, parents, flags, names);
    }

    // ---- Where the table is ----

    private static Layout ReadLayout(IVolume volume)
    {
        var boot = new byte[4096];
        volume.Read(0, boot, boot.Length);
        if (Encoding.ASCII.GetString(boot, 3, 8) != "NTFS    ")
            throw new InvalidDataException("The drive is not formatted as NTFS.");

        var sectorBytes = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(0x0B));
        // Large clusters are written as a negative power of two.
        var sectorsPerCluster = (sbyte)boot[0x0D];
        var clusterBytes = sectorsPerCluster > 0 ? sectorBytes * sectorsPerCluster : sectorBytes << -sectorsPerCluster;
        var tableCluster = BinaryPrimitives.ReadInt64LittleEndian(boot.AsSpan(0x30));
        // So is the record size, which is usually smaller than a cluster.
        var recordSize = (sbyte)boot[0x40];
        var recordBytes = recordSize > 0 ? recordSize * clusterBytes : 1 << -recordSize;

        if (sectorBytes < 256 || clusterBytes < sectorBytes || recordBytes < 512 || recordBytes > 65536 ||
            ChunkBytes % recordBytes != 0 || ChunkBytes % clusterBytes != 0 || tableCluster <= 0)
            throw new InvalidDataException("The drive's layout is not one Dashio can read.");
        return new Layout(clusterBytes, recordBytes, tableCluster * clusterBytes);
    }

    /// <summary>The table is a file like any other and can be in pieces. Its own first record says where they are.</summary>
    private static (List<Run> Runs, long Bytes) TableRuns(IVolume volume, Layout layout)
    {
        var size = RoundUp(layout.RecordBytes, layout.ClusterBytes);
        var buffer = new byte[size];
        volume.Read(layout.TableOffset, buffer, size);
        var record = buffer.AsSpan(0, layout.RecordBytes);
        if (!Unfix(record))
            throw new InvalidDataException("The drive's file table could not be read.");

        var runs = new List<Run>();
        long bytes = 0;
        var further = new List<(long FirstCluster, long Record)>();
        foreach (var attribute in Attributes(record))
        {
            var header = record[attribute.Offset..];
            if (attribute.Type == Data && header[9] == 0 && header[8] != 0)
            {
                bytes = BinaryPrimitives.ReadInt64LittleEndian(header[0x30..]);
                runs.AddRange(Runs(header[..attribute.Length]));
            }
            else if (attribute.Type == AttributeList)
            {
                further.AddRange(ListedDataParts(volume, layout, header[..attribute.Length]));
            }
        }
        if (runs.Count == 0 || bytes <= 0)
            throw new InvalidDataException("The drive's file table could not be found.");

        // A table in very many pieces lists the rest of them in further records of itself.
        foreach (var part in further.Where(p => p.FirstCluster > 0).OrderBy(p => p.FirstCluster))
        {
            var extra = new byte[size];
            ReadTable(volume, layout, runs, part.Record * layout.RecordBytes / layout.ClusterBytes * layout.ClusterBytes, extra, size);
            var inCluster = (int)(part.Record * layout.RecordBytes % layout.ClusterBytes);
            var extraRecord = extra.AsSpan(inCluster, layout.RecordBytes);
            if (!Unfix(extraRecord))
                throw new InvalidDataException("The drive's file table could not be read.");
            foreach (var attribute in Attributes(extraRecord))
            {
                var header = extraRecord[attribute.Offset..];
                if (attribute.Type == Data && header[9] == 0 && header[8] != 0 &&
                    BinaryPrimitives.ReadInt64LittleEndian(header[0x10..]) == part.FirstCluster)
                    runs.AddRange(Runs(header[..attribute.Length]));
            }
        }
        return (runs, bytes);
    }

    /// <summary>The parts of the table's data that are described in other records, from its attribute list.</summary>
    private static List<(long FirstCluster, long Record)> ListedDataParts(IVolume volume, Layout layout, ReadOnlySpan<byte> attribute)
    {
        byte[] list;
        if (attribute[8] == 0)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(attribute[0x10..]);
            var offset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[0x14..]);
            list = attribute.Slice(offset, length).ToArray();
        }
        else
        {
            var length = checked((int)BinaryPrimitives.ReadInt64LittleEndian(attribute[0x30..]));
            var runs = Runs(attribute);
            list = new byte[RoundUp(length, layout.ClusterBytes)];
            var filled = 0;
            foreach (var run in runs)
            {
                var count = (int)Math.Min(run.Clusters * layout.ClusterBytes, list.Length - filled);
                if (count <= 0)
                    break;
                var piece = new byte[count];
                volume.Read(run.FirstCluster * layout.ClusterBytes, piece, count);
                piece.CopyTo(list, filled);
                filled += count;
            }
            list = list[..length];
        }

        var parts = new List<(long, long)>();
        for (var at = 0; at + 0x1A <= list.Length;)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(list.AsSpan(at));
            var entryLength = BinaryPrimitives.ReadUInt16LittleEndian(list.AsSpan(at + 4));
            if (entryLength < 0x1A)
                break;
            if (type == Data && list[at + 6] == 0)
            {
                parts.Add((
                    BinaryPrimitives.ReadInt64LittleEndian(list.AsSpan(at + 8)),
                    BinaryPrimitives.ReadInt64LittleEndian(list.AsSpan(at + 0x10)) & RecordNumberMask));
            }
            at += entryLength;
        }
        return parts;
    }

    /// <summary>Reads part of the table, which may lie in several places on the drive.</summary>
    private static void ReadTable(IVolume volume, Layout layout, List<Run> runs, long tableOffset, byte[] buffer, int count)
    {
        var filled = 0;
        long runStart = 0;
        foreach (var run in runs)
        {
            var runBytes = run.Clusters * layout.ClusterBytes;
            var from = tableOffset + filled;
            if (from < runStart + runBytes && filled < count)
            {
                var within = from - runStart;
                var take = (int)Math.Min(count - filled, runBytes - within);
                if (filled == 0)
                {
                    volume.Read(run.FirstCluster * layout.ClusterBytes + within, buffer, take);
                }
                else
                {
                    var piece = new byte[take];
                    volume.Read(run.FirstCluster * layout.ClusterBytes + within, piece, take);
                    piece.CopyTo(buffer, filled);
                }
                filled += take;
            }
            runStart += runBytes;
        }
        if (filled < count)
            Array.Clear(buffer, filled, count - filled);
    }

    /// <summary>Where a piece of a file is on the drive: runs of clusters, each placed relative to the last.</summary>
    private static List<Run> Runs(ReadOnlySpan<byte> attribute)
    {
        var runs = new List<Run>();
        var at = (int)BinaryPrimitives.ReadUInt16LittleEndian(attribute[0x20..]);
        long cluster = 0;
        while (at < attribute.Length && attribute[at] != 0)
        {
            var lengthBytes = attribute[at] & 0x0F;
            var offsetBytes = attribute[at] >> 4;
            at++;
            if (lengthBytes == 0 || at + lengthBytes + offsetBytes > attribute.Length)
                break;

            long clusters = 0;
            for (var i = 0; i < lengthBytes; i++)
                clusters |= (long)attribute[at + i] << (8 * i);
            at += lengthBytes;

            // No offset means a hole: clusters that were never written.
            if (offsetBytes > 0)
            {
                long offset = 0;
                for (var i = 0; i < offsetBytes; i++)
                    offset |= (long)attribute[at + i] << (8 * i);
                // The offset is signed, in as many bytes as it was written in.
                if ((attribute[at + offsetBytes - 1] & 0x80) != 0 && offsetBytes < 8)
                    offset |= -1L << (8 * offsetBytes);
                at += offsetBytes;
                cluster += offset;
                runs.Add(new Run(cluster, clusters));
            }
        }
        return runs;
    }

    // ---- One record of the table ----

    private readonly record struct Attribute(uint Type, int Offset, int Length);

    private static List<Attribute> Attributes(ReadOnlySpan<byte> record)
    {
        var found = new List<Attribute>();
        var at = (int)BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..]);
        while (at + 16 <= record.Length)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(record[at..]);
            if (type == EndOfAttributes)
                break;
            var length = BinaryPrimitives.ReadInt32LittleEndian(record[(at + 4)..]);
            if (length < 16 || at + length > record.Length)
                break;
            found.Add(new Attribute(type, at, length));
            at += length;
        }
        return found;
    }

    /// <summary>
    /// The last two bytes of every 512 are swapped for a check value when a record is written.
    /// This puts the real bytes back, and says no when the check fails, which means a torn write.
    /// </summary>
    private static bool Unfix(Span<byte> record)
    {
        if (record.Length < 0x30 || BinaryPrimitives.ReadUInt32LittleEndian(record) != FileSignature)
            return false;
        var arrayOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        var arrayCount = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
        if (arrayCount < 1 || arrayOffset + arrayCount * 2 > record.Length)
            return false;

        var check = record.Slice(arrayOffset, 2);
        for (var i = 1; i < arrayCount; i++)
        {
            var end = i * FixupStride - 2;
            if (end + 2 > record.Length)
                break;
            if (record[end] != check[0] || record[end + 1] != check[1])
                return false;
            record.Slice(arrayOffset + i * 2, 2).CopyTo(record.Slice(end, 2));
        }
        return true;
    }

    private static void ReadRecord(
        Span<byte> record, int number, long[] sizes, int[] parents, byte[] flags, Dictionary<int, string> names)
    {
        if (!Unfix(record))
            return;
        var recordFlags = BinaryPrimitives.ReadUInt16LittleEndian(record[0x16..]);
        if ((recordFlags & 1) == 0)
            return;

        // A file with many attributes spills into further records, which name the first one.
        var first = BinaryPrimitives.ReadInt64LittleEndian(record[0x20..]) & RecordNumberMask;
        var isFirst = first == 0;
        var owner = isFirst ? number : first;
        if (owner >= sizes.Length)
            return;

        long size = 0;
        var parent = -1;
        var offline = false;
        var hasList = false;
        var sawData = false;
        ReadOnlySpan<byte> name = default;
        foreach (var attribute in Attributes(record))
        {
            var header = record[attribute.Offset..(attribute.Offset + attribute.Length)];
            var isResident = header[8] == 0;
            switch (attribute.Type)
            {
                case StandardInformation when isResident:
                    var information = header[BinaryPrimitives.ReadUInt16LittleEndian(header[0x14..])..];
                    if (information.Length >= 0x24)
                        offline = (BinaryPrimitives.ReadUInt32LittleEndian(information[0x20..]) & NotOnDisk) != 0;
                    break;

                case AttributeList:
                    hasList = true;
                    break;

                case FileName when isResident && isFirst && name.IsEmpty:
                    var content = header[BinaryPrimitives.ReadUInt16LittleEndian(header[0x14..])..];
                    // A file can also carry a short DOS name; that one is not its name.
                    if (content.Length < 0x42 || content[0x41] == DosNameOnly)
                        break;
                    var length = content[0x40] * 2;
                    if (0x42 + length > content.Length)
                        break;
                    parent = (int)(BinaryPrimitives.ReadInt64LittleEndian(content) & RecordNumberMask);
                    name = content.Slice(0x42, length);
                    break;

                // The file's contents: the unnamed stream. Named streams beside it are not counted.
                case Data when header[9] == 0:
                    if (isResident)
                    {
                        size += BinaryPrimitives.ReadUInt32LittleEndian(header[0x10..]);
                        sawData = true;
                    }
                    else if (BinaryPrimitives.ReadInt64LittleEndian(header[0x10..]) == 0)
                    {
                        // Only the piece that starts at the beginning carries the size of the whole.
                        size += BinaryPrimitives.ReadInt64LittleEndian(header[0x30..]);
                        sawData = true;
                    }
                    break;
            }
        }

        if (size > 0)
            sizes[owner] += size;
        if (!isFirst)
            return;

        var isFolder = (recordFlags & 2) != 0;
        var hasName = !name.IsEmpty && parent >= 0 && parent < sizes.Length;
        flags[number] = (byte)(InUse | (isFolder ? Folder : 0) | (hasName ? Named : 0) | (offline ? Offline : 0));
        parents[number] = hasName ? parent : -1;
        // Names are kept only where they will be shown: folders, large files, and files whose size
        // is in another record and so not known yet.
        if (hasName && (isFolder || size >= DiskScanner.SmallestFileListed || (hasList && !sawData)))
            names[number] = Encoding.Unicode.GetString(name);
    }

    // ---- From records to folders ----

    private static FolderNode BuildTree(string rootPath, long[] sizes, int[] parents, byte[] flags, Dictionary<int, string> names)
    {
        var root = DiskScanner.CreateRoot(rootPath);
        var nodes = new Dictionary<int, FolderNode?> { [RootRecord] = root };

        FolderNode? NodeOf(int record)
        {
            // Walk up to a folder that already has a node, then come back down making the missing ones.
            var chain = new List<int>();
            var at = record;
            while (!nodes.ContainsKey(at))
            {
                var usable = at >= 0 && at < flags.Length && (flags[at] & (InUse | Folder | Named)) == (InUse | Folder | Named) &&
                             names.ContainsKey(at) && chain.Count < 1024 && !chain.Contains(at);
                if (!usable)
                {
                    // A folder whose way up is broken belongs to nothing that can be shown.
                    foreach (var lost in chain)
                        nodes[lost] = null;
                    nodes[at] = null;
                    return null;
                }
                chain.Add(at);
                at = parents[at];
            }

            var node = nodes[at];
            for (var i = chain.Count - 1; i >= 0; i--)
            {
                if (node is null)
                {
                    nodes[chain[i]] = null;
                    continue;
                }
                var name = names[chain[i]];
                node = new FolderNode(name, Path.Combine(node.Path, name), node);
                nodes[chain[i]] = node;
            }
            return node;
        }

        var ownBytes = new Dictionary<FolderNode, long>();
        var ownFiles = new Dictionary<FolderNode, long>();
        var largeFiles = new Dictionary<FolderNode, List<FileEntry>>();
        var children = new Dictionary<FolderNode, List<FolderNode>>();

        for (var record = 0; record < flags.Length; record++)
        {
            if ((flags[record] & (InUse | Named)) != (InUse | Named))
                continue;

            if ((flags[record] & Folder) != 0)
            {
                if (record != RootRecord && NodeOf(record) is { Parent: { } above } folder)
                {
                    if (!children.TryGetValue(above, out var siblings))
                        children[above] = siblings = [];
                    siblings.Add(folder);
                }
                continue;
            }

            if (NodeOf(parents[record]) is not { } home)
                continue;
            var size = (flags[record] & Offline) != 0 ? 0 : sizes[record];
            ownBytes[home] = ownBytes.GetValueOrDefault(home) + size;
            ownFiles[home] = ownFiles.GetValueOrDefault(home) + 1;
            if (size >= DiskScanner.SmallestFileListed && names.TryGetValue(record, out var fileName))
            {
                if (!largeFiles.TryGetValue(home, out var files))
                    largeFiles[home] = files = [];
                files.Add(new FileEntry(fileName, size));
            }
        }

        var all = nodes.Values.OfType<FolderNode>().ToList();
        foreach (var folder in all)
        {
            folder.SetChildren(children.TryGetValue(folder, out var list) ? list : []);
            folder.SetLargestFiles(largeFiles.TryGetValue(folder, out var files)
                ? files.OrderByDescending(f => f.Bytes).Take(DiskScanner.LargestFilesKept).ToList()
                : []);
            folder.AddOwn(ownBytes.GetValueOrDefault(folder), ownFiles.GetValueOrDefault(folder));
            folder.MarkComplete();
        }
        // As in the folder-by-folder scan, small folders forget their subfolders to save memory.
        foreach (var folder in all.Where(f => f.Parent is not null && f.Bytes < DiskScanner.ForgetBelow))
            folder.SetChildren([]);
        return root;
    }

    private static int RoundUp(int value, int multiple) => (value + multiple - 1) / multiple * multiple;
}
