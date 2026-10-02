namespace Dashio.Core.Processes;

/// <summary>Turns two samples into percentages. Pure, so it can be tested with made-up samples.</summary>
public static class UsageCalculator
{
    /// <summary>Share of the whole machine's processor time that was not idle between the samples.</summary>
    public static double SystemCpuPercent(SystemSample? previous, SystemSample current)
    {
        var total = TotalTicks(previous, current);
        if (previous is null || total <= 0)
            return 0;
        var idle = current.IdleTicks - previous.IdleTicks;
        return Math.Clamp(100.0 * (total - idle) / total, 0, 100);
    }

    /// <summary>Each process's share of the whole machine's processor time between the samples, by process id.</summary>
    public static IReadOnlyDictionary<int, double> ProcessCpuPercent(SystemSample? previous, SystemSample current)
    {
        var result = new Dictionary<int, double>(current.Processes.Count);
        var total = TotalTicks(previous, current);
        if (previous is null || total <= 0)
            return result;

        var before = previous.Processes.ToDictionary(p => p.Pid);
        foreach (var process in current.Processes)
        {
            // A different start time means the id was reused by a new process; it has no earlier reading.
            if (!before.TryGetValue(process.Pid, out var earlier) || earlier.StartTicks != process.StartTicks)
                continue;
            var used = process.CpuTicks - earlier.CpuTicks;
            if (used > 0)
                result[process.Pid] = Math.Min(100.0, 100.0 * used / total);
        }
        return result;
    }

    private static long TotalTicks(SystemSample? previous, SystemSample current) => previous is null
        ? 0
        : (current.KernelTicks - previous.KernelTicks) + (current.UserTicks - previous.UserTicks);
}
