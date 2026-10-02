using System.Xml.Linq;
using Dashio.Core.Models;
using Microsoft.Win32;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace Dashio.Core.Collectors;

/// <summary>Store packages for the current user, and the startup tasks their manifests declare.</summary>
public sealed class PackageCollector : IItemCollector
{
    public const string StateRoot =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";

    // Values of the State entry under StateRoot\<family>\<taskId>.
    public const int StateDisabled = 0;
    public const int StateDisabledByUser = 1;
    public const int StateEnabled = 2;
    public const int StateDisabledByPolicy = 3;
    public const int StateEnabledByPolicy = 4;

    public sealed record Result(IReadOnlyList<AppSource> Sources, IReadOnlyList<AutostartItem> Items);

    public string Name => "Store packages";

    public static string IdFor(string familyName, string taskId) => $"packagedtask:user:{familyName}!{taskId}";

    public IReadOnlyList<AutostartItem> Collect() => Scan().Items;

    public Result Scan()
    {
        var sources = new List<AppSource>();
        var items = new List<AutostartItem>();

        foreach (var package in new PackageManager().FindPackagesForUser(string.Empty))
        {
            try
            {
                if (package.IsFramework || package.IsResourcePackage || package.IsBundle)
                    continue;

                var family = package.Id.FamilyName;
                var location = package.InstalledPath;
                var name = Safe(() => package.DisplayName);
                if (string.IsNullOrWhiteSpace(name))
                    name = package.Id.Name;

                var manifest = string.IsNullOrEmpty(location) ? null : LoadManifest(location);
                sources.Add(new AppSource
                {
                    Kind = AppSourceKind.StorePackage,
                    Id = $"pkg:{family}",
                    Name = name,
                    Publisher = Safe(() => package.PublisherDisplayName),
                    InstallLocation = location,
                    IconPath = Safe(() => package.Logo?.LocalPath),
                    PackageFamilyName = family,
                    IsSystem = package.SignatureKind == PackageSignatureKind.System,
                    InstalledOn = SafeDate(() => package.InstalledDate),
                    AppIds = manifest is null ? [] : ReadApps(manifest).Select(a => $"{family}!{a.Id}").ToList(),
                    AppPrograms = manifest is null
                        ? []
                        : ReadApps(manifest).Where(a => a.Executable is not null)
                            .Select(a => Path.Combine(location!, a.Executable!)).ToList(),
                });

                if (manifest is not null)
                    items.AddRange(ReadStartupTasks(family, name, location!, manifest));
            }
            catch (Exception)
            {
                // A broken package registration must not hide the others.
            }
        }
        return new Result(sources, items);
    }

    private static string? Safe(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static DateTimeOffset? SafeDate(Func<DateTimeOffset> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static XDocument? LoadManifest(string location)
    {
        var manifestPath = Path.Combine(location, "AppxManifest.xml");
        try
        {
            return File.Exists(manifestPath) ? XDocument.Load(manifestPath) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>The apps a person can open: those with an entry in the Start menu's app list.</summary>
    private static List<(string Id, string? Executable)> ReadApps(XDocument manifest) => manifest.Descendants()
        .Where(e => e.Name.LocalName == "Application")
        .Where(e => !e.Descendants().Any(v =>
            v.Name.LocalName == "VisualElements" &&
            string.Equals((string?)v.Attribute("AppListEntry"), "none", StringComparison.OrdinalIgnoreCase)))
        .Select(e => (Id: (string?)e.Attribute("Id"), Executable: (string?)e.Attribute("Executable")))
        .Where(a => !string.IsNullOrEmpty(a.Id))
        .Select(a => (a.Id!, string.IsNullOrEmpty(a.Executable) ? null : a.Executable))
        .ToList();

    private static IEnumerable<AutostartItem> ReadStartupTasks(
        string family, string packageName, string location, XDocument manifest)
    {
        var extensions = manifest.Descendants().Where(e =>
            e.Name.LocalName == "Extension" && (string?)e.Attribute("Category") == "windows.startupTask");

        foreach (var extension in extensions)
        {
            var task = extension.Elements().FirstOrDefault(e => e.Name.LocalName == "StartupTask");
            var taskId = (string?)task?.Attribute("TaskId");
            if (string.IsNullOrEmpty(taskId))
                continue;

            var application = extension.Ancestors().FirstOrDefault(e => e.Name.LocalName == "Application");
            var executable = (string?)extension.Attribute("Executable") ?? (string?)application?.Attribute("Executable");
            var enabledByDefault = string.Equals((string?)task!.Attribute("Enabled"), "true", StringComparison.OrdinalIgnoreCase);

            yield return new AutostartItem
            {
                Id = IdFor(family, taskId),
                Kind = AutostartKind.PackagedStartupTask,
                Name = taskId,
                DisplayName = packageName,
                Command = executable,
                TargetPath = string.IsNullOrEmpty(executable) ? null : Path.Combine(location, executable),
                Scope = ItemScope.User,
                Enabled = ReadEnabled(family, taskId, enabledByDefault),
                Trigger = StartTrigger.Logon,
                RequiresAdmin = false,
                Location = $@"HKCU\{StateRoot}\{family}\{taskId}",
                PackageFamilyName = family,
            };
        }
    }

    private static bool ReadEnabled(string family, string taskId, bool enabledByDefault)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{StateRoot}\{family}\{taskId}");
            return key?.GetValue("State") is int state
                ? state is StateEnabled or StateEnabledByPolicy
                : enabledByDefault;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return enabledByDefault;
        }
    }
}
