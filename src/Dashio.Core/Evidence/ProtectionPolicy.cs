using Dashio.Core.Models;

namespace Dashio.Core.Evidence;

/// <summary>Decides which items are Windows components. Those are read-only everywhere, including in the helper.</summary>
public static class ProtectionPolicy
{
    private const string WindowsTaskFolder = @"\Microsoft\Windows\";

    public static bool IsProtected(AutostartItem item)
    {
        if (item.Kind == AutostartKind.ScheduledTask &&
            (item.Location ?? "").StartsWith(WindowsTaskFolder, StringComparison.OrdinalIgnoreCase))
            return true;

        // Judged by the file that actually runs. A third-party script launched through cmd.exe
        // has no trusted Windows target, so it is never protected just because its host is.
        return item.TargetPath is not null && item.Evidence?.IsWindowsComponent == true;
    }
}
