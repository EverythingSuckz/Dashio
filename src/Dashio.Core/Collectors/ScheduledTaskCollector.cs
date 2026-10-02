using System.Security.Principal;
using Dashio.Core.Models;
using Dashio.Core.Parsing;
using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;
using ScheduledTask = Microsoft.Win32.TaskScheduler.Task;

namespace Dashio.Core.Collectors;

/// <summary>Scheduled tasks the current user is allowed to see.</summary>
public sealed class ScheduledTaskCollector : IItemCollector
{
    public string Name => "Scheduled tasks";

    public static string IdFor(string taskPath) => $"task:machine:{taskPath}";

    public IReadOnlyList<AutostartItem> Collect()
    {
        var items = new List<AutostartItem>();
        using var service = new TaskService();
        Walk(service.RootFolder, items, CurrentUserNames());
        return items;
    }

    private static HashSet<string> CurrentUserNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Environment.UserName };
        using var identity = WindowsIdentity.GetCurrent();
        names.Add(identity.Name);
        if (identity.User is { } sid)
            names.Add(sid.Value);
        return names;
    }

    private static void Walk(TaskFolder folder, List<AutostartItem> items, HashSet<string> currentUser)
    {
        try
        {
            foreach (var task in folder.GetTasks())
            {
                using (task)
                {
                    if (Read(task, currentUser) is { } item)
                        items.Add(item);
                }
            }
        }
        catch (Exception e) when (IsExpected(e))
        {
        }

        try
        {
            foreach (var sub in folder.SubFolders)
            {
                using (sub)
                    Walk(sub, items, currentUser);
            }
        }
        catch (Exception e) when (IsExpected(e))
        {
        }
    }

    private static bool IsExpected(Exception e) =>
        e is UnauthorizedAccessException or IOException or System.Runtime.InteropServices.COMException
            or InvalidOperationException or InvalidCastException;

    private static AutostartItem? Read(ScheduledTask task, HashSet<string> currentUser)
    {
        string path, name;
        bool enabled;
        try
        {
            path = task.Path;
            name = task.Name;
            enabled = task.Enabled;
        }
        catch (Exception e) when (IsExpected(e))
        {
            return null;
        }

        var folderPath = path[..(path.LastIndexOf('\\') + 1)];
        var trigger = StartTrigger.OnDemand;
        string? command = null, description = null, target = null, host = null;
        var requiresAdmin = true;

        try
        {
            var definition = task.Definition;
            trigger = Classify(definition.Triggers);
            description = NativeStrings.Resolve(definition.RegistrationInfo.Description);

            foreach (var action in definition.Actions)
            {
                if (action is ExecAction exec && !string.IsNullOrWhiteSpace(exec.Path))
                {
                    var exe = exec.Path.Trim();
                    if (!exe.StartsWith('"') && exe.Contains(' '))
                        exe = $"\"{exe}\"";
                    command = string.IsNullOrWhiteSpace(exec.Arguments) ? exe : $"{exe} {exec.Arguments}";
                    var resolved = CommandLineParser.Resolve(command);
                    target = resolved.TargetOrPassiveHost;
                    host = resolved.HostPath;
                    break;
                }
                if (action is ComHandlerAction com)
                {
                    command = $"COM handler {com.ClassId:B}";
                    target = ComServerPath(com.ClassId);
                    break;
                }
            }

            var principal = definition.Principal;
            requiresAdmin = !(principal.RunLevel == TaskRunLevel.LUA
                              && principal.UserId is { Length: > 0 } user
                              && (currentUser.Contains(user) || currentUser.Contains(user.Split('\\')[^1]))
                              && !folderPath.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (IsExpected(e))
        {
            // The task is listed even when its definition cannot be read.
        }

        return new AutostartItem
        {
            Id = IdFor(path),
            Kind = AutostartKind.ScheduledTask,
            Name = name,
            DisplayName = name,
            Description = description,
            Command = command,
            TargetPath = target,
            HostPath = host,
            Scope = ItemScope.Machine,
            Enabled = enabled,
            Trigger = trigger,
            RequiresAdmin = requiresAdmin,
            Location = folderPath,
        };
    }

    private static StartTrigger Classify(TriggerCollection triggers)
    {
        var result = StartTrigger.OnDemand;
        foreach (var trigger in triggers)
        {
            switch (trigger.TriggerType)
            {
                case TaskTriggerType.Boot:
                    return StartTrigger.Boot;
                case TaskTriggerType.Logon:
                    result = StartTrigger.Logon;
                    break;
                default:
                    if (result == StartTrigger.OnDemand)
                        result = StartTrigger.Schedule;
                    break;
            }
        }
        return result;
    }

    private static string? ComServerPath(Guid classId)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{classId:B}");
            if (key is null)
                return null;
            foreach (var serverKind in new[] { "InprocServer32", "LocalServer32" })
            {
                using var server = key.OpenSubKey(serverKind);
                if (server?.GetValue(null) is string value && value.Length > 0)
                    return CommandLineParser.Resolve(value).TargetPath;
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        return null;
    }
}
