using Dashio.Core.Storage;

namespace Dashio.Core.Tests;

public class DiskScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"dashio-scan-{Guid.NewGuid():N}");

    public DiskScannerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private void Write(string relativePath, int bytes)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    private FolderNode Scan()
    {
        var root = DiskScanner.CreateRoot(_root);
        DiskScanner.Scan(root);
        return root;
    }

    private const int Megabyte = 1024 * 1024;

    [Fact]
    public void A_folder_is_as_large_as_everything_under_it()
    {
        Write(@"Tools\a.bin", 3 * Megabyte);
        Write(@"Tools\Deep\b.bin", 2 * Megabyte);
        Write("c.bin", 100);

        var root = Scan();

        Assert.True(root.IsComplete);
        Assert.Equal(5L * Megabyte + 100, root.Bytes);
        Assert.Equal(3, root.Files);
        Assert.Equal(100, root.OwnBytes);
        Assert.Equal(1, root.OwnFiles);
        var tools = Assert.Single(root.Children);
        Assert.Equal("Tools", tools.Name);
        Assert.Equal(5L * Megabyte, tools.Bytes);
        Assert.Equal(3L * Megabyte, tools.OwnBytes);
        Assert.Equal(2L * Megabyte, Assert.Single(tools.Children).Bytes);
    }

    [Fact]
    public void The_largest_files_of_a_folder_are_listed_largest_first()
    {
        Write("small.txt", 10);
        Write("big.bin", 4 * Megabyte);
        Write("bigger.bin", 6 * Megabyte);

        var files = Scan().LargestFiles;

        Assert.Equal(["bigger.bin", "big.bin"], files.Select(f => f.Name));
        Assert.Equal(6L * Megabyte, files[0].Bytes);
    }

    [Fact]
    public void A_small_folder_forgets_its_subfolders_but_keeps_its_size()
    {
        Write(@"Tiny\One\a.txt", 10);
        Write(@"Tiny\Two\b.txt", 20);

        var tiny = Assert.Single(Scan().Children);

        Assert.Equal(30, tiny.Bytes);
        Assert.Equal(2, tiny.Files);
        Assert.Empty(tiny.Children);
    }

    [Fact]
    public void An_empty_folder_is_listed_with_no_size()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Empty"));

        var empty = Assert.Single(Scan().Children);

        Assert.Equal(0, empty.Bytes);
        Assert.True(empty.IsComplete);
        Assert.False(empty.WasDenied);
    }

    [Fact]
    public void A_folder_that_does_not_exist_is_marked_unreadable()
    {
        var root = DiskScanner.CreateRoot(Path.Combine(_root, "gone"));

        DiskScanner.Scan(root);

        Assert.True(root.WasDenied);
        Assert.Equal(1, root.DeniedFolders);
        Assert.Equal(0, root.Bytes);
    }

    [Fact]
    public void A_cancelled_scan_stops_and_is_not_complete()
    {
        Write(@"A\a.bin", Megabyte);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var root = DiskScanner.CreateRoot(_root);

        DiskScanner.Scan(root, cancelled.Token);

        Assert.False(root.IsComplete);
        Assert.Equal(0, root.Bytes);
    }

    [Fact]
    public void A_drive_root_is_named_by_its_path()
    {
        Assert.Equal(@"C:\", DiskScanner.CreateRoot(@"C:\").Name);
        Assert.Equal("Tools", DiskScanner.CreateRoot(@"C:\Tools\").Name);
    }
}

public class TreemapTests
{
    [Fact]
    public void Tiles_fill_the_area_in_proportion_to_their_weights()
    {
        double[] weights = [50, 25, 15, 10];

        var tiles = Treemap.Layout(weights, 400, 200);

        var total = 400.0 * 200;
        for (var i = 0; i < weights.Length; i++)
            Assert.Equal(total * weights[i] / 100, tiles[i].Width * tiles[i].Height, precision: 3);
    }

    [Fact]
    public void Tiles_stay_inside_the_area_and_do_not_overlap()
    {
        double[] weights = [40, 30, 12, 8, 5, 3, 1, 1];

        var tiles = Treemap.Layout(weights, 300, 240);

        foreach (var tile in tiles)
        {
            Assert.True(tile.X >= -0.001 && tile.Y >= -0.001);
            Assert.True(tile.X + tile.Width <= 300.001 && tile.Y + tile.Height <= 240.001);
        }
        for (var a = 0; a < tiles.Count; a++)
        {
            for (var b = a + 1; b < tiles.Count; b++)
            {
                var overlapX = Math.Min(tiles[a].X + tiles[a].Width, tiles[b].X + tiles[b].Width) - Math.Max(tiles[a].X, tiles[b].X);
                var overlapY = Math.Min(tiles[a].Y + tiles[a].Height, tiles[b].Y + tiles[b].Height) - Math.Max(tiles[a].Y, tiles[b].Y);
                Assert.False(overlapX > 0.001 && overlapY > 0.001, $"Tiles {a} and {b} overlap.");
            }
        }
    }

    [Fact]
    public void Tiles_of_equal_weight_come_out_close_to_square()
    {
        var tiles = Treemap.Layout([1, 1, 1, 1], 200, 200);

        Assert.All(tiles, t => Assert.InRange(t.Width / t.Height, 0.5, 2));
    }

    [Fact]
    public void Nothing_to_show_gives_empty_tiles()
    {
        Assert.All(Treemap.Layout([0, 0], 100, 100), t => Assert.Equal(0, t.Width * t.Height));
        Assert.All(Treemap.Layout([5, 5], 0, 100), t => Assert.Equal(0, t.Width * t.Height));
        Assert.Empty(Treemap.Layout([], 100, 100));
    }
}

/// <summary>Prints how fast the Windows folder is read with different numbers of threads, for a person to read.</summary>
[Trait("Category", "Report")]
public class DiskScanSpeedReport(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory]
    [InlineData(8, System.Threading.ThreadPriority.Normal)]
    [InlineData(8, System.Threading.ThreadPriority.BelowNormal)]
    [InlineData(16, System.Threading.ThreadPriority.Normal)]
    [InlineData(2, System.Threading.ThreadPriority.Normal)]
    public void Reading_the_Windows_folder(int threads, System.Threading.ThreadPriority priority)
    {
        var root = DiskScanner.CreateRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var worker = new Thread(() => DiskScanner.Scan(root, threads: threads)) { Priority = priority };
        worker.Start();
        worker.Join();
        output.WriteLine(
            $"{threads} threads, {priority}: {root.Files:N0} files in {watch.Elapsed.TotalSeconds:F1} s " +
            $"({root.Files / watch.Elapsed.TotalSeconds:N0} files a second)");
    }
}

public class SeenItemsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"dashio-seen-{Guid.NewGuid():N}.json");
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Everything_found_by_the_first_scan_counts_as_already_there()
    {
        var seen = Dashio.Core.Scanning.SeenItems.Load(_path);

        Assert.True(seen.Note(["service:a", "task:b"], Start));

        Assert.Null(seen.NewSince("service:a", Start.AddDays(1)));
    }

    [Fact]
    public void An_item_that_appears_later_is_new_for_two_weeks()
    {
        var seen = Dashio.Core.Scanning.SeenItems.Load(_path);
        seen.Note(["service:a"], Start);
        var added = Start.AddDays(3);

        Assert.True(seen.Note(["service:a", "task:b"], added));

        Assert.Equal(added, seen.NewSince("task:b", added.AddDays(1)));
        Assert.Null(seen.NewSince("service:a", added.AddDays(1)));
        Assert.Null(seen.NewSince("task:b", added.AddDays(15)));
        Assert.False(seen.Note(["service:a", "task:b"], added.AddDays(2)));
    }

    [Fact]
    public void What_was_seen_is_remembered_between_sessions()
    {
        var seen = Dashio.Core.Scanning.SeenItems.Load(_path);
        seen.Note(["service:a"], Start);
        seen.Note(["task:b"], Start.AddDays(2));
        seen.Save();

        var again = Dashio.Core.Scanning.SeenItems.Load(_path);

        Assert.Equal(Start, again.BaselineAt);
        Assert.Equal(Start.AddDays(2), again.NewSince("TASK:B", Start.AddDays(3)));
        Assert.Null(again.NewSince("service:a", Start.AddDays(3)));
    }

    [Fact]
    public void An_unreadable_file_starts_over_without_calling_anything_new()
    {
        File.WriteAllText(_path, "not json");

        var seen = Dashio.Core.Scanning.SeenItems.Load(_path);
        seen.Note(["service:a"], Start);

        Assert.Null(seen.NewSince("service:a", Start.AddDays(1)));
    }
}
