using System.Text.RegularExpressions;

namespace Dashio.Core.Attribution;

/// <summary>
/// The folder that identifies the product a file belongs to, e.g.
/// <c>C:\Program Files\Intel\Driver and Support Assistant\x86\svc.exe</c> → vendor "Intel",
/// product "Driver and Support Assistant".
/// </summary>
public sealed partial record FolderKey(string Key, string Name, string? Vendor)
{
    private static readonly string[] ProgramRoots = Roots(
        Environment.GetEnvironmentVariable("ProgramW6432"),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

    private static readonly string LocalAppData = Root(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    private static readonly string RoamingAppData = Root(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
    private static readonly string WinDir = Root(Environment.GetFolderPath(Environment.SpecialFolder.Windows));

    private static string Root(string path) => path.TrimEnd('\\') + "\\";

    private static string[] Roots(params string?[] paths) =>
        paths.Where(p => !string.IsNullOrEmpty(p)).Select(p => Root(p!)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static readonly HashSet<string> GenericFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "bin32", "bin64", "binn", "program", "programs", "app", "apps", "application", "x64", "x86",
        "amd64", "arm64", "win32", "win64", "service", "services", "lib", "libs", "current", "resources",
        "common", "shared", "tools", "release", "debug", "target", "dist", "build", "binaries", "portal",
        "runtime", "system", "data", "plugins", "scripts", "updater", "update", "install", "installer",
        "framework", "prerequisites", "support", "helper", "helpers", "client", "server", "core", "main",
        "oem", "vendor", "vendors",
    };

    [GeneratedRegex(@"^(v|app-)?\d[\d._\-]*$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionLike();

    public static bool IsGeneric(string folder) => GenericFolders.Contains(folder) || VersionLike().IsMatch(folder);

    /// <param name="path">The file's full path.</param>
    /// <param name="isVendor">Whether a folder name is a known software vendor rather than a product.</param>
    public static FolderKey? For(string path, Func<string, bool> isVendor)
    {
        string? directory;
        try
        {
            directory = Path.GetDirectoryName(path);
        }
        catch (ArgumentException)
        {
            return null;
        }
        if (string.IsNullOrEmpty(directory))
            return null;
        directory += "\\";

        foreach (var root in ProgramRoots)
        {
            if (StartsWith(directory, root))
                return Under(root, directory, isVendor, skipFirst: "Common Files");
        }

        if (StartsWith(directory, LocalAppData))
        {
            var programs = LocalAppData + "Programs\\";
            if (StartsWith(directory, programs))
                return Under(programs, directory, isVendor);
            // App execution aliases are stubs; their folder says nothing about the app.
            if (StartsWith(directory, LocalAppData + "Microsoft\\WindowsApps\\"))
                return null;
            return Under(LocalAppData, directory, isVendor);
        }
        if (StartsWith(directory, RoamingAppData))
            return Under(RoamingAppData, directory, isVendor);

        if (StartsWith(directory, WinDir))
        {
            foreach (var sub in new[] { "System32\\drivers\\", "System32\\", "SysWOW64\\" })
            {
                if (StartsWith(directory, WinDir + sub))
                    return Under(WinDir + sub, directory, isVendor);
            }
            return Under(WinDir, directory, isVendor);
        }

        return Elsewhere(directory);
    }

    private static bool StartsWith(string path, string root) =>
        path.StartsWith(root, StringComparison.OrdinalIgnoreCase);

    private static FolderKey? Under(string root, string directory, Func<string, bool> isVendor, string? skipFirst = null)
    {
        var segments = directory[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var prefix = root;
        if (skipFirst is not null && segments.Length > 0 && segments[0].Equals(skipFirst, StringComparison.OrdinalIgnoreCase))
        {
            prefix += segments[0] + "\\";
            segments = segments[1..];
        }
        // A generic top folder ("OEM") names nothing; the product is one level down.
        while (segments.Length > 1 && IsGeneric(segments[0]))
        {
            prefix += segments[0] + "\\";
            segments = segments[1..];
        }
        if (segments.Length == 0)
            return null;

        var first = segments[0];
        if (segments.Length > 1 && !IsGeneric(segments[1]) && isVendor(first))
            return new FolderKey($"{prefix}{first}\\{segments[1]}".ToLowerInvariant(), segments[1], first);
        return new FolderKey($"{prefix}{first}".ToLowerInvariant(), first, null);
    }

    /// <summary>Outside the usual install roots: the nearest folder that is not a build or version folder.</summary>
    private static FolderKey? Elsewhere(string directory)
    {
        // A command that never expanded to a real path ("$env:SystemRoot\…") says nothing.
        if (directory.Length < 3 || directory[1] != ':' || directory[2] != '\\')
            return null;

        var current = directory.TrimEnd('\\');
        while (true)
        {
            var name = Path.GetFileName(current);
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(parent))
                return null;
            if (!IsGeneric(name))
                return new FolderKey(current.ToLowerInvariant(), name, null);
            current = parent;
        }
    }
}
