using System.Text.Json;

namespace Dashio.Core.Inventory;

/// <summary>
/// Remembers folder sizes between runs, because measuring every app takes a minute or more.
/// A size is reused until it is older than <see cref="MaxAge"/>.
/// </summary>
public sealed class SizeCache
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);

    private sealed record Entry(long Bytes, DateTimeOffset Measured);

    private readonly string _path;
    private readonly Lock _gate = new();
    private Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public SizeCache(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path) &&
                JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path)) is { } loaded)
                _entries = new Dictionary<string, Entry>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable cache only means everything is measured again.
        }
    }

    /// <summary>The remembered size, however old. Shown at once while a fresh measurement is on its way.</summary>
    public long? Last(string folder)
    {
        lock (_gate)
            return _entries.TryGetValue(folder, out var entry) ? entry.Bytes : null;
    }

    public bool IsFresh(string folder, DateTimeOffset now)
    {
        lock (_gate)
            return _entries.TryGetValue(folder, out var entry) && now - entry.Measured < MaxAge;
    }

    public void Set(string folder, long bytes, DateTimeOffset now)
    {
        lock (_gate)
            _entries[folder] = new Entry(bytes, now);
    }

    /// <summary>Writes the cache, keeping only the folders still in use.</summary>
    public void Save(IEnumerable<string> foldersInUse)
    {
        string json;
        lock (_gate)
        {
            var keep = foldersInUse.ToHashSet(StringComparer.OrdinalIgnoreCase);
            _entries = _entries.Where(e => keep.Contains(e.Key))
                .ToDictionary(e => e.Key, e => e.Value, StringComparer.OrdinalIgnoreCase);
            json = JsonSerializer.Serialize(_entries);
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, json);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
