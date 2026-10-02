using System.Diagnostics;
using System.Runtime.InteropServices;
using Dashio.Core.Evidence;

namespace Dashio.Core.Processes;

/// <summary>A process to end. The start time tells it from a later process that reuses the id.</summary>
public sealed record EndRequest(int Pid, long StartTicks);

public sealed record EndResult(int Pid, bool Success, string? Error = null, bool AccessDenied = false);

/// <summary>
/// Ends processes: first asks their windows to close, then terminates what is still running.
/// It never ends a Windows component, and never trusts a process id without its start time.
/// </summary>
public sealed class ProcessEnder
{
    /// <summary>How long windows get to close by themselves.</summary>
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(4);

    private static readonly TimeSpan TerminateWait = TimeSpan.FromSeconds(5);

    private const uint ProcessTerminate = 0x0001;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const int ErrorAccessDenied = 5;
    private const uint WaitObject0 = 0;
    private const uint WmClose = 0x0010;
    private const uint GwOwner = 4;
    private const int ProcessBreakOnTermination = 29;

    private readonly ProcessSampler _sampler;
    private readonly FileEvidenceReader _evidence;

    public ProcessEnder(ProcessSampler? sampler = null, FileEvidenceReader? evidence = null)
    {
        _sampler = sampler ?? new ProcessSampler();
        _evidence = evidence ?? new FileEvidenceReader();
    }

    private sealed class Target(EndRequest request)
    {
        public EndRequest Request { get; } = request;
        public nint Handle;
        public EndResult? Result;
    }

    /// <summary>Why this process must not be ended, or null when it may be.</summary>
    public string? Refusal(EndRequest request)
    {
        if (request.Pid <= 4)
            return "This is part of Windows and cannot be ended.";
        if (request.Pid == Environment.ProcessId)
            return "Dashio cannot end itself.";
        if (_sampler.ImagePath(request.Pid) is not { } path)
            return "Windows does not say which program this is, so it is left alone.";
        return _evidence.Read(path).IsWindowsComponent ? "This is part of Windows and cannot be ended." : null;
    }

    public IReadOnlyList<EndResult> EndAll(IReadOnlyList<EndRequest> requests, TimeSpan? grace = null)
    {
        var running = _sampler.Sample().Processes.ToDictionary(p => p.Pid);
        var targets = requests.Select(r => new Target(r)).ToList();
        try
        {
            foreach (var target in targets)
                Open(target, running);

            var open = targets.Where(t => t.Handle != 0).ToList();
            if (open.Count > 0 && AskWindowsToClose(open.Select(t => t.Request.Pid).ToHashSet()))
                WaitForAll(open, grace ?? DefaultGrace);

            foreach (var target in open.Where(t => !HasExited(t.Handle)))
            {
                if (!TerminateProcess(target.Handle, 1) && !HasExited(target.Handle))
                {
                    var error = Marshal.GetLastWin32Error();
                    target.Result = error == ErrorAccessDenied
                        ? new EndResult(target.Request.Pid, false, "Administrator rights are needed.", AccessDenied: true)
                        : new EndResult(target.Request.Pid, false, new System.ComponentModel.Win32Exception(error).Message);
                }
            }

            WaitForAll(open.Where(t => t.Result is null).ToList(), TerminateWait);
            foreach (var target in open.Where(t => t.Result is null))
            {
                target.Result = HasExited(target.Handle)
                    ? new EndResult(target.Request.Pid, true)
                    : new EndResult(target.Request.Pid, false, "It is still running.");
            }
        }
        finally
        {
            foreach (var target in targets.Where(t => t.Handle != 0))
                CloseHandle(target.Handle);
        }
        return targets.Select(t => t.Result!).ToList();
    }

    private void Open(Target target, Dictionary<int, ProcessSample> running)
    {
        var request = target.Request;
        // A different start time means the id now belongs to another program: the target has gone.
        if (!running.TryGetValue(request.Pid, out var process) || process.StartTicks != request.StartTicks)
        {
            target.Result = new EndResult(request.Pid, true);
            return;
        }
        if (Refusal(request) is { } refusal)
        {
            target.Result = new EndResult(request.Pid, false, refusal);
            return;
        }

        var handle = OpenProcess(ProcessTerminate | Synchronize | ProcessQueryInformation, false, request.Pid);
        if (handle == 0)
            handle = OpenProcess(ProcessTerminate | Synchronize | ProcessQueryLimitedInformation, false, request.Pid);
        if (handle == 0)
        {
            var error = Marshal.GetLastWin32Error();
            target.Result = error == ErrorAccessDenied
                ? new EndResult(request.Pid, false, "Administrator rights are needed.", AccessDenied: true)
                : new EndResult(request.Pid, true);     // No such process any more.
            return;
        }

        // Checked again on the handle, which cannot be swapped for another process.
        if (!GetProcessTimes(handle, out var created, out _, out _, out _) || created != request.StartTicks)
        {
            CloseHandle(handle);
            target.Result = new EndResult(request.Pid, true);
            return;
        }
        // Ending a process marked critical stops Windows with an error screen.
        if (NtQueryInformationProcess(handle, ProcessBreakOnTermination, out var critical, sizeof(int), out _) == 0 && critical != 0)
        {
            CloseHandle(handle);
            target.Result = new EndResult(request.Pid, false, "Windows depends on this program, so it cannot be ended.");
            return;
        }
        target.Handle = handle;
    }

    /// <summary>Sends a close request to every visible main window of the given processes.</summary>
    private static bool AskWindowsToClose(HashSet<int> pids)
    {
        var asked = false;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var pid);
            if (pids.Contains((int)pid) && IsWindowVisible(window) && GetWindow(window, GwOwner) == 0)
                asked |= PostMessage(window, WmClose, 0, 0);
            return true;
        }, 0);
        return asked;
    }

    private static void WaitForAll(List<Target> targets, TimeSpan limit)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < limit && targets.Any(t => !HasExited(t.Handle)))
            Thread.Sleep(100);
    }

    private static bool HasExited(nint handle) => WaitForSingleObject(handle, 0) == WaitObject0;

    private delegate bool EnumWindowsCallback(nint window, nint parameter);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(nint process, uint exitCode);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(nint process, out long created, out long exited, out long kernel, out long user);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(nint process, int informationClass, out int value, int length, out int returned);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint pid);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint relation);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
