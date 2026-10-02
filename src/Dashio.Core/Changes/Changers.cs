using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.ServiceProcess;
using Dashio.Core.Collectors;
using Dashio.Core.Models;
using Dashio.Core.Parsing;
using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;

namespace Dashio.Core.Changes;

/// <summary>Applies one change to Windows. Only switches things on and off; never deletes.</summary>
public sealed class ChangeExecutor
{
    private const int ErrorAccessDenied = 5;

    public ChangeResult Apply(AutostartItem item, ItemState target)
    {
        try
        {
            switch (item.Kind)
            {
                case AutostartKind.Service:
                    return ServiceChanger.Apply(item, target);
                case AutostartKind.ScheduledTask:
                    TaskChanger.Apply(item, target);
                    break;
                case AutostartKind.RunKey:
                case AutostartKind.StartupFolder:
                    StartupApprovedChanger.Apply(item, target);
                    break;
                case AutostartKind.PackagedStartupTask:
                    PackagedTaskChanger.Apply(item, target);
                    break;
                default:
                    return new ChangeResult(item.Id, false, $"Items of kind {item.Kind} cannot be changed.");
            }
            return new ChangeResult(item.Id, true);
        }
        catch (Exception e) when (IsAccessDenied(e))
        {
            return new ChangeResult(item.Id, false, "Administrator rights are needed.", AccessDenied: true);
        }
        catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException
                                      or COMException or ArgumentException or System.ServiceProcess.TimeoutException)
        {
            return new ChangeResult(item.Id, false, e.Message);
        }
    }

    private static bool IsAccessDenied(Exception e) => e switch
    {
        UnauthorizedAccessException or SecurityException => true,
        Win32Exception win32 => win32.NativeErrorCode == ErrorAccessDenied,
        COMException com => com.HResult == unchecked((int)0x80070005),
        InvalidOperationException { InnerException: Win32Exception inner } => inner.NativeErrorCode == ErrorAccessDenied,
        _ => false,
    };
}

internal static class ServiceChanger
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(15);

    public static ChangeResult Apply(AutostartItem item, ItemState target)
    {
        var startType = target.StartType ?? (target.Enabled ? ServiceStartType.Manual : ServiceStartType.Disabled);
        SetStartType(item.Name, startType);

        // The start type is what keeps the service from coming back; stopping it is best effort.
        try
        {
            using var controller = new ServiceController(item.Name);
            if (!target.Enabled || target.Running == false)
            {
                if (controller.Status is not (ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending))
                    controller.Stop();
                controller.WaitForStatus(ServiceControllerStatus.Stopped, StopTimeout);
            }
            else if (target.Running == true && controller.Status == ServiceControllerStatus.Stopped)
            {
                controller.Start();
            }
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or System.ServiceProcess.TimeoutException)
        {
            var verb = target.Enabled ? "started" : "stopped";
            return new ChangeResult(
                item.Id, true,
                $"The start type was changed, but the service could not be {verb}: {(e.InnerException ?? e).Message}");
        }
        return new ChangeResult(item.Id, true);
    }

    private static void SetStartType(string serviceName, ServiceStartType startType)
    {
        var manager = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (manager == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var service = OpenServiceW(manager, serviceName, SERVICE_CHANGE_CONFIG);
            if (service == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var native = startType switch
                {
                    ServiceStartType.Automatic or ServiceStartType.AutomaticDelayed => SERVICE_AUTO_START,
                    ServiceStartType.Disabled => SERVICE_DISABLED,
                    _ => SERVICE_DEMAND_START,
                };
                if (!ChangeServiceConfigW(
                        service, SERVICE_NO_CHANGE, native, SERVICE_NO_CHANGE,
                        null, null, IntPtr.Zero, null, null, null, null))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                if (native == SERVICE_AUTO_START)
                {
                    var delayed = new SERVICE_DELAYED_AUTO_START_INFO
                    {
                        fDelayedAutostart = startType == ServiceStartType.AutomaticDelayed,
                    };
                    // Some services reject the delayed flag; the start type itself is already set.
                    ChangeServiceConfig2W(service, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, ref delayed);
                }
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SERVICE_CHANGE_CONFIG = 0x0002;
    private const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;
    private const uint SERVICE_AUTO_START = 2;
    private const uint SERVICE_DEMAND_START = 3;
    private const uint SERVICE_DISABLED = 4;
    private const uint SERVICE_CONFIG_DELAYED_AUTO_START_INFO = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_DELAYED_AUTO_START_INFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fDelayedAutostart;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr OpenServiceW(IntPtr manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfigW(
        IntPtr service, uint serviceType, uint startType, uint errorControl, string? binaryPathName,
        string? loadOrderGroup, IntPtr tagId, string? dependencies, string? serviceStartName,
        string? password, string? displayName);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2W(
        IntPtr service, uint infoLevel, ref SERVICE_DELAYED_AUTO_START_INFO info);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}

internal static class TaskChanger
{
    public static void Apply(AutostartItem item, ItemState target)
    {
        using var service = new TaskService();
        using var task = service.GetTask(item.Location + item.Name)
                         ?? throw new InvalidOperationException("The scheduled task no longer exists.");
        task.Enabled = target.Enabled;
    }
}

/// <summary>Writes the same on/off flag Task Manager writes, so both tools agree.</summary>
internal static class StartupApprovedChanger
{
    public static void Apply(AutostartItem item, ItemState target)
    {
        var hive = item.Scope == ItemScope.User ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
        var approvedName = item.Kind == AutostartKind.StartupFolder
            ? StartupFolderCollector.ApprovedName
            : ApprovedNameFromId(item.Id);

        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.CreateSubKey($@"{RunKeyLocation.ApprovedPath}\{approvedName}", writable: true);
        key.SetValue(item.Name, StartupApprovedCodec.Encode(target.Enabled, DateTime.UtcNow), RegistryValueKind.Binary);
    }

    /// <summary>Run key ids are <c>runkey:{scope}:{Run|Run32}:{valueName}</c>.</summary>
    private static string ApprovedNameFromId(string id)
    {
        var parts = id.Split(':', 4);
        return parts.Length == 4 && parts[2].Equals("Run32", StringComparison.OrdinalIgnoreCase) ? "Run32" : "Run";
    }
}

internal static class PackagedTaskChanger
{
    public static void Apply(AutostartItem item, ItemState target)
    {
        if (item.PackageFamilyName is null)
            throw new InvalidOperationException("The startup task has no package.");

        using var key = Registry.CurrentUser.CreateSubKey(
            $@"{PackageCollector.StateRoot}\{item.PackageFamilyName}\{item.Name}", writable: true);
        if (key.GetValue("State") is int state &&
            state is PackageCollector.StateDisabledByPolicy or PackageCollector.StateEnabledByPolicy)
            throw new InvalidOperationException("This startup task is controlled by a policy.");

        key.SetValue(
            "State",
            target.Enabled ? PackageCollector.StateEnabled : PackageCollector.StateDisabledByUser,
            RegistryValueKind.DWord);
    }
}
