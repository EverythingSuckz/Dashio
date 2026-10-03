using Dashio.Core.Models;

namespace Dashio.Core.Processes;

/// <summary>One process as Windows reports it at one moment.</summary>
/// <param name="PrivateBytes">Private working set: the figure Task Manager shows as Memory.</param>
/// <param name="CpuTicks">Processor time used since the process started, in 100 ns units, summed over all cores.</param>
/// <param name="StartTicks">When it started. Together with the id this tells a process from a later one that reuses the id.</param>
/// <param name="ParentPid">The process that started it. That one may be gone, and its id may have been reused since.</param>
public sealed record ProcessSample(int Pid, string Name, long PrivateBytes, long CpuTicks, long StartTicks, int ParentPid = 0);

/// <summary>Every process, plus the machine's memory and processor counters, at one moment.</summary>
/// <param name="KernelTicks">Includes idle time, as Windows reports it.</param>
public sealed record SystemSample(
    IReadOnlyList<ProcessSample> Processes,
    long MemoryTotalBytes,
    long MemoryAvailableBytes,
    long IdleTicks,
    long KernelTicks,
    long UserTicks);

/// <summary>A running process with what is known about its file.</summary>
/// <param name="Services">Names of the services running inside it.</param>
public sealed record RunningProcess(int Pid, string Name, string? Path, IReadOnlyList<string> Services);

/// <summary>The app a process belongs to.</summary>
/// <param name="Group">The group from the scan, or null for an app that runs but starts nothing by itself.</param>
public sealed record ProcessOwner(
    string GroupId, string Name, string? Publisher, string? IconPath, bool IsWindows, AppGroup? Group, AppSource? Source);

public sealed record ProcessUsage(
    int Pid, long StartTicks, string Name, string? Path, IReadOnlyList<string> Services, long MemoryBytes, double CpuPercent);

/// <summary>What one app is using right now, summed over its processes.</summary>
public sealed record AppUsage(ProcessOwner Owner, long MemoryBytes, double CpuPercent, IReadOnlyList<ProcessUsage> Processes)
{
    public string GroupId => Owner.GroupId;
}

public sealed record SystemUsage(long MemoryUsedBytes, long MemoryTotalBytes, double CpuPercent)
{
    public double MemoryPercent => MemoryTotalBytes <= 0 ? 0 : 100.0 * MemoryUsedBytes / MemoryTotalBytes;
}

/// <param name="Apps">Empty until the first scan has grouped the apps.</param>
public sealed record UsageSnapshot(SystemUsage System, IReadOnlyList<AppUsage> Apps, DateTimeOffset TakenAt);
