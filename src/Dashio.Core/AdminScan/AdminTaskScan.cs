using System.Security;
using Dashio.Core.Collectors;
using Dashio.Core.Models;
using Dashio.Core.Scanning;
using Microsoft.Win32;

namespace Dashio.Core.AdminScan;

/// <summary>Reads the registry's own list of scheduled tasks. Needs administrator rights.</summary>
public static class TaskCacheReader
{
    public const string TreePath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree";

    /// <summary>Full task paths such as <c>\Microsoft\Windows\Defrag\ScheduledDefrag</c>.</summary>
    public static IReadOnlyList<string> ReadTaskPaths()
    {
        var paths = new List<string>();
        using var tree = Registry.LocalMachine.OpenSubKey(TreePath);
        if (tree is not null)
            Walk(tree, "", paths);
        return paths;
    }

    private static void Walk(RegistryKey key, string path, List<string> paths)
    {
        foreach (var name in key.GetSubKeyNames())
        {
            try
            {
                using var sub = key.OpenSubKey(name);
                if (sub is null)
                    continue;
                var subPath = $@"{path}\{name}";
                // A task has an Id; a folder does not.
                if (sub.GetValue("Id") is string)
                    paths.Add(subPath);
                Walk(sub, subPath, paths);
            }
            catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }
    }
}

public static class AdminTaskScan
{
    /// <summary>
    /// Every scheduled task as an administrator sees it, plus tasks that exist in the registry but
    /// that Task Scheduler does not return. Must run elevated.
    /// </summary>
    public static IReadOnlyList<AutostartItem> Run(SystemScanner scanner)
    {
        var items = new ScheduledTaskCollector().Collect().Select(scanner.Finish).ToList();
        var known = items.Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var path in TaskCacheReader.ReadTaskPaths())
        {
            var id = ScheduledTaskCollector.IdFor(path);
            if (!known.Add(id))
                continue;

            var slash = path.LastIndexOf('\\');
            items.Add(scanner.Finish(new AutostartItem
            {
                Id = id,
                Kind = AutostartKind.ScheduledTask,
                Name = path[(slash + 1)..],
                DisplayName = path[(slash + 1)..],
                Description = "Registered in the task registry but not returned by Task Scheduler.",
                Scope = ItemScope.Machine,
                Enabled = true,
                Trigger = StartTrigger.Schedule,
                RequiresAdmin = true,
                Location = path[..(slash + 1)],
                IsHiddenTask = true,
            }));
        }
        return items;
    }

    /// <summary>The tasks the admin scan found that the normal scan did not.</summary>
    public static IReadOnlyList<AutostartItem> NewItems(
        IEnumerable<AutostartItem> known, IEnumerable<AutostartItem> adminView)
    {
        var knownIds = known.Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return adminView.Where(i => !knownIds.Contains(i.Id))
            .Select(i => i with { VisibleOnlyWithAdmin = true })
            .ToList();
    }
}
