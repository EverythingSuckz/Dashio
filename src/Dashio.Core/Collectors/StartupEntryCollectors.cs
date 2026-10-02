using System.Security;
using Dashio.Core.Models;
using Dashio.Core.Parsing;
using Microsoft.Win32;

namespace Dashio.Core.Collectors;

/// <summary>Where a Run key lives and where Task Manager keeps its on/off flag.</summary>
public sealed record RunKeyLocation(RegistryHive Hive, RegistryView View, string ApprovedName, ItemScope Scope)
{
    public const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    public static readonly IReadOnlyList<RunKeyLocation> All =
    [
        new(RegistryHive.CurrentUser, RegistryView.Default, "Run", ItemScope.User),
        new(RegistryHive.LocalMachine, RegistryView.Registry64, "Run", ItemScope.Machine),
        new(RegistryHive.LocalMachine, RegistryView.Registry32, "Run32", ItemScope.Machine),
    ];

    public string HiveName => Hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM";

    public string DisplayPath => View == RegistryView.Registry32
        ? $@"{HiveName}\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"
        : $@"{HiveName}\{RunPath}";
}

internal static class StartupApproved
{
    /// <summary>The flags always live in the 64-bit view, even for 32-bit Run entries.</summary>
    public static byte[]? Read(RegistryHive hive, string approvedName, string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey($@"{RunKeyLocation.ApprovedPath}\{approvedName}");
            return key?.GetValue(valueName) as byte[];
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}

/// <summary>Entries under the <c>Run</c> registry keys.</summary>
public sealed class RunKeyCollector : IItemCollector
{
    public string Name => "Run keys";

    public static string IdFor(ItemScope scope, string approvedName, string valueName) =>
        $"runkey:{scope.ToString().ToLowerInvariant()}:{approvedName}:{valueName}";

    public IReadOnlyList<AutostartItem> Collect()
    {
        var items = new List<AutostartItem>();
        foreach (var location in RunKeyLocation.All)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(location.Hive, location.View);
                using var key = baseKey.OpenSubKey(RunKeyLocation.RunPath);
                if (key is null)
                    continue;

                foreach (var valueName in key.GetValueNames())
                {
                    if (valueName.Length == 0 || key.GetValue(valueName) is not string command)
                        continue;

                    var resolved = CommandLineParser.Resolve(command);
                    items.Add(new AutostartItem
                    {
                        Id = IdFor(location.Scope, location.ApprovedName, valueName),
                        Kind = AutostartKind.RunKey,
                        Name = valueName,
                        DisplayName = valueName,
                        Command = command,
                        TargetPath = resolved.TargetOrPassiveHost,
                        HostPath = resolved.HostPath,
                        Scope = location.Scope,
                        Enabled = StartupApprovedCodec.IsEnabled(
                            StartupApproved.Read(location.Hive, location.ApprovedName, valueName)),
                        Trigger = StartTrigger.Logon,
                        RequiresAdmin = location.Scope == ItemScope.Machine,
                        Location = location.DisplayPath,
                    });
                }
            }
            catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }
        return items;
    }
}

/// <summary>Shortcuts and files in the user and all-users Startup folders.</summary>
public sealed class StartupFolderCollector : IItemCollector
{
    public const string ApprovedName = "StartupFolder";

    public string Name => "Startup folders";

    public static string IdFor(ItemScope scope, string fileName) =>
        $"startupfolder:{scope.ToString().ToLowerInvariant()}:{fileName}";

    public static string FolderFor(ItemScope scope) => Environment.GetFolderPath(
        scope == ItemScope.User ? Environment.SpecialFolder.Startup : Environment.SpecialFolder.CommonStartup);

    public IReadOnlyList<AutostartItem> Collect()
    {
        var items = new List<AutostartItem>();
        foreach (var scope in new[] { ItemScope.User, ItemScope.Machine })
        {
            var folder = FolderFor(scope);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                continue;

            var hive = scope == ItemScope.User ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file);
                if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                    continue;

                string? command = file, target = file, host = null;
                if (Path.GetExtension(file).Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
                    ShortcutReader.Read(file) is { } shortcut)
                {
                    command = string.IsNullOrEmpty(shortcut.Arguments)
                        ? shortcut.Target
                        : $"\"{shortcut.Target}\" {shortcut.Arguments}";
                    // The shortcut's target is one path even when it contains spaces.
                    var resolved = CommandLineParser.Resolve($"\"{shortcut.Target}\" {shortcut.Arguments}");
                    target = resolved.TargetOrPassiveHost;
                    host = resolved.HostPath;
                }

                items.Add(new AutostartItem
                {
                    Id = IdFor(scope, fileName),
                    Kind = AutostartKind.StartupFolder,
                    Name = fileName,
                    DisplayName = Path.GetFileNameWithoutExtension(fileName),
                    Command = command,
                    TargetPath = target,
                    HostPath = host,
                    Scope = scope,
                    Enabled = StartupApprovedCodec.IsEnabled(StartupApproved.Read(hive, ApprovedName, fileName)),
                    Trigger = StartTrigger.Logon,
                    RequiresAdmin = scope == ItemScope.Machine,
                    Location = folder,
                });
            }
        }
        return items;
    }
}

internal static class ShortcutReader
{
    public sealed record Shortcut(string Target, string? Arguments);

    public static Shortcut? Read(string lnkPath)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null)
                return null;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic link = shell.CreateShortcut(lnkPath);
            string target = link.TargetPath;
            string arguments = link.Arguments;
            return string.IsNullOrWhiteSpace(target) ? null : new Shortcut(target, arguments);
        }
        catch (Exception)
        {
            // A broken shortcut is listed by its own path instead.
            return null;
        }
    }
}
