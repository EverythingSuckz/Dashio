using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Dashio.Core.Models;
using Dashio.Core.Parsing;
using Microsoft.Win32;

namespace Dashio.Core.Collectors;

/// <summary>Apps &amp; Features entries from the three Uninstall registry keys.</summary>
public sealed class InstalledAppCollector
{
    private const string UninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly (RegistryHive Hive, RegistryView View, string Tag)[] Locations =
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64, "hklm64"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, "hklm32"),
        (RegistryHive.CurrentUser, RegistryView.Default, "hkcu"),
    ];

    public string Name => "Installed apps";

    public IReadOnlyList<AppSource> Collect()
    {
        var sources = new List<AppSource>();
        foreach (var (hive, view, tag) in Locations)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var root = baseKey.OpenSubKey(UninstallPath);
                if (root is null)
                    continue;

                foreach (var subName in root.GetSubKeyNames())
                {
                    using var key = root.OpenSubKey(subName);
                    if (key?.GetValue("DisplayName") is not string name || string.IsNullOrWhiteSpace(name))
                        continue;
                    // Patches and updates hang off a parent entry and are not apps themselves.
                    if (key.GetValue("ParentKeyName") is string)
                        continue;

                    var icon = StripIconIndex(key.GetValue("DisplayIcon") as string);
                    sources.Add(new AppSource
                    {
                        Kind = AppSourceKind.InstalledApp,
                        Id = $"app:{tag}:{subName}",
                        Name = name.Trim(),
                        Publisher = (key.GetValue("Publisher") as string)?.Trim(),
                        InstallLocation = InstallFolder(key, icon),
                        IconPath = icon,
                    });
                }
            }
            catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }
        return sources;
    }

    private static string? StripIconIndex(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon))
            return null;
        var path = displayIcon.Trim().Trim('"');
        var comma = path.LastIndexOf(',');
        if (comma > 0 && int.TryParse(path[(comma + 1)..].Trim(), out _))
            path = path[..comma];
        return path.Trim().Trim('"');
    }

    /// <summary>
    /// Most entries leave InstallLocation empty, so fall back to the folder of the icon or the uninstaller.
    /// </summary>
    private static string? InstallFolder(RegistryKey key, string? icon)
    {
        var declared = InstallFolders.Clean(key.GetValue("InstallLocation") as string);
        if (declared is not null)
            return declared;

        if (icon is not null && InstallFolders.Clean(SafeDirectory(icon)) is { } fromIcon)
            return fromIcon;

        var uninstaller = CommandLineParser.Resolve(key.GetValue("UninstallString") as string);
        if (uninstaller.HostPath is null && uninstaller.TargetPath is { } exe)
            return InstallFolders.Clean(SafeDirectory(exe));
        return null;
    }

    private static string? SafeDirectory(string path)
    {
        try
        {
            return Path.GetDirectoryName(path);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>Decides whether a folder is specific enough to identify one app.</summary>
public static class InstallFolders
{
    private static readonly string[] SharedRoots = BuildSharedRoots();

    private static string[] BuildSharedRoots()
    {
        var folders = new[]
        {
            Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86,
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.CommonProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments,
        };
        return folders.Select(Environment.GetFolderPath).Where(p => p.Length > 0)
            .Select(p => p.TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static readonly string WinDir =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

    /// <summary>Returns the folder without a trailing slash, or null when it is missing or shared by many apps.</summary>
    public static string? Clean(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return null;

        var path = folder.Trim().Trim('"').TrimEnd('\\', '/').Replace('/', '\\');
        if (path.Length < 4 || !path.Contains('\\'))
            return null;
        if (SharedRoots.Contains(path, StringComparer.OrdinalIgnoreCase))
            return null;
        // Installer caches hold icons and uninstallers for many products.
        if (path.StartsWith(WinDir, StringComparison.OrdinalIgnoreCase) ||
            path.Contains(@"\Package Cache", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(@"\Installer\", StringComparison.OrdinalIgnoreCase))
            return null;
        return path;
    }

    public static bool Contains(string folder, string filePath) =>
        filePath.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Driver packages that items point into, read from the driver store.</summary>
public sealed partial class DriverPackageCollector
{
    [GeneratedRegex(@"^(.*\\DriverStore\\FileRepository\\(([^\\]+?)\.inf_[^\\]+))\\", RegexOptions.IgnoreCase)]
    private static partial Regex DriverStoreFolder();

    public string Name => "Driver packages";

    /// <summary>The driver-store folder a file lives in, or null.</summary>
    public static (string Folder, string FolderName, string InfName)? Locate(string? path)
    {
        if (path is null)
            return null;
        var match = DriverStoreFolder().Match(path);
        return match.Success ? (match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value) : null;
    }

    public static string IdFor(string folderName) => $"drv:{folderName.ToLowerInvariant()}";

    public IReadOnlyList<AppSource> Collect(IEnumerable<string?> targetPaths)
    {
        var sources = new Dictionary<string, AppSource>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in targetPaths)
        {
            if (Locate(path) is not { } located || sources.ContainsKey(located.FolderName))
                continue;

            string? provider = null, description = null;
            try
            {
                var infPath = Path.Combine(located.Folder, located.InfName + ".inf");
                if (File.Exists(infPath))
                {
                    var info = InfParser.Parse(ReadText(infPath));
                    provider = info.Provider;
                    description = info.Description;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }

            sources[located.FolderName] = new AppSource
            {
                Kind = AppSourceKind.DriverPackage,
                Id = IdFor(located.FolderName),
                Name = description ?? located.InfName,
                Publisher = provider,
                InstallLocation = located.Folder,
            };
        }
        return sources.Values.ToList();
    }

    /// <summary>INF files are UTF-16 with a byte-order mark, or a legacy code page without one.</summary>
    private static string ReadText(string path)
    {
        using var reader = new StreamReader(path, Encoding.Latin1, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
