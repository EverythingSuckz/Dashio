namespace Dashio.App.ViewModels;

/// <summary>How memory and processor figures are written, so every page agrees.</summary>
public static class UsageText
{
    private const double Megabyte = 1024 * 1024;
    private const double Gigabyte = Megabyte * 1024;

    public static string Memory(long bytes) => bytes switch
    {
        >= (long)Gigabyte * 100 => $"{bytes / Gigabyte:F0} GB",
        >= (long)Gigabyte => $"{bytes / Gigabyte:F1} GB",
        >= (long)Megabyte => $"{bytes / Megabyte:F0} MB",
        > 0 => "<1 MB",
        _ => "0 MB",
    };

    public static string Cpu(double percent) => percent switch
    {
        >= 9.95 => $"{percent:F0}%",
        >= 0.05 => $"{percent:F1}%",
        _ => "0%",
    };

    /// <summary>
    /// How strongly to tint a cell for a share of the whole (0 to 1). Small shares still show,
    /// because most of what runs uses little and the differences among them matter.
    /// </summary>
    public static double Heat(double share)
    {
        // High contrast keeps text on the plain background.
        if (Accessibility.HighContrast)
            return 0;
        return share <= 0 ? 0.03 : Math.Min(0.6, 0.03 + 0.6 * Math.Sqrt(Math.Min(1, share)));
    }

    private static readonly Windows.UI.ViewManagement.AccessibilitySettings Accessibility = new();

    public static string Processes(int count) => ItemText.Plural(count, "process", "processes");
}
