using System.Text.Json;

namespace Dashio.Core.Scanning;

/// <summary>
/// Remembers which startup items have been seen and when each first appeared, so an item that
/// an app added since can be pointed out. Everything found by the very first scan counts as
/// already there.
/// </summary>
public sealed class SeenItems
{
    /// <summary>An item stops being called new this long after it first appeared.</summary>
    public static readonly TimeSpan NewFor = TimeSpan.FromDays(14);

    private sealed record Model(DateTimeOffset BaselineAt, Dictionary<string, DateTimeOffset> FirstSeen);

    private readonly string _path;
    private Dictionary<string, DateTimeOffset> _firstSeen = new(StringComparer.OrdinalIgnoreCase);

    private SeenItems(string path) => _path = path;

    /// <summary>When the first scan was made. Null until there has been one.</summary>
    public DateTimeOffset? BaselineAt { get; private set; }

    public static SeenItems Load(string path)
    {
        var seen = new SeenItems(path);
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Model>(File.ReadAllText(path)) is { FirstSeen: not null } model)
            {
                seen.BaselineAt = model.BaselineAt;
                seen._firstSeen = new Dictionary<string, DateTimeOffset>(model.FirstSeen, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable file means starting over: everything counts as already there again.
        }
        return seen;
    }

    /// <summary>Notes the items of a scan. Returns true when something was added, so the file needs saving.</summary>
    public bool Note(IEnumerable<string> itemIds, DateTimeOffset now)
    {
        BaselineAt ??= now;
        var added = false;
        foreach (var id in itemIds)
            added |= _firstSeen.TryAdd(id, now);
        return added;
    }

    /// <summary>When the item first appeared, if that was after the first scan and not long ago.</summary>
    public DateTimeOffset? NewSince(string itemId, DateTimeOffset now) =>
        BaselineAt is { } baseline && _firstSeen.TryGetValue(itemId, out var first) && first > baseline && now - first < NewFor
            ? first
            : null;

    public void Save()
    {
        if (BaselineAt is not { } baseline)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(new Model(baseline, _firstSeen)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
