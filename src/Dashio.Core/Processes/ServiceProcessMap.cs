using System.Runtime.InteropServices;

namespace Dashio.Core.Processes;

/// <summary>Which running services live in which process.</summary>
public static unsafe class ServiceProcessMap
{
    private const uint ScManagerEnumerateService = 0x0004;
    private const int ScEnumProcessInfo = 0;
    private const uint ServiceWin32 = 0x30;
    private const uint ServiceActive = 1;
    private const int ErrorMoreData = 234;

    // ENUM_SERVICE_STATUS_PROCESSW on 64-bit Windows: two string pointers, then SERVICE_STATUS_PROCESS.
    private const int EntrySize = 56;
    private const int OffsetServiceName = 0;
    private const int OffsetProcessId = 44;

    /// <summary>Process id to the names of the services running in it. Empty when the service manager cannot be read.</summary>
    public static IReadOnlyDictionary<int, IReadOnlyList<string>> Read()
    {
        var result = new Dictionary<int, List<string>>();
        var manager = OpenSCManager(null, null, ScManagerEnumerateService);
        if (manager == 0)
            return new Dictionary<int, IReadOnlyList<string>>();

        try
        {
            var buffer = new byte[256 << 10];
            uint resume = 0;
            while (true)
            {
                fixed (byte* start = buffer)
                {
                    var ok = EnumServicesStatusEx(
                        manager, ScEnumProcessInfo, ServiceWin32, ServiceActive,
                        start, (uint)buffer.Length, out _, out var returned, ref resume, null);
                    var more = !ok && Marshal.GetLastWin32Error() == ErrorMoreData;
                    if (!ok && !more)
                        break;

                    for (var i = 0; i < returned; i++)
                    {
                        var entry = start + i * EntrySize;
                        var pid = *(int*)(entry + OffsetProcessId);
                        var name = *(char**)(entry + OffsetServiceName);
                        if (pid <= 0 || name is null)
                            continue;
                        if (!result.TryGetValue(pid, out var services))
                            result[pid] = services = [];
                        services.Add(new string(name));
                    }

                    if (!more)
                        break;
                }
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }

        return result.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", EntryPoint = "EnumServicesStatusExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumServicesStatusEx(
        nint manager, int infoLevel, uint serviceType, uint serviceState,
        byte* services, uint size, out uint bytesNeeded, out uint returned, ref uint resumeHandle, string? group);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(nint handle);
}
