using Dashio.Core.Attribution;
using Dashio.Core.Collectors;
using Dashio.Core.Models;
using Dashio.Core.Parsing;

namespace Dashio.Core.Processes;

/// <summary>
/// Decides which app a running process belongs to. Pure: it works from the groups and app
/// sources of a scan, so it can be tested with hand-written data.
/// A process joins a group from the scan when a strong sign ties it to that group; otherwise
/// it gets a group of its own. A shared word in a name is never enough, because a wrong match is
/// worse than a missed one.
/// </summary>
public sealed partial class ProcessAttributor
{
    public const string VirtualMachinesGroupId = "vm";

    private static readonly string WinDir =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

    private readonly AttributionOverrides _overrides;
    private readonly AppGroup? _windows;
    private readonly Dictionary<string, AppGroup> _byService = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AppGroup?> _byFile = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AppGroup?> _byDriverFolder = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AppGroup?> _byProductFolder = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AppGroup> _bySource = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProcessOwner?> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProcessOwner> _sameAs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AppSource> _located;
    private readonly HashSet<string> _knownVendors;

    public ProcessAttributor(
        IReadOnlyList<AppGroup> groups, IReadOnlyList<AppSource> sources, AttributionOverrides? overrides = null)
    {
        _overrides = overrides ?? AttributionOverrides.LoadDefault();
        _windows = groups.FirstOrDefault(g => g.IsWindows);
        _knownVendors = sources.Select(s => _overrides.PublisherKey(s.Publisher)).Where(k => k.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        _located = sources
            .Where(s => s.Kind != AppSourceKind.DriverPackage && s.InstallLocation is not null)
            .OrderByDescending(s => s.InstallLocation!.Length)
            .ThenBy(s => s.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var group in groups)
        {
            foreach (var source in group.Sources)
                _bySource[source.Id] = group;

            // "Unmatched" is a leftover bin, not an app; a process is better named after its own file.
            if (group.IsUnmatched)
                continue;

            foreach (var item in group.Items.Select(i => i.Item))
            {
                if (item.Kind == AutostartKind.Service)
                    _byService[item.Name] = group;
                if (item.TargetPath is not { } path)
                    continue;

                AddUnique(_byFile, path, group);
                if (DriverPackageCollector.Locate(path) is { } driver)
                    AddUnique(_byDriverFolder, driver.FolderName, group);
                else if (!group.IsWindows && !group.IsVendorBucket &&
                         ProductFolders.For(path, item.Evidence?.Company, _knownVendors, _overrides) is { } folder)
                    AddUnique(_byProductFolder, folder.Key, group);
            }
        }

        // An SDK can register dozens of components that all live in one folder. They are one app.
        var sharing = _located.Where(s => !s.IsSystem && !_bySource.ContainsKey(s.Id))
            .GroupBy(s => s.InstallLocation!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);
        foreach (var folder in sharing)
        {
            var members = folder.OrderBy(s => s.Id, StringComparer.OrdinalIgnoreCase).ToList();
            var names = members.Select(SourceName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var shared = New(
                members[0].Id,
                names.Count == 1 ? names[0] : SharedName(names, folder.Key),
                members.Select(m => m.Publisher).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)),
                members.Select(m => m.IconPath).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)),
                members[0]);
            foreach (var member in members)
                _sameAs[member.Id] = shared;
        }

        foreach (var group in groups.Where(g => !g.IsWindows && !g.IsUnmatched && !g.IsVendorBucket))
            AddName(Existing(group));
        foreach (var source in _located.Where(s => !s.IsSystem && !_bySource.ContainsKey(s.Id)))
            AddName(_sameAs.GetValueOrDefault(source.Id) ?? FromSource(source, null));

        // Entries that say nothing about where they are installed come last, so one that does
        // keeps its place. An SDK or a runtime registers one such entry for every version.
        var unlocated = sources
            .Where(s => s.Kind != AppSourceKind.DriverPackage && s.InstallLocation is null)
            .Where(s => !s.IsSystem && !s.IsHiddenComponent && !_bySource.ContainsKey(s.Id))
            .OrderBy(s => s.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var source in unlocated)
            AddName(FromSource(source, null));
    }

    /// <summary>The words the names start with in common, or failing that the folder's own name.</summary>
    private string SharedName(List<string> names, string folder)
    {
        var prefix = AttributionEngine.CommonWordPrefix(names);
        if (prefix.Length >= 3)
            return NameTokens.CleanDisplayName(prefix);
        return ProductFolders.For(folder + "\\x", null, _knownVendors, _overrides) is { } product
            ? ProductFolders.DisplayName(product)
            : Path.GetFileName(folder);
    }

    private static string SourceName(AppSource source) => source.Kind == AppSourceKind.InstalledApp
        ? NameTokens.CleanAppName(source.Name)
        : NameTokens.CleanDisplayName(source.Name);

    /// <summary>The owner a source ends up with after duplicates have been folded together.</summary>
    private ProcessOwner Canonical(AppSource source, string? processPath)
    {
        if (!_sameAs.TryGetValue(source.Id, out var owner))
            return FromSource(source, processPath);
        // A folder-sharing set can itself be the same app as another entry of the same name.
        return _sameAs.TryGetValue(owner.GroupId, out var further) && further.GroupId != owner.GroupId ? further : owner;
    }

    private void AddName(ProcessOwner owner)
    {
        if (!_byName.TryGetValue(owner.Name, out var existing))
        {
            _byName[owner.Name] = owner;
            return;
        }
        if (existing is null)
            return;

        // One app is often registered more than once: twice by its installer, or as an installed
        // app and as a Store package. The same name from the same maker is the same app.
        var existingMaker = _overrides.PublisherKey(existing.Publisher);
        var maker = _overrides.PublisherKey(owner.Publisher);
        var sameApp = existing.GroupId == owner.GroupId ||
            (existing.Source?.InstallLocation is { } folder &&
             folder.Equals(owner.Source?.InstallLocation, StringComparison.OrdinalIgnoreCase)) ||
            (existingMaker.Length > 0 && maker.Length > 0 && NameTokens.PublishersCompatible(existingMaker, maker));
        if (sameApp)
            _sameAs[owner.GroupId] = existing;
        else
            _byName[owner.Name] = null;
    }

    private ProcessOwner FromSource(AppSource source, string? processPath) =>
        New(source.Id, _overrides.Rename(SourceName(source)), source.Publisher, source.IconPath ?? processPath, source);

    /// <summary>
    /// An app whose name is exactly the folder's name, when only one app has that name and the
    /// makers do not conflict. A shared word is not enough.
    /// </summary>
    private ProcessOwner? ByExactName(string name, string? company, string path)
    {
        if (_byName.GetValueOrDefault(name) is not { } owner)
            return null;
        // A folder in the user's app data named after an app is that app's own data folder, and the
        // tools it downloads there (language servers, updaters) are often made by someone else.
        if (AppDataRoots.Any(root => path.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
            return owner;
        return NameTokens.PublishersCompatible(_overrides.PublisherKey(owner.Publisher), _overrides.PublisherKey(company))
            ? owner
            : null;
    }

    private static readonly string TempFolder = Path.GetTempPath().TrimEnd('\\');

    [System.Text.RegularExpressions.GeneratedRegex(
        @"^v?\d+\.\d|(^|[-_.])(x86_64|x86|x64|amd64|arm64|aarch64|i686|win32|win64|windows|linux|darwin|msvc|gnu)([-_.]|$)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex BuildFolderName();

    private static readonly string[] AppDataRoots =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).TrimEnd('\\') + "\\",
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData).TrimEnd('\\') + "\\",
    ];

    /// <summary>A key claimed by two different groups identifies neither, so it is kept as null.</summary>
    private static void AddUnique(Dictionary<string, AppGroup?> index, string key, AppGroup group)
    {
        if (index.TryGetValue(key, out var existing) && existing != group)
            index[key] = null;
        else
            index[key] = group;
    }

    /// <param name="evidence">
    /// Reads the file's version info and signer. It is only called when the cheaper signs fail,
    /// and may return null when the answer is not ready yet; the result is then provisional.
    /// </param>
    public ProcessOwner Assign(RunningProcess process, Func<string, FileEvidence?> evidence)
    {
        if (ByServices(process.Services) is { } hosting)
            return Existing(hosting);

        if (process.Path is not { } path)
        {
            return process.Name.StartsWith("vmmem", StringComparison.OrdinalIgnoreCase)
                ? New(VirtualMachinesGroupId, "Virtual machines (WSL, Hyper-V)", "Microsoft", null, null)
                : Windows();
        }

        if (_byFile.GetValueOrDefault(path) is { } sameFile)
            return Existing(sameFile);

        if (_located.FirstOrDefault(s => InstallFolders.Contains(s.InstallLocation!, path)) is { } source)
        {
            if (_bySource.TryGetValue(source.Id, out var owner))
                return Existing(owner);
            return source.IsSystem ? Windows() : Canonical(source, path);
        }

        if (DriverPackageCollector.Locate(path) is { } driver)
        {
            return _byDriverFolder.GetValueOrDefault(driver.FolderName) is { } driverGroup
                ? Existing(driverGroup)
                : New(DriverPackageCollector.IdFor(driver.FolderName), driver.InfName, null, path, null);
        }

        var facts = evidence(path);
        var inWindowsFolder = path.StartsWith(WinDir, StringComparison.OrdinalIgnoreCase);
        // Until the signature has been read, a file in the Windows folder is taken to be Windows.
        if (facts?.IsWindowsComponent ?? inWindowsFolder)
            return Windows();

        var company = facts?.Company ?? (facts is { Signer: { } signer, SignerIsGeneric: false } ? signer : null);
        // A dot folder (".local") or the temp folder is a place files are dropped into, not a product.
        if (!inWindowsFolder && ProductFolders.For(path, company, _knownVendors, _overrides) is { } folder &&
            !folder.Name.StartsWith('.') && !folder.Key.Equals(TempFolder, StringComparison.OrdinalIgnoreCase))
        {
            if (_byProductFolder.GetValueOrDefault(folder.Key) is { } sameFolder)
                return Existing(sameFolder);
            // A folder named after a version or a build target ("stable-x86_64-pc-windows-msvc") still
            // says which programs belong together, but it is no name for them: the file knows better.
            var name = BuildFolderName().IsMatch(folder.Name)
                ? facts?.Product ?? facts?.Description ?? Path.GetFileNameWithoutExtension(path)
                : _overrides.Rename(ProductFolders.DisplayName(folder));
            return ByExactName(name, company, path)
                   ?? New($"dir:{folder.Key}", name, folder.Vendor ?? ShortCompany(company), path, null);
        }

        var fileName = Path.GetFileNameWithoutExtension(path);
        return New(
            $"exe:{path.ToLowerInvariant()}",
            facts?.Description ?? facts?.Product ?? fileName,
            ShortCompany(company), path, null);
    }

    /// <summary>The app an installed-app entry or Store package belongs to, under the same id its processes get.</summary>
    public ProcessOwner OwnerOfSource(AppSource source)
    {
        if (_bySource.TryGetValue(source.Id, out var group))
            return Existing(group);
        return source.IsSystem ? Windows() : Canonical(source, null);
    }

    /// <summary>
    /// The known app a file belongs to, or null. Unlike a process, a file that matches nothing is
    /// left unclaimed instead of being given a group of its own.
    /// </summary>
    public ProcessOwner? OwnerOfPath(string path)
    {
        var owner = Assign(new RunningProcess(0, "", path, []), _ => null);
        return owner.IsWindows || (owner.Group is null && owner.Source is null) ? null : owner;
    }

    /// <summary>The known app a folder of data belongs to, or null.</summary>
    public ProcessOwner? OwnerOfFolder(string folder) => OwnerOfPath(Path.Combine(folder, "_"));

    /// <summary>Whether a folder is named after a software maker, so its subfolders are the products.</summary>
    public bool IsVendorFolder(string folder)
    {
        var key = _overrides.PublisherKey(Path.GetFileName(folder));
        return key.Length > 0 && _knownVendors.Contains(key);
    }

    /// <summary>The group most of the hosted services belong to. Windows wins a tie, as the host is usually its own.</summary>
    private AppGroup? ByServices(IReadOnlyList<string> services)
    {
        if (services.Count == 0)
            return null;
        return services
            .Select(s => _byService.GetValueOrDefault(s))
            .Where(g => g is not null)
            .GroupBy(g => g!)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key.IsWindows ? 0 : 1)
            .ThenBy(g => g.Key.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()?.Key;
    }

    private static string? ShortCompany(string? company) =>
        string.IsNullOrWhiteSpace(company) ? null : NameTokens.ShortPublisher(company);

    private ProcessOwner Windows() => _windows is not null
        ? Existing(_windows)
        : new ProcessOwner(
            AttributionEngine.WindowsGroupId, "Windows", "Microsoft", AttributionEngine.WindowsIconPath, true, null, null);

    private static ProcessOwner Existing(AppGroup group) =>
        new(group.Id, group.Name, group.Publisher, group.IconPath, group.IsWindows, group, null);

    private static ProcessOwner New(string id, string name, string? publisher, string? icon, AppSource? source) =>
        new(id, name, publisher, icon, false, null, source);
}
