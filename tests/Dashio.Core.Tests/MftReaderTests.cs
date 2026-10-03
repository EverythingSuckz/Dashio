using System.Buffers.Binary;
using System.Text;
using Dashio.Core.Storage;

namespace Dashio.Core.Tests;

/// <summary>
/// A hand-built NTFS drive: a boot sector and a file table, laid out as the real thing is.
/// It behaves like a drive with 4096-byte sectors, the strictest kind, and refuses any read
/// that is not in whole sectors.
/// </summary>
internal sealed class FakeNtfsDrive : IVolume
{
    public const int ClusterBytes = 4096;
    public const int RecordBytes = 1024;
    private const int RecordsPerCluster = ClusterBytes / RecordBytes;

    private readonly byte[] _image = new byte[64 * ClusterBytes];
    private readonly SortedDictionary<int, byte[]> _records = [];

    /// <summary>Where each piece of the table is: the cluster it starts at and how many clusters it has.</summary>
    public List<(int Cluster, int Clusters)> TablePieces { get; } = [(4, 8)];

    public int ReadsOutOfStep { get; private set; }

    public void Read(long offset, byte[] buffer, int count)
    {
        if (offset % ClusterBytes != 0 || count % ClusterBytes != 0)
            ReadsOutOfStep++;
        Array.Copy(_image, offset, buffer, 0, count);
    }

    // ---- Attributes ----

    public static byte[] Information(uint fileAttributes = 0)
    {
        var content = new byte[0x30];
        BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(0x20), fileAttributes);
        return Resident(0x10, content);
    }

    public static byte[] Name(string name, int parent, byte nameSpace = 1)
    {
        var text = Encoding.Unicode.GetBytes(name);
        var content = new byte[0x42 + text.Length];
        BinaryPrimitives.WriteInt64LittleEndian(content, (uint)parent | (1L << 48));
        content[0x40] = (byte)name.Length;
        content[0x41] = nameSpace;
        text.CopyTo(content, 0x42);
        return Resident(0x30, content);
    }

    public static byte[] SmallData(int bytes) => Resident(0x80, new byte[bytes]);

    /// <summary>A file's contents kept outside the record: only where it is and how large it is.</summary>
    public static byte[] LargeData(long bytes, long firstCluster = 0, params byte[] runs)
    {
        var attribute = new byte[Pad(0x40 + Math.Max(runs.Length, 1) + 1)];
        BinaryPrimitives.WriteUInt32LittleEndian(attribute, 0x80);
        BinaryPrimitives.WriteInt32LittleEndian(attribute.AsSpan(4), attribute.Length);
        attribute[8] = 1;
        BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(0x10), firstCluster);
        BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(0x20), 0x40);
        BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(0x30), bytes);
        runs.CopyTo(attribute, 0x40);
        return attribute;
    }

    /// <summary>The marker that says some of the file's attributes are in other records.</summary>
    public static byte[] ListOfAttributes() => Resident(0x20, new byte[0x20]);

    private static byte[] Resident(uint type, byte[] content)
    {
        var attribute = new byte[Pad(0x18 + content.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(attribute, type);
        BinaryPrimitives.WriteInt32LittleEndian(attribute.AsSpan(4), attribute.Length);
        BinaryPrimitives.WriteInt32LittleEndian(attribute.AsSpan(0x10), content.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(0x14), 0x18);
        content.CopyTo(attribute, 0x18);
        return attribute;
    }

    private static int Pad(int length) => (length + 7) / 8 * 8;

    // ---- Records ----

    public void Add(int number, bool inUse, bool folder, int partOf, params byte[][] attributes)
    {
        var record = new byte[RecordBytes];
        Encoding.ASCII.GetBytes("FILE").CopyTo(record, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 0x30);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x14), 0x38);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x16), (ushort)((inUse ? 1 : 0) | (folder ? 2 : 0)));
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(0x20), partOf == 0 ? 0 : (uint)partOf | (1L << 48));

        var at = 0x38;
        foreach (var attribute in attributes)
        {
            attribute.CopyTo(record, at);
            at += attribute.Length;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(at), 0xFFFFFFFF);

        // The check value takes the place of the last two bytes of each 512; the real bytes go in the header.
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x30), 0x0007);
        for (var i = 1; i <= 2; i++)
        {
            record.AsSpan(i * 512 - 2, 2).CopyTo(record.AsSpan(0x30 + i * 2));
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(i * 512 - 2), 0x0007);
        }
        _records[number] = record;
    }

    public void File(int number, string name, int parent, params byte[][] more) =>
        Add(number, true, false, 0, [Information(), Name(name, parent), .. more]);

    public void Folder(int number, string name, int parent) => Add(number, true, true, 0, Information(), Name(name, parent));

    /// <summary>Damages a record the way a write cut short by a power failure would.</summary>
    public void Tear(int number) => _records[number][510] ^= 0xFF;

    /// <summary>Writes the boot sector and the table. Call after the last record has been added.</summary>
    public FakeNtfsDrive Build(string format = "NTFS    ")
    {
        var tableRecords = TablePieces.Sum(p => p.Clusters) * RecordsPerCluster;
        long tableBytes = tableRecords * RecordBytes;

        // The table describes itself in its first record, and the root folder is record 5.
        var runs = new List<byte>();
        var previous = 0;
        foreach (var (cluster, clusters) in TablePieces)
        {
            runs.AddRange([0x11, (byte)clusters, unchecked((byte)(sbyte)(cluster - previous))]);
            previous = cluster;
        }
        Add(0, true, false, 0, Information(), Name("$MFT", 5), LargeData(tableBytes, 0, [.. runs]));
        if (!_records.ContainsKey(5))
            Add(5, true, true, 0, Information(), Name(".", 5));

        Encoding.ASCII.GetBytes(format).CopyTo(_image, 3);
        BinaryPrimitives.WriteUInt16LittleEndian(_image.AsSpan(0x0B), 512);
        _image[0x0D] = ClusterBytes / 512;
        BinaryPrimitives.WriteInt64LittleEndian(_image.AsSpan(0x30), TablePieces[0].Cluster);
        _image[0x40] = unchecked((byte)(sbyte)-10);     // Records of 2 to the power 10 bytes.

        foreach (var (number, record) in _records)
        {
            // The record's place in the table, then the place of that part of the table on the drive.
            var within = number;
            foreach (var (cluster, clusters) in TablePieces)
            {
                var held = clusters * RecordsPerCluster;
                if (within < held)
                {
                    record.CopyTo(_image, cluster * ClusterBytes + within * RecordBytes);
                    break;
                }
                within -= held;
            }
        }
        return this;
    }

    public long TableBytes => TablePieces.Sum(p => p.Clusters) * (long)ClusterBytes;
}

public class MftReaderTests
{
    private const int Megabyte = 1024 * 1024;

    private static FakeNtfsDrive Drive()
    {
        var drive = new FakeNtfsDrive();
        drive.Folder(6, "Tools", 5);
        drive.File(7, "a.bin", 6, FakeNtfsDrive.LargeData(3 * Megabyte));
        drive.File(8, "small.txt", 5, FakeNtfsDrive.SmallData(100));
        return drive;
    }

    [Fact]
    public void Folders_and_sizes_come_out_of_the_file_table()
    {
        var drive = Drive().Build();

        var root = MftReader.ReadTree(drive, @"X:\");

        Assert.True(root.IsComplete);
        Assert.Equal(@"X:\", root.Path);
        var tools = Assert.Single(root.Children);
        Assert.Equal("Tools", tools.Name);
        Assert.Equal(@"X:\Tools", tools.Path);
        Assert.Equal(3L * Megabyte, tools.Bytes);
        Assert.Equal(1, tools.Files);
        Assert.Equal("a.bin", Assert.Single(tools.LargestFiles).Name);
        // The table itself is a file in the root folder and takes room like any other.
        Assert.Equal(3L * Megabyte + 100 + drive.TableBytes, root.Bytes);
        Assert.Equal(3, root.Files);
        Assert.Equal(0, drive.ReadsOutOfStep);
    }

    [Fact]
    public void A_deleted_file_is_not_counted()
    {
        var drive = Drive();
        drive.Add(9, inUse: false, folder: false, partOf: 0,
            FakeNtfsDrive.Information(), FakeNtfsDrive.Name("gone.iso", 6), FakeNtfsDrive.LargeData(900 * Megabyte));

        var tools = Assert.Single(MftReader.ReadTree(drive.Build(), @"X:\").Children);

        Assert.Equal(3L * Megabyte, tools.Bytes);
    }

    [Fact]
    public void A_file_goes_by_its_long_name_not_its_short_one()
    {
        var drive = Drive();
        drive.Add(9, true, false, 0,
            FakeNtfsDrive.Information(), FakeNtfsDrive.Name("LONGFI~1.BIN", 6, nameSpace: 2),
            FakeNtfsDrive.Name("Long file name.bin", 6), FakeNtfsDrive.LargeData(2 * Megabyte));

        var tools = Assert.Single(MftReader.ReadTree(drive.Build(), @"X:\").Children);

        Assert.Equal(["a.bin", "Long file name.bin"], tools.LargestFiles.Select(f => f.Name));
        Assert.Equal(2, tools.Files);
    }

    [Fact]
    public void A_size_kept_in_a_further_record_counts_for_the_file_it_belongs_to()
    {
        var drive = Drive();
        drive.File(9, "huge.vhdx", 6, FakeNtfsDrive.ListOfAttributes());
        drive.Add(10, true, false, partOf: 9, FakeNtfsDrive.LargeData(5 * Megabyte));
        // A later piece of the same file says nothing about its size.
        drive.Add(11, true, false, partOf: 9, FakeNtfsDrive.LargeData(5 * Megabyte, firstCluster: 400));

        var tools = Assert.Single(MftReader.ReadTree(drive.Build(), @"X:\").Children);

        Assert.Equal(8L * Megabyte, tools.Bytes);
        Assert.Equal(2, tools.Files);
        Assert.Equal("huge.vhdx", tools.LargestFiles[0].Name);
        Assert.Equal(5L * Megabyte, tools.LargestFiles[0].Bytes);
    }

    [Fact]
    public void A_file_kept_in_the_cloud_is_counted_but_takes_no_room()
    {
        var drive = Drive();
        drive.Add(9, true, false, 0,
            FakeNtfsDrive.Information(fileAttributes: 0x00400000), FakeNtfsDrive.Name("holiday.mp4", 6),
            FakeNtfsDrive.LargeData(700 * Megabyte));

        var tools = Assert.Single(MftReader.ReadTree(drive.Build(), @"X:\").Children);

        Assert.Equal(3L * Megabyte, tools.Bytes);
        Assert.Equal(2, tools.Files);
    }

    [Fact]
    public void A_table_in_pieces_is_followed_from_piece_to_piece()
    {
        var drive = Drive();
        // The second piece lies before the first on the drive, so its place is a step backwards.
        drive.TablePieces.Clear();
        drive.TablePieces.AddRange([(20, 2), (10, 6)]);
        drive.Folder(12, "Deep", 6);
        drive.File(13, "b.bin", 12, FakeNtfsDrive.LargeData(4 * Megabyte));

        var root = MftReader.ReadTree(drive.Build(), @"X:\");

        var tools = Assert.Single(root.Children);
        Assert.Equal(7L * Megabyte, tools.Bytes);
        Assert.Equal(4L * Megabyte, Assert.Single(tools.Children).Bytes);
        Assert.Equal(0, drive.ReadsOutOfStep);
    }

    [Fact]
    public void A_torn_record_is_left_out_and_the_rest_still_read()
    {
        var drive = Drive();
        drive.Tear(7);

        var root = MftReader.ReadTree(drive.Build(), @"X:\");

        Assert.Equal(0, Assert.Single(root.Children).Bytes);
        Assert.Equal(100 + drive.TableBytes, root.Bytes);
    }

    [Fact]
    public void A_folder_with_no_way_up_to_the_root_is_left_out()
    {
        var drive = Drive();
        drive.Folder(9, "Orphan", 40);
        drive.File(10, "lost.bin", 9, FakeNtfsDrive.LargeData(6 * Megabyte));

        var root = MftReader.ReadTree(drive.Build(), @"X:\");

        Assert.Equal("Tools", Assert.Single(root.Children).Name);
        Assert.Equal(3L * Megabyte + 100 + drive.TableBytes, root.Bytes);
    }

    [Fact]
    public void A_drive_that_is_not_NTFS_is_refused()
    {
        var drive = Drive().Build("EXFAT   ");

        Assert.Throws<InvalidDataException>(() => MftReader.ReadTree(drive, @"X:\"));
    }

    [Fact]
    public void Progress_reaches_the_end()
    {
        var steps = new List<double>();

        MftReader.ReadTree(Drive().Build(), @"X:\", steps.Add);

        Assert.Equal(1, steps[^1]);
    }
}

public class FolderTreeFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"dashio-tree-{Guid.NewGuid():N}");

    public FolderTreeFileTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    private FolderNode Scanned()
    {
        var data = Path.Combine(_folder, "data");
        Directory.CreateDirectory(Path.Combine(data, "Tools", "Deep"));
        File.WriteAllBytes(Path.Combine(data, "Tools", "a.bin"), new byte[3 * 1024 * 1024]);
        File.WriteAllBytes(Path.Combine(data, "Tools", "Deep", "b.bin"), new byte[2 * 1024 * 1024]);
        File.WriteAllBytes(Path.Combine(data, "c.txt"), new byte[100]);
        var root = DiskScanner.CreateRoot(data);
        DiskScanner.Scan(root);
        return root;
    }

    [Fact]
    public void A_kept_reading_comes_back_as_it_was()
    {
        var tree = Scanned();
        var path = Path.Combine(_folder, "kept", "drive.bin");
        var readAt = new DateTimeOffset(2026, 3, 4, 10, 30, 0, TimeSpan.FromHours(2));

        FolderTreeFile.Save(path, tree, readAt, readEverything: true);
        var kept = FolderTreeFile.Load(path);

        Assert.NotNull(kept);
        Assert.Equal(readAt, kept!.ReadAt);
        Assert.True(kept.ReadEverything);
        Assert.Equal(tree.Path, kept.Tree.Path);
        Assert.Equal(tree.Bytes, kept.Tree.Bytes);
        Assert.Equal(tree.Files, kept.Tree.Files);
        Assert.Equal(tree.OwnFiles, kept.Tree.OwnFiles);
        Assert.True(kept.Tree.IsComplete);
        var tools = Assert.Single(kept.Tree.Children);
        Assert.Equal(Path.Combine(tree.Path, "Tools"), tools.Path);
        Assert.Same(kept.Tree, tools.Parent);
        Assert.Equal("a.bin", Assert.Single(tools.LargestFiles).Name);
        Assert.Equal(2L * 1024 * 1024, Assert.Single(tools.Children).Bytes);
    }

    [Fact]
    public void A_missing_or_damaged_file_gives_nothing_rather_than_failing()
    {
        var path = Path.Combine(_folder, "broken.bin");
        Assert.Null(FolderTreeFile.Load(path));

        File.WriteAllText(path, "this is not a reading");
        Assert.Null(FolderTreeFile.Load(path));
    }

    [Fact]
    public void The_largest_files_are_found_wherever_they_are()
    {
        var files = LargestFiles.Under(Scanned(), 1);

        var largest = Assert.Single(files);
        Assert.Equal("a.bin", largest.Name);
        Assert.EndsWith("Tools", largest.Folder);

        Assert.Equal(["a.bin", "b.bin"], LargestFiles.Under(Scanned(), 10).Select(f => f.Name));
    }
}
