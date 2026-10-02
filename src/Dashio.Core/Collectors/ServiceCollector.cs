using System.Security;
using System.ServiceProcess;
using Dashio.Core.Models;
using Dashio.Core.Parsing;
using Microsoft.Win32;

namespace Dashio.Core.Collectors;

/// <summary>Win32 services. Kernel drivers are left out.</summary>
public sealed class ServiceCollector : IItemCollector
{
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";

    private const int Win32ServiceMask = 0x30;      // own process | share process
    private const int UserServiceFlag = 0x40;
    private const int UserServiceInstanceFlag = 0x80;

    public string Name => "Services";

    public static string IdFor(string serviceName) => $"service:machine:{serviceName}";

    public IReadOnlyList<AutostartItem> Collect()
    {
        var known = new Dictionary<string, (string DisplayName, bool Running)>(StringComparer.OrdinalIgnoreCase);
        foreach (var controller in ServiceController.GetServices())
        {
            using (controller)
            {
                try
                {
                    known[controller.ServiceName] =
                        (controller.DisplayName, controller.Status == ServiceControllerStatus.Running);
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        var items = new List<AutostartItem>();
        using var root = Registry.LocalMachine.OpenSubKey(ServicesKey);
        if (root is null)
            return items;

        foreach (var name in root.GetSubKeyNames())
        {
            if (!known.TryGetValue(name, out var scm))
                continue;
            try
            {
                using var key = root.OpenSubKey(name);
                if (key is not null && Read(root, name, key, scm.DisplayName, scm.Running) is { } item)
                    items.Add(item);
            }
            catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }
        return items;
    }

    private static AutostartItem? Read(
        RegistryKey services, string name, RegistryKey key, string displayName, bool running)
    {
        if (key.GetValue("Type") is not int type || (type & Win32ServiceMask) == 0)
            return null;
        // A per-user service template is not a service itself; its per-session instances are.
        if ((type & UserServiceFlag) != 0 && (type & UserServiceInstanceFlag) == 0)
            return null;

        var start = key.GetValue("Start") as int? ?? 3;
        var delayed = key.GetValue("DelayedAutostart") as int? == 1;
        var startType = start switch
        {
            2 when delayed => ServiceStartType.AutomaticDelayed,
            2 => ServiceStartType.Automatic,
            4 => ServiceStartType.Disabled,
            _ => ServiceStartType.Manual,
        };

        var command = key.GetValue("ImagePath") as string;
        var resolved = CommandLineParser.Resolve(command);
        var target = resolved.TargetOrPassiveHost;
        if (resolved.HostPath is not null &&
            Path.GetFileNameWithoutExtension(resolved.HostPath).Equals("svchost", StringComparison.OrdinalIgnoreCase))
        {
            // A per-user instance ("CDPUserSvc_1a2b3c") keeps its DLL on the template ("CDPUserSvc").
            var isInstance = (type & UserServiceInstanceFlag) != 0;
            target = ReadServiceDll(key)
                     ?? (isInstance ? ReadTemplateServiceDll(services, name) : null)
                     ?? resolved.HostPath;
        }

        return new AutostartItem
        {
            Id = IdFor(name),
            Kind = AutostartKind.Service,
            Name = name,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? name : displayName,
            Description = NativeStrings.Resolve(key.GetValue("Description") as string),
            Command = command,
            TargetPath = target,
            HostPath = resolved.HostPath,
            Scope = ItemScope.Machine,
            Enabled = startType != ServiceStartType.Disabled,
            Trigger = startType is ServiceStartType.Automatic or ServiceStartType.AutomaticDelayed
                ? StartTrigger.Boot
                : StartTrigger.OnDemand,
            IsRunning = running,
            RequiresAdmin = true,
            ServiceStartType = startType,
            Location = $@"HKLM\{ServicesKey}\{name}",
        };
    }

    private static string? ReadTemplateServiceDll(RegistryKey services, string instanceName)
    {
        var underscore = instanceName.LastIndexOf('_');
        if (underscore <= 0)
            return null;
        using var template = services.OpenSubKey(instanceName[..underscore]);
        return template is null ? null : ReadServiceDll(template);
    }

    private static string? ReadServiceDll(RegistryKey serviceKey)
    {
        using var parameters = serviceKey.OpenSubKey("Parameters");
        var dll = parameters?.GetValue("ServiceDll") as string ?? serviceKey.GetValue("ServiceDll") as string;
        return string.IsNullOrWhiteSpace(dll) ? null : CommandLineParser.Resolve($"\"{dll}\"").TargetPath;
    }
}
