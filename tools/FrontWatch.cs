using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

/// <summary>
/// Watches, while the UI tests run, that the test copy of Dashio never comes to the front and
/// never shows a window on the screen. The tests must not interrupt whoever is using the PC.
/// </summary>
public static class FrontWatch
{
    private delegate bool EnumProc(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    private static volatile bool _stop;
    private static Thread _thread;

    /// <summary>The process to watch. Zero until the app has been started.</summary>
    public static int ProcessId;

    /// <summary>What the tests are doing right now, so a problem can be traced to a step.</summary>
    public static string Step = "start";

    public static int MillisecondsInFront;
    public static int MillisecondsOnScreen;
    public static readonly List<string> Problems = new List<string>();

    public static void Start()
    {
        _stop = false;
        _thread = new Thread(Watch) { IsBackground = true };
        _thread.Start();
    }

    public static void Stop()
    {
        _stop = true;
        if (_thread != null)
            _thread.Join();
    }

    private static void Watch()
    {
        const int Interval = 20;
        var wasInFront = false;
        var wasOnScreen = false;
        while (!_stop)
        {
            if (ProcessId != 0)
            {
                uint owner;
                GetWindowThreadProcessId(GetForegroundWindow(), out owner);
                var inFront = owner == ProcessId;
                if (inFront)
                    MillisecondsInFront += Interval;
                if (inFront && !wasInFront)
                    Note("came to the front during: " + Step);
                wasInFront = inFront;

                var shown = OnScreen();
                if (shown != null)
                    MillisecondsOnScreen += Interval;
                if (shown != null && !wasOnScreen)
                    Note("showed a window on the screen (" + shown + ") during: " + Step);
                wasOnScreen = shown != null;
            }
            Thread.Sleep(Interval);
        }
    }

    private static void Note(string problem)
    {
        lock (Problems)
            Problems.Add(problem);
    }

    /// <summary>The class of a visible window of the process that lies within the screens, or null.</summary>
    private static string OnScreen()
    {
        // The virtual screen: every monitor together.
        int left = GetSystemMetrics(76), top = GetSystemMetrics(77);
        int right = left + GetSystemMetrics(78), bottom = top + GetSystemMetrics(79);
        string found = null;
        EnumWindows((window, _) =>
        {
            uint owner;
            GetWindowThreadProcessId(window, out owner);
            Rect rect;
            if (owner != ProcessId || !IsWindowVisible(window) || !GetWindowRect(window, out rect))
                return true;
            // A cloaked window is laid out and rendered but not drawn on the screen.
            int cloaked;
            if (DwmGetWindowAttribute(window, 14, out cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;
            var empty = rect.Right <= rect.Left || rect.Bottom <= rect.Top;
            if (empty || rect.Right <= left || rect.Left >= right || rect.Bottom <= top || rect.Top >= bottom)
                return true;
            var name = new StringBuilder(256);
            GetClassName(window, name, name.Capacity);
            found = name.ToString();
            return false;
        }, IntPtr.Zero);
        return found;
    }
}
