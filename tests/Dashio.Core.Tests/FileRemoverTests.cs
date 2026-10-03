using Dashio.Core.Storage;

namespace Dashio.Core.Tests;

public sealed class FileRemoverTests : IDisposable
{
    private const int Megabyte = 1024 * 1024;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"dashio-remove-{Guid.NewGuid():N}");

    public FileRemoverTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string Write(string relativePath, int bytes)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private FolderNode Scan()
    {
        var root = DiskScanner.CreateRoot(_root);
        DiskScanner.Scan(root);
        return root;
    }

    [Fact]
    public void A_drive_Windows_and_the_places_programs_and_profiles_live_are_never_deleted()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.NotNull(FileRemover.WhyNot(Path.GetPathRoot(windows)));
        Assert.NotNull(FileRemover.WhyNot(windows));
        Assert.NotNull(FileRemover.WhyNot(Path.Combine(windows, "System32", "drivers")));
        Assert.NotNull(FileRemover.WhyNot(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
        Assert.NotNull(FileRemover.WhyNot(profile));
        // The folder all profiles are in contains this one.
        Assert.NotNull(FileRemover.WhyNot(Path.GetDirectoryName(profile)));
        Assert.NotNull(FileRemover.WhyNot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
        Assert.NotNull(FileRemover.WhyNot(null));
        Assert.NotNull(FileRemover.WhyNot(@"relative\file.bin"));
    }

    [Fact]
    public void An_ordinary_file_or_folder_may_be_deleted()
    {
        Assert.Null(FileRemover.WhyNot(Write("video.bin", 10)));
        Assert.Null(FileRemover.WhyNot(Path.Combine(_root, "Some folder")));
    }

    [Fact]
    public void Deleting_for_good_removes_a_file_and_a_folder_with_what_is_in_it()
    {
        var file = Write("video.bin", 10);
        Write(@"Old\a.bin", 10);
        Write(@"Old\Deeper\b.bin", 10);

        FileRemover.Delete(file, permanently: true);
        FileRemover.Delete(Path.Combine(_root, "Old"), permanently: true);

        Assert.False(File.Exists(file));
        Assert.False(Directory.Exists(Path.Combine(_root, "Old")));
    }

    [Fact]
    public void A_place_that_is_never_deleted_is_refused_before_anything_is_touched()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        Assert.Throws<InvalidOperationException>(() => FileRemover.Delete(windows, permanently: true));
        Assert.True(Directory.Exists(windows));
    }

    [Fact]
    public void A_deleted_folder_leaves_the_figures_of_every_folder_above_it()
    {
        Write(@"Keep\a.bin", 2 * Megabyte);
        Write(@"Keep\Old\b.bin", 3 * Megabyte);
        Write(@"Keep\Old\c.bin", 4 * Megabyte);
        var root = Scan();
        var keep = root.Find(Path.Combine(_root, "Keep"))!;

        Assert.True(keep.Forget("Old"));

        Assert.Empty(keep.Children);
        Assert.Equal(2 * Megabyte, keep.Bytes);
        Assert.Equal(1, keep.Files);
        Assert.Equal(2 * Megabyte, root.Bytes);
        Assert.Null(root.Find(Path.Combine(_root, "Keep", "Old")));
    }

    [Fact]
    public void A_deleted_file_leaves_its_folder_and_the_ones_above()
    {
        Write(@"Keep\a.bin", 2 * Megabyte);
        Write(@"Keep\b.bin", 3 * Megabyte);
        var root = Scan();
        var keep = root.Find(Path.Combine(_root, "Keep"))!;

        Assert.True(keep.Forget("b.bin"));

        Assert.Equal(["a.bin"], keep.LargestFiles.Select(f => f.Name));
        Assert.Equal(2 * Megabyte, keep.OwnBytes);
        Assert.Equal(1, keep.OwnFiles);
        Assert.Equal(2 * Megabyte, root.Bytes);
        Assert.Equal(1, root.Files);
        Assert.False(keep.Forget("b.bin"));
    }
}
