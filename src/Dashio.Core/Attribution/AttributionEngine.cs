using Dashio.Core.Collectors;
using Dashio.Core.Models;
using Dashio.Core.Parsing;

namespace Dashio.Core.Attribution;

/// <summary>
/// Groups autostart items into apps. Pure: no I/O, so it can be tested from recorded data.
/// Each item is placed by the first rule that matches, then groups that are the same product are merged.
/// </summary>
public sealed class AttributionEngine
{
    public const string WindowsGroupId = "windows";
    public const string UnmatchedGroupId = "unmatched";

    public const string RuleWindows = "Windows";
    public const string RuleStorePackage = "StorePackage";
    public const string RuleInstalledAppFolder = "InstalledAppFolder";
    public const string RuleDriverPackage = "DriverPackage";
    public const string RuleFolder = "Folder";
    public const string RuleFileIdentity = "FileIdentity";
    public const string RuleNameMatch = "NameMatch";
    public const string RuleNone = "None";

    /// <summary>A leading word shared by more names than this is a common word, not a product.</summary>
    private const int MaxNodesPerToken = 6;

    /// <summary>Marks the whole-name key so it cannot collide with a word.</summary>
    private const char NameKeyPrefix = '=';

    private readonly AttributionOverrides _overrides;

    public AttributionEngine(AttributionOverrides? overrides = null) =>
        _overrides = overrides ?? AttributionOverrides.LoadDefault();

    private enum NodeKind { Source, Folder, Vendor, Windows, Unmatched }

    private sealed class Node
    {
        public required string Key;
        public required NodeKind Kind;
        public required string Name;
        public int NamePriority;
        public string? Publisher;
        public string PublisherKey = "";
        public AppSource? Source;
        public readonly List<AttributedItem> Items = [];

        /// <summary>The product folder's own name, for nodes made from a folder.</summary>
        public string? FolderName;

        /// <summary>Leading distinctive words of the node's names, plus its whole-name key.</summary>
        public List<string> Tokens = [];
        public Node Parent;

        public Node() => Parent = this;

        public bool IsDriver => Key.StartsWith("drv:", StringComparison.OrdinalIgnoreCase);
        public bool Mergeable => Kind is NodeKind.Source or NodeKind.Folder;

        /// <summary>An Apps &amp; Features entry or Store package that holds items of its own. Driver packages do not count.</summary>
        public bool Anchored =>
            Source is { Kind: AppSourceKind.InstalledApp or AppSourceKind.StorePackage } && Items.Count > 0;

        public Node Root
        {
            get
            {
                var node = this;
                while (node.Parent != node)
                    node = node.Parent = node.Parent.Parent;
                return node;
            }
        }
    }

    public IReadOnlyList<AppGroup> Group(IReadOnlyList<AutostartItem> items, IReadOnlyList<AppSource> sources)
    {
        var nodes = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
            nodes[source.Id] = SourceNode(source);

        var knownVendors = nodes.Values.Select(n => n.PublisherKey).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);
        var installedApps = sources
            .Where(s => s.Kind == AppSourceKind.InstalledApp && s.InstallLocation is not null)
            .OrderByDescending(s => s.InstallLocation!.Length)
            .ThenBy(s => s.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var packages = sources.Where(s => s.Kind == AppSourceKind.StorePackage && s.InstallLocation is not null).ToList();

        var windows = NewNode(nodes, WindowsGroupId, NodeKind.Windows, "Windows", 0);
        var unmatched = NewNode(nodes, UnmatchedGroupId, NodeKind.Unmatched, "Unmatched", 0);

        foreach (var item in items.OrderBy(i => i.Id, StringComparer.OrdinalIgnoreCase))
            Place(item, nodes, windows, unmatched, knownVendors, installedApps, packages);

        var ordered = nodes.Values.OrderBy(n => n.Key, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var node in ordered)
            Finish(node);

        Merge(ordered);
        RescueByName(ordered, unmatched);

        return BuildGroups(ordered);
    }

    private Node SourceNode(AppSource source) => new()
    {
        Key = source.Id,
        Kind = NodeKind.Source,
        Name = source.Kind == AppSourceKind.InstalledApp
            ? NameTokens.CleanAppName(source.Name)
            : NameTokens.CleanDisplayName(source.Name),
        NamePriority = source.Kind switch
        {
            AppSourceKind.StorePackage => 1,
            AppSourceKind.InstalledApp => 2,
            _ => 3,
        },
        Publisher = source.Publisher,
        PublisherKey = _overrides.PublisherKey(source.Publisher),
        Source = source,
    };

    private static Node NewNode(Dictionary<string, Node> nodes, string key, NodeKind kind, string name, int priority)
    {
        var node = new Node { Key = key, Kind = kind, Name = name, NamePriority = priority };
        nodes[key] = node;
        return node;
    }

    private void Place(
        AutostartItem item, Dictionary<string, Node> nodes, Node windows, Node unmatched,
        HashSet<string> knownVendors, List<AppSource> installedApps, List<AppSource> packages)
    {
        if (item.IsProtected)
        {
            windows.Items.Add(new AttributedItem(item, RuleWindows, "Part of Windows", Confidence.High));
            return;
        }

        var path = item.TargetPath;
        var hint = _overrides.PublisherForItem(item.Name, item.DisplayName);
        var company = hint ?? item.Evidence?.Company;

        // Rule 1: Store package.
        var family = item.PackageFamilyName;
        if (family is null && path is not null)
            family = packages.FirstOrDefault(p => InstallFolders.Contains(p.InstallLocation!, path))?.PackageFamilyName;
        if (family is not null)
        {
            var key = $"pkg:{family}";
            if (!nodes.TryGetValue(key, out var package))
                package = NewNode(nodes, key, NodeKind.Folder, item.DisplayName, 1);
            package.Items.Add(new AttributedItem(
                item, RuleStorePackage, $"Part of the Store package {package.Name}", Confidence.High));
            return;
        }

        if (path is not null)
        {
            // Rule 2: installed app folder. The list is longest-first, so the most specific app wins.
            var app = installedApps.FirstOrDefault(a => InstallFolders.Contains(a.InstallLocation!, path));
            if (app is not null)
            {
                var node = nodes[app.Id];
                node.Items.Add(new AttributedItem(
                    item, RuleInstalledAppFolder, $"Installed in the folder of {node.Name}", Confidence.High));
                return;
            }

            // Rule 3: driver package.
            if (DriverPackageCollector.Locate(path) is { } driver)
            {
                var key = DriverPackageCollector.IdFor(driver.FolderName);
                if (!nodes.TryGetValue(key, out var package))
                    package = NewNode(nodes, key, NodeKind.Folder, driver.InfName, 3);
                package.Items.Add(new AttributedItem(
                    item, RuleDriverPackage, $"Installed by the driver package {driver.InfName}.inf", Confidence.High));
                return;
            }

            // Rule 4: vendor/product folder.
            var companyKey = _overrides.PublisherKey(company);
            bool IsVendor(string folder)
            {
                var folderKey = _overrides.PublisherKey(folder);
                if (folderKey.Length == 0)
                    return false;
                return knownVendors.Contains(folderKey) ||
                       (companyKey.Length > 0 && NameTokens.PublishersCompatible(folderKey, companyKey));
            }

            if (FolderKey.For(path, IsVendor) is { } folder)
            {
                var key = $"dir:{folder.Key}";
                if (!nodes.TryGetValue(key, out var node))
                {
                    node = NewNode(nodes, key, NodeKind.Folder, FolderDisplayName(folder), 4);
                    node.Publisher = folder.Vendor;
                    node.FolderName = folder.Name;
                }
                var shown = folder.Vendor is null ? folder.Name : $@"{folder.Vendor}\{folder.Name}";
                node.Items.Add(new AttributedItem(item, RuleFolder, $"In the folder {shown}", Confidence.Medium));
                return;
            }
        }

        // Rule 5: company name, or a signer that names the maker.
        var identity = company;
        var reason = hint is not null
            ? $"Its name matches a built-in rule for {hint}"
            : $"Its file names {company} as the company";
        if (string.IsNullOrWhiteSpace(identity) && item.Evidence is { Signer: { } signer, SignerIsGeneric: false })
        {
            identity = signer;
            reason = $"Signed by {signer}";
        }

        var identityKey = _overrides.PublisherKey(identity);
        if (identityKey.Length > 0)
        {
            var key = $"co:{identityKey}";
            if (!nodes.TryGetValue(key, out var vendor))
            {
                var display = _overrides.DisplayFor(identityKey) ?? NameTokens.ShortPublisher(identity!);
                vendor = NewNode(nodes, key, NodeKind.Vendor, $"Other {display} components", 5);
                vendor.Publisher = display;
                vendor.PublisherKey = identityKey;
            }
            vendor.Items.Add(new AttributedItem(item, RuleFileIdentity, reason, Confidence.Low));
            return;
        }

        unmatched.Items.Add(new AttributedItem(
            item, RuleNone, "Nothing identifies the app this belongs to", Confidence.Low));
    }

    private static string FolderDisplayName(FolderKey folder)
    {
        var name = NameTokens.CleanDisplayName(folder.Name);
        if (folder.Vendor is null)
            return name;
        var vendorWord = NameTokens.Words(folder.Vendor).FirstOrDefault();
        return vendorWord is not null && NameTokens.Words(name).Contains(vendorWord)
            ? name
            : $"{folder.Vendor} {name}";
    }

    /// <summary>Fills in what is only known once all items are placed: publisher, driver package name, tokens.</summary>
    private void Finish(Node node)
    {
        if (node.Kind is NodeKind.Windows or NodeKind.Unmatched)
            return;

        if (string.IsNullOrWhiteSpace(node.Publisher) && node.Items.Count > 0)
            node.Publisher = MostCommon(node.Items.Select(i => ItemCompany(i.Item)));
        if (node.PublisherKey.Length == 0)
            node.PublisherKey = _overrides.PublisherKey(node.Publisher);

        if (node.IsDriver && node.Items.Count > 0)
            node.Name = DriverPackageName(node);

        // A driver package is named after its services, which are descriptions rather than brands,
        // so it takes no part in name matching.
        if (!node.Mergeable || node.IsDriver)
            return;

        var aliasWords = _overrides.AliasWords(node.PublisherKey);
        string? Leading(string? name, bool split) => NameTokens
            .DistinctiveTokens(name, node.PublisherKey, split)
            .FirstOrDefault(t => !aliasWords.Contains(t));

        var tokens = new List<string?> { Leading(node.FolderName ?? node.Name, split: false) };
        if (node.Source is { Kind: AppSourceKind.StorePackage, PackageFamilyName: { } family })
            tokens.Add(Leading(PackageIdentityTail(family), split: true));

        node.Tokens = tokens.Where(t => t is not null).Select(t => t!).Distinct().ToList();
        if (node.Tokens.Count == 0)
            node.Tokens = NameTokens.BrandTokens(node.Name, node.PublisherKey).Take(1).ToList();

        var wholeName = string.Join(' ', NameTokens.Words(node.Name, splitCamelCase: false));
        if (wholeName.Length > 0)
            node.Tokens.Add(NameKeyPrefix + wholeName);
    }

    private string? ItemCompany(AutostartItem item) =>
        _overrides.PublisherForItem(item.Name, item.DisplayName)
        ?? item.Evidence?.Company
        ?? (item.Evidence is { Signer: { } signer, SignerIsGeneric: false } ? signer : null);

    private static string? MostCommon(IEnumerable<string?> values) => values
        .Where(v => !string.IsNullOrWhiteSpace(v))
        .GroupBy(v => v!, StringComparer.OrdinalIgnoreCase)
        .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault()?.Key;

    /// <summary>"RivetNetworks.KillerControlCenter_abc" → "KillerControlCenter": the part that names the product.</summary>
    private static string PackageIdentityTail(string familyName)
    {
        var identity = familyName.Split('_')[0];
        var dot = identity.IndexOf('.');
        return dot < 0 ? identity : identity[(dot + 1)..];
    }

    private static string DriverPackageName(Node node)
    {
        // The collector already found a description in the INF.
        if (node.Source is { } source && DriverPackageCollector.Locate(source.InstallLocation + "\\") is { } located &&
            !source.Name.Equals(located.InfName, StringComparison.OrdinalIgnoreCase))
            return NameTokens.CleanDisplayName(source.Name);

        var displayNames = node.Items.Select(i => i.Item.DisplayName).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (displayNames.Count == 1)
            return NameTokens.CleanDisplayName(displayNames[0]);

        var prefix = CommonWordPrefix(displayNames);
        return prefix.Split(' ').Length >= 2 ? NameTokens.CleanDisplayName(prefix) : NameTokens.CleanDisplayName(displayNames[0]);
    }

    private static string CommonWordPrefix(IReadOnlyList<string> names)
    {
        var first = names[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var count = first.Length;
        foreach (var name in names.Skip(1))
        {
            var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var i = 0;
            while (i < count && i < words.Length && words[i].Equals(first[i], StringComparison.OrdinalIgnoreCase))
                i++;
            count = i;
        }
        return string.Join(' ', first.Take(count));
    }

    /// <summary>
    /// A shared word needs both publishers known and compatible; a shared whole name only needs
    /// them not to conflict.
    /// </summary>
    private static bool CanJoin(Node a, Node b, string token)
    {
        if (!NameTokens.PublishersCompatible(a.PublisherKey, b.PublisherKey))
            return false;
        return token[0] == NameKeyPrefix || (a.PublisherKey.Length > 0 && b.PublisherKey.Length > 0);
    }

    /// <summary>
    /// Joins nodes that are the same product: their names lead with the same distinctive word, or are equal.
    /// First the nodes that hold items are joined with each other. Then an app or package with no items
    /// of its own is attached, but only when exactly one group fits. Two apps, or two packages, that each
    /// hold items stay separate products.
    /// </summary>
    private static void Merge(IReadOnlyList<Node> ordered)
    {
        var byToken = new SortedDictionary<string, List<Node>>(StringComparer.Ordinal);
        foreach (var node in ordered.Where(n => n.Mergeable))
        {
            foreach (var token in node.Tokens)
            {
                if (!byToken.TryGetValue(token, out var list))
                    byToken[token] = list = [];
                list.Add(node);
            }
        }

        // Per set: how many installed apps and how many Store packages already hold items of their own.
        var anchors = new Dictionary<Node, (int Apps, int Packages)>();
        (int Apps, int Packages) Anchors(Node root) => anchors.GetValueOrDefault(root);
        foreach (var node in ordered.Where(n => n.Anchored))
            anchors[node] = node.Source!.Kind == AppSourceKind.InstalledApp ? (1, 0) : (0, 1);

        bool TryUnion(Node a, Node b)
        {
            var rootA = a.Root;
            var rootB = b.Root;
            if (rootA == rootB)
                return true;
            var apps = Anchors(rootA).Apps + Anchors(rootB).Apps;
            var packages = Anchors(rootA).Packages + Anchors(rootB).Packages;
            if (apps > 1 || packages > 1)
                return false;
            rootB.Parent = rootA;
            anchors[rootA] = (apps, packages);
            return true;
        }

        foreach (var (token, nodes) in byToken)
        {
            if (nodes.Count < 2 || nodes.Count > MaxNodesPerToken)
                continue;

            var withItems = nodes.Where(n => n.Items.Count > 0).ToList();
            for (var i = 0; i < withItems.Count; i++)
            {
                for (var j = i + 1; j < withItems.Count; j++)
                {
                    if (CanJoin(withItems[i], withItems[j], token))
                        TryUnion(withItems[i], withItems[j]);
                }
            }

            foreach (var lone in nodes.Where(n => n.Items.Count == 0 && n.Root == n))
            {
                var fits = withItems.Where(n => CanJoin(lone, n, token)).Select(n => n.Root).Distinct().ToList();
                if (fits.Count == 1)
                    TryUnion(fits[0], lone);
            }
        }
    }

    /// <summary>
    /// An item nothing could place, or known only by vendor, joins a product when its own name
    /// leads with that product's word and exactly one product fits.
    /// </summary>
    private static void RescueByName(IReadOnlyList<Node> ordered, Node unmatched)
    {
        var byToken = ordered.Where(n => n.Mergeable)
            .SelectMany(n => n.Tokens.Where(t => t[0] != NameKeyPrefix).Select(t => (Token: t, Node: n)))
            .ToLookup(x => x.Token, x => x.Node, StringComparer.Ordinal);

        foreach (var bucket in ordered.Where(n => n == unmatched || n.Kind == NodeKind.Vendor))
        {
            foreach (var attributed in bucket.Items.ToList())
            {
                var item = attributed.Item;
                var token = NameTokens.DistinctiveTokens(item.DisplayName, bucket.PublisherKey).FirstOrDefault()
                            ?? NameTokens.DistinctiveTokens(item.Name, bucket.PublisherKey).FirstOrDefault();
                if (token is null || token.Length < 5)
                    continue;

                var candidates = byToken[token]
                    .Where(n => NameTokens.PublishersCompatible(n.PublisherKey, bucket.PublisherKey))
                    .ToList();
                if (candidates.Count is 0 or > MaxNodesPerToken)
                    continue;

                var roots = candidates.Select(n => n.Root).Distinct().ToList();
                Node target;
                if (roots.Count == 1)
                    target = roots[0];
                else if (candidates.All(n => Members(ordered, n.Root).All(m => m.Items.Count == 0)) && MutuallyCompatible(candidates))
                    target = candidates.OrderBy(n => n.NamePriority).ThenBy(n => n.Key, StringComparer.OrdinalIgnoreCase).First().Root;
                else
                    continue;

                bucket.Items.Remove(attributed);
                target.Items.Add(new AttributedItem(
                    item, RuleNameMatch, $"Its name matches {BestName(Members(ordered, target))}", Confidence.Low));
            }
        }
    }

    private static bool MutuallyCompatible(List<Node> nodes) =>
        nodes.All(a => nodes.All(b => NameTokens.PublishersCompatible(a.PublisherKey, b.PublisherKey)));

    private static List<Node> Members(IEnumerable<Node> nodes, Node root) =>
        nodes.Where(n => n.Root == root).ToList();

    /// <summary>
    /// An Apps &amp; Features entry or package that holds items names the group best; then one that
    /// was attached by name; then a driver package or folder.
    /// </summary>
    private static string BestName(IEnumerable<Node> members) => members
        .OrderBy(n => n.Anchored ? 0 : n.Source is { Kind: not AppSourceKind.DriverPackage } ? 1 : 2)
        .ThenBy(n => n.NamePriority)
        .ThenByDescending(n => n.Items.Count)
        .ThenBy(n => n.Key, StringComparer.OrdinalIgnoreCase)
        .First().Name;

    private IReadOnlyList<AppGroup> BuildGroups(IReadOnlyList<Node> ordered)
    {
        var groups = new List<AppGroup>();
        foreach (var set in ordered.GroupBy(n => n.Root))
        {
            var members = set.ToList();
            var items = members.SelectMany(n => n.Items)
                .OrderBy(i => i.Item.Kind).ThenBy(i => i.Item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.Item.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (items.Count == 0)
                continue;

            var kind = set.Key.Kind;
            var sources = members.Where(n => n.Source is not null).Select(n => n.Source!).ToList();
            var name = kind is NodeKind.Windows or NodeKind.Unmatched
                ? set.Key.Name
                : _overrides.Rename(NameTokens.CleanDisplayName(BestName(members)));

            groups.Add(new AppGroup
            {
                Id = members[0].Key,
                Name = name,
                Publisher = kind == NodeKind.Windows ? "Microsoft" : Publisher(members),
                Items = items,
                Sources = sources,
                Confidence = items.Min(i => i.Confidence),
                IconPath = Icon(sources, items),
                IsWindows = kind == NodeKind.Windows,
                IsUnmatched = kind == NodeKind.Unmatched,
                IsVendorBucket = kind == NodeKind.Vendor,
            });
        }
        return groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Id, StringComparer.Ordinal).ToList();
    }

    private string? Publisher(List<Node> members)
    {
        var best = members
            .Where(n => !string.IsNullOrWhiteSpace(n.Publisher))
            .GroupBy(n => n.PublisherKey)
            .OrderByDescending(g => g.Sum(n => Math.Max(1, n.Items.Count)))
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .FirstOrDefault();
        if (best is null)
            return null;
        if (_overrides.DisplayFor(best.Key) is { } alias)
            return alias;

        // Prefer a spelling that is not shouting ("INTEL CORP").
        return best.Select(n => NameTokens.CleanDisplayName(n.Publisher!))
            .OrderBy(p => p.Any(char.IsLower) ? 0 : 1)
            .ThenBy(p => p.Length)
            .ThenBy(p => p, StringComparer.Ordinal)
            .First();
    }

    private static string? Icon(List<AppSource> sources, List<AttributedItem> items)
    {
        var fromSource = sources
            .OrderBy(s => s.Kind == AppSourceKind.StorePackage ? 0 : 1)
            .Select(s => s.IconPath)
            .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
        if (fromSource is not null)
            return fromSource;

        return items.Select(i => i.Item.TargetPath)
            .Where(p => p is not null)
            .OrderBy(p => p!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault();
    }
}
