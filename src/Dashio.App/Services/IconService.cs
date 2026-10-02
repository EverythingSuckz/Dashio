using Dashio.Core.Attribution;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Dashio.App.Services;

/// <summary>Loads an app's real icon from an image file or from the icon of an executable.</summary>
public sealed class IconService
{
    private const uint IconSize = 64;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif",
    };

    private readonly Dictionary<string, Task<ImageSource?>> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Must be called on the UI thread. Returns null when no icon can be read.</summary>
    public Task<ImageSource?> GetAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult<ImageSource?>(null);
        if (!_cache.TryGetValue(path, out var task))
            _cache[path] = task = LoadAsync(path);
        return task;
    }

    /// <summary>
    /// Windows sometimes fails to hand out an icon while many are being asked for at once, as right
    /// after a scan, so a failure gets a couple more tries before the generic icon is settled on.
    /// </summary>
    private static async Task<ImageSource?> LoadAsync(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (await TryLoadAsync(path) is { } icon)
                return icon;
            if (attempt == 2 || ImageExtensions.Contains(Path.GetExtension(path)) || !File.Exists(path))
                return null;
            await Task.Delay(400 * (attempt + 1));
        }
    }

    private static async Task<ImageSource?> TryLoadAsync(string path)
    {
        try
        {
            if (path.Equals(AttributionEngine.WindowsIconPath, StringComparison.OrdinalIgnoreCase))
                return new BitmapImage(new Uri("ms-appx:///Assets/WindowsLogo.png"));

            if (ImageExtensions.Contains(Path.GetExtension(path)))
            {
                var image = ResolveScaledAsset(path);
                return image is null ? null : new BitmapImage(new Uri(image));
            }

            if (!File.Exists(path))
                return null;

            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumbnail = await file.GetThumbnailAsync(
                ThumbnailMode.SingleItem, IconSize, ThumbnailOptions.UseCurrentScale);
            if (thumbnail is null || thumbnail.Size == 0)
                return null;

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(thumbnail);
            return bitmap;
        }
        catch (Exception)
        {
            // An icon is decoration; any failure just means the generic icon is shown.
            return null;
        }
    }

    /// <summary>
    /// A package logo is declared as "Logo.png" but stored as "Logo.scale-200.png" and similar.
    /// Picks the largest variant that exists.
    /// </summary>
    private static string? ResolveScaledAsset(string path)
    {
        if (File.Exists(path))
            return path;

        var folder = Path.GetDirectoryName(path);
        if (folder is null || !Directory.Exists(folder))
            return null;

        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        return Directory.EnumerateFiles(folder, $"{stem}.*{extension}")
            .Where(f => !f.Contains("contrast-", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => new FileInfo(f).Length)
            .FirstOrDefault();
    }
}
