using System.Runtime.InteropServices;
using System.Text;

namespace Dashio.Core.Processes;

/// <summary>
/// Reads every process and the machine's counters in one call, without opening any process,
/// so it works without administrator rights even for services that run as SYSTEM.
/// </summary>
public sealed unsafe class ProcessSampler
{
    private const int SystemProcessInformation = 5;
    private const int SystemProcessIdInformation = 0x58;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    // Offsets into SYSTEM_PROCESS_INFORMATION on 64-bit Windows.
    private const int OffsetNext = 0;
    private const int OffsetPrivateWorkingSet = 8;
    private const int OffsetCreateTime = 32;
    private const int OffsetUserTime = 40;
    private const int OffsetKernelTime = 48;
    private const int OffsetNameLength = 56;
    private const int OffsetNameBuffer = 64;
    private const int OffsetProcessId = 80;
    private const int OffsetParentProcessId = 88;

    private readonly object _gate = new();
    private byte[] _buffer = new byte[1 << 20];
    private Dictionary<string, string>? _devices;

    public SystemSample Sample()
    {
        lock (_gate)
        {
            var processes = ReadProcesses();

            var memory = new MEMORYSTATUSEX { dwLength = (uint)sizeof(MEMORYSTATUSEX) };
            GlobalMemoryStatusEx(ref memory);
            GetSystemTimes(out var idle, out var kernel, out var user);

            return new SystemSample(
                processes, (long)memory.ullTotalPhys, (long)memory.ullAvailPhys, idle, kernel, user);
        }
    }

    private List<ProcessSample> ReadProcesses()
    {
        while (true)
        {
            fixed (byte* start = _buffer)
            {
                var status = NtQuerySystemInformation(SystemProcessInformation, start, _buffer.Length, out var needed);
                if (status == StatusInfoLengthMismatch)
                {
                    _buffer = new byte[Math.Max(needed, _buffer.Length) + (64 << 10)];
                    continue;
                }
                if (status != 0)
                    return [];

                var processes = new List<ProcessSample>(512);
                var entry = start;
                while (true)
                {
                    var pid = (int)*(long*)(entry + OffsetProcessId);
                    // Process 0 is the idle counter, not a program.
                    if (pid != 0)
                    {
                        var nameLength = *(ushort*)(entry + OffsetNameLength);
                        var nameBuffer = *(char**)(entry + OffsetNameBuffer);
                        var name = nameBuffer is null || nameLength == 0 ? "System" : new string(nameBuffer, 0, nameLength / 2);
                        processes.Add(new ProcessSample(
                            pid,
                            name,
                            *(long*)(entry + OffsetPrivateWorkingSet),
                            *(long*)(entry + OffsetUserTime) + *(long*)(entry + OffsetKernelTime),
                            *(long*)(entry + OffsetCreateTime),
                            (int)*(long*)(entry + OffsetParentProcessId)));
                    }

                    var next = *(uint*)(entry + OffsetNext);
                    if (next == 0)
                        break;
                    entry += next;
                }
                return processes;
            }
        }
    }

    /// <summary>The full path of the file a process runs, or null when Windows does not say (a few kernel processes).</summary>
    public string? ImagePath(int pid)
    {
        const int capacity = 2048;
        var text = stackalloc char[capacity];
        var info = new SYSTEM_PROCESS_ID_INFORMATION
        {
            ProcessId = pid,
            ImageName = new UNICODE_STRING { MaximumLength = capacity * sizeof(char), Buffer = text },
        };
        var status = NtQuerySystemInformation(
            SystemProcessIdInformation, (byte*)&info, sizeof(SYSTEM_PROCESS_ID_INFORMATION), out _);
        if (status != 0 || info.ImageName.Length == 0)
            return null;
        return ToDrivePath(new string(text, 0, info.ImageName.Length / 2));
    }

    /// <summary>"\Device\HarddiskVolume3\Windows\x.exe" → "C:\Windows\x.exe".</summary>
    private string? ToDrivePath(string devicePath)
    {
        const string network = @"\Device\Mup\";
        if (devicePath.StartsWith(network, StringComparison.OrdinalIgnoreCase))
            return @"\\" + devicePath[network.Length..];

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var devices = _devices ??= ReadDevices();
            foreach (var (device, drive) in devices)
            {
                if (devicePath.StartsWith(device + "\\", StringComparison.OrdinalIgnoreCase))
                    return drive + devicePath[device.Length..];
            }
            // A drive that was plugged in after the first lookup.
            _devices = null;
        }
        return null;
    }

    private static Dictionary<string, string> ReadDevices()
    {
        var devices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var target = new StringBuilder(512);
        foreach (var root in Environment.GetLogicalDrives())
        {
            var drive = root.TrimEnd('\\');
            if (QueryDosDevice(drive, target, target.Capacity) > 0)
                devices[target.ToString()] = drive;
        }
        return devices;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public char* Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_PROCESS_ID_INFORMATION
    {
        public nint ProcessId;
        public UNICODE_STRING ImageName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int informationClass, byte* information, int length, out int returnLength);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int QueryDosDevice(string device, StringBuilder target, int capacity);
}
