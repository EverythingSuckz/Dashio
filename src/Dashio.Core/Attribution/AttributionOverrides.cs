using System.Text.Json;
using System.Text.RegularExpressions;
using Dashio.Core.Parsing;

namespace Dashio.Core.Attribution;

/// <summary>Hand-written corrections for vendors whose files do not describe themselves well.</summary>
public sealed class AttributionOverrides
{
    public sealed record PublisherAlias(string From, string To);
    public sealed record ItemRule(string NameRegex, string Publisher);
    public sealed record GroupRename(string NameRegex, string Name);

    private sealed record FileModel(
        List<PublisherAlias>? PublisherAliases, List<ItemRule>? ItemRules, List<GroupRename>? GroupRenames);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly Dictionary<string, string> _aliasKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _display = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _aliasWords = new(StringComparer.Ordinal);
    private readonly List<(Regex Pattern, string Publisher)> _itemRules = [];
    private readonly List<(Regex Pattern, string Name)> _renames = [];

    public static AttributionOverrides Empty { get; } = new();

    private AttributionOverrides()
    {
    }

    public static AttributionOverrides LoadDefault()
    {
        using var stream = typeof(AttributionOverrides).Assembly
            .GetManifestResourceStream("Dashio.Core.Attribution.attribution-overrides.json")!;
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public static AttributionOverrides Parse(string json)
    {
        var model = JsonSerializer.Deserialize<FileModel>(json, Json) ?? new FileModel(null, null, null);
        var overrides = new AttributionOverrides();

        foreach (var alias in model.PublisherAliases ?? [])
        {
            var from = NameTokens.NormalizePublisher(alias.From);
            var to = NameTokens.NormalizePublisher(alias.To);
            if (from.Length == 0 || to.Length == 0)
                continue;
            overrides._aliasKeys[from] = to;
            overrides._display[to] = alias.To;
            if (!overrides._aliasWords.TryGetValue(to, out var words))
                overrides._aliasWords[to] = words = [];
            words.UnionWith(from.Split(' '));
        }

        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        foreach (var rule in model.ItemRules ?? [])
            overrides._itemRules.Add((new Regex(rule.NameRegex, options), rule.Publisher));
        foreach (var rename in model.GroupRenames ?? [])
            overrides._renames.Add((new Regex(rename.NameRegex, options), rename.Name));
        return overrides;
    }

    /// <summary>The normalised publisher key, after aliases ("rivet networks" → "intel").</summary>
    public string PublisherKey(string? publisher)
    {
        var key = NameTokens.NormalizePublisher(publisher);
        return _aliasKeys.GetValueOrDefault(key, key);
    }

    /// <summary>The preferred spelling for an aliased publisher, if any.</summary>
    public string? DisplayFor(string publisherKey) => _display.GetValueOrDefault(publisherKey);

    /// <summary>Words of a publisher's other names; they identify the vendor, not a product.</summary>
    public IReadOnlySet<string> AliasWords(string publisherKey) =>
        _aliasWords.TryGetValue(publisherKey, out var words) ? words : EmptyWords;

    private static readonly HashSet<string> EmptyWords = [];

    /// <summary>A publisher forced by an item's name, for files that name a contractor as company.</summary>
    public string? PublisherForItem(string name, string displayName)
    {
        foreach (var (pattern, publisher) in _itemRules)
        {
            if (pattern.IsMatch(name) || pattern.IsMatch(displayName))
                return publisher;
        }
        return null;
    }

    public string Rename(string groupName)
    {
        foreach (var (pattern, name) in _renames)
        {
            if (pattern.IsMatch(groupName))
                return name;
        }
        return groupName;
    }
}
