using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Dashio.Core.Inventory;

/// <summary>Shortcuts in the Start menu, for this user and for everyone.</summary>
public static class StartMenuCollector
{
    public static IReadOnlyList<Shortcut> Collect()
    {
        var shortcuts = new List<Shortcut>();
        var type = Type.GetTypeFromProgID("WScript.Shell");
        if (type is null)
            return shortcuts;

        dynamic shell;
        try
        {
            shell = Activator.CreateInstance(type)!;
        }
        catch (Exception)
        {
            return shortcuts;
        }

        var menus = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        };
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var menu in menus.Where(m => m.Length > 0 && Directory.Exists(m)))
        {
            foreach (var link in Directory.EnumerateFiles(menu, "*.lnk", options))
            {
                try
                {
                    string target = shell.CreateShortcut(link).TargetPath;
                    if (!string.IsNullOrWhiteSpace(target))
                        shortcuts.Add(new Shortcut(Path.GetFileNameWithoutExtension(link), link, target));
                }
                catch (Exception)
                {
                    // A broken shortcut opens nothing.
                }
            }
        }
        return shortcuts;
    }
}

/// <summary>
/// Windows' own record of what was launched from Start, the taskbar and Explorer. Readable
/// without admin rights, but some PCs have stopped writing it, so check <see cref="IsCurrent"/>.
/// </summary>
public static partial class UserAssistReader
{
    private const string Root = @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist";
    private const int EntrySize = 72;
    private const int LastRunOffset = 60;

    /// <summary>A record this fresh shows Windows is still writing the list.</summary>
    private static readonly TimeSpan Fresh = TimeSpan.FromDays(14);

    [GeneratedRegex(@"^\{([0-9A-Fa-f-]{36})\}\\")]
    private static partial Regex KnownFolderPrefix();

    public static IReadOnlyList<LaunchRecord> Read()
    {
        var records = new List<LaunchRecord>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(Root);
            if (root is null)
                return records;
            foreach (var guid in root.GetSubKeyNames())
            {
                using var count = root.OpenSubKey($@"{guid}\Count");
                if (count is null)
                    continue;
                foreach (var name in count.GetValueNames())
                {
                    if (count.GetValue(name) is byte[] { Length: >= EntrySize } data &&
                        Decode(name, BitConverter.ToInt64(data, LastRunOffset)) is { } record)
                        records.Add(record);
                }
            }
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        return records;
    }

    public static bool IsCurrent(IReadOnlyList<LaunchRecord> records, DateTimeOffset now) =>
        records.Count > 0 && now - records.Max(r => r.LastRun) <= Fresh;

    /// <summary>Names are stored with each letter moved 13 places, and folders as a known-folder id.</summary>
    internal static LaunchRecord? Decode(string storedName, long lastRunFileTime)
    {
        // Entries that were only ever shown, never launched, carry no time.
        if (lastRunFileTime <= 0)
            return null;

        var name = Rot13(storedName);
        var folder = KnownFolderPrefix().Match(name);
        if (folder.Success && Guid.TryParse(folder.Groups[1].Value, out var id) && KnownFolder(id) is { } path)
            name = Path.Combine(path, name[folder.Length..]);

        DateTimeOffset lastRun;
        try
        {
            lastRun = DateTimeOffset.FromFileTime(lastRunFileTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        return new LaunchRecord(name, lastRun);
    }

    internal static string Rot13(string text) => string.Create(text.Length, text, (span, source) =>
    {
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            span[i] = c switch
            {
                >= 'a' and <= 'z' => (char)('a' + (c - 'a' + 13) % 26),
                >= 'A' and <= 'Z' => (char)('A' + (c - 'A' + 13) % 26),
                _ => c,
            };
        }
    });

    private static string? KnownFolder(Guid id)
    {
        if (SHGetKnownFolderPath(id, 0, 0, out var pointer) != 0)
            return null;
        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, nint token, out nint path);
}

/// <summary>
/// The Prefetch folder: one file per program Windows has run lately, last written when it last ran.
/// Needs administrator rights, so only the helper calls it.
/// </summary>
public static partial class PrefetchReader
{
    [GeneratedRegex(@"^(.+)-[0-9A-F]{8}\.pf$", RegexOptions.IgnoreCase)]
    private static partial Regex FileName();

    public static IReadOnlyList<PrefetchEntry> Read()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");
        var files = new DirectoryInfo(folder).EnumerateFiles("*.pf")
            .Select(f => (f.Name, Time: new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero)));
        return Summarise(files);
    }

    /// <summary>A program can have several files (one per folder it ran from); the latest one counts.</summary>
    public static IReadOnlyList<PrefetchEntry> Summarise(IEnumerable<(string FileName, DateTimeOffset LastWrite)> files) => files
        .Select(f => (Match: FileName().Match(f.FileName), f.LastWrite))
        .Where(f => f.Match.Success)
        .GroupBy(f => f.Match.Groups[1].Value.ToUpperInvariant())
        .Select(g => new PrefetchEntry(g.Key, g.Max(f => f.LastWrite)))
        .OrderBy(e => e.ExeName, StringComparer.Ordinal)
        .ToList();

    /// <summary>Prefetch keeps at most 29 characters of a program's file name, in capitals.</summary>
    public static string NameFor(string exePath)
    {
        var name = Path.GetFileName(exePath).ToUpperInvariant();
        return name.Length > 29 ? name[..29] : name;
    }
}

/// <summary>Folders where apps keep data outside their install folder.</summary>
public static class DataFolderCollector
{
    /// <summary>Shared by every app, so they belong to none.</summary>
    private static readonly HashSet<string> Shared = new(StringComparer.OrdinalIgnoreCase)
    {
        "Temp", "Packages", "Programs", "Package Cache", "CrashDumps", "D3DSCache", "Application Data",
        "History", "Temporary Internet Files", "Desktop", "Documents", "Start Menu", "Templates",
    };

    public static IReadOnlyList<string> Roots { get; } = new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    }.Where(r => r.Length > 0).ToList();

    public static string PackageDataRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");

    /// <summary>The top-level folders under each root, without links and without the shared ones.</summary>
    public static IReadOnlyList<string> TopFolders() => Roots.SelectMany(Subfolders)
        .Where(f => !Shared.Contains(Path.GetFileName(f)))
        .ToList();

    public static IReadOnlyList<string> Subfolders(string folder)
    {
        try
        {
            return new FileSystemEnumerable<string>(
                folder,
                (ref FileSystemEntry entry) => entry.ToFullPath(),
                new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) => entry.IsDirectory,
            }.ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>Adds up the files in a folder. Links are skipped, so nothing is counted twice or followed elsewhere.</summary>
public static class FolderSizer
{
    public static long Measure(string folder, CancellationToken cancellation = default)
    {
        if (!Directory.Exists(folder))
            return 0;

        var sizes = new FileSystemEnumerable<long>(
            folder,
            (ref FileSystemEntry entry) => entry.Length,
            new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            })
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory,
        };

        long total = 0;
        var seen = 0;
        try
        {
            foreach (var size in sizes)
            {
                total += size;
                if (++seen % 2000 == 0)
                    cancellation.ThrowIfCancellationRequested();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A folder that vanished or locked part-way still gives the part that was read.
        }
        return total;
    }
}
