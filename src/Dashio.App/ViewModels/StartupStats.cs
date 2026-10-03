using Dashio.App.Services;
using Dashio.Core.Models;

namespace Dashio.App.ViewModels;

/// <summary>One kind of autostart item with how many are on, for the "What starts" panel.</summary>
public sealed record KindBreakdown(AutostartKind Kind, string Glyph, string Name, string Explanation, int On, int Total)
{
    public string CountText => $"{On} of {Total} on";
    public double Percent => Total == 0 ? 0 : 100.0 * On / Total;
    public string AccessibleName => $"{Name}: {CountText}. {Explanation} Show them.";
}

/// <summary>The headline numbers about what starts by itself on this PC.</summary>
public sealed record StartupStats(int AtStartup, int NotInTaskManager, int RunningServices, IReadOnlyList<KindBreakdown> Kinds)
{
    /// <summary>The groups the user has chosen to see: everything, or everything but Windows itself.</summary>
    public static List<AppGroup> ShownGroups() => AppServices.State.Groups
        .Where(g => AppServices.Settings.ShowWindowsComponents || !g.IsWindows)
        .ToList();

    public static StartupStats Of(IReadOnlyList<AppGroup> groups) => new(
        groups.Count(g => g.StartsWithWindowsCount > 0),
        groups.Count(g => g.HiddenFromTaskManager),
        groups.Sum(g => g.RunningCount),
        groups.SelectMany(g => g.Items)
            .GroupBy(i => i.Item.Kind)
            .OrderBy(k => k.Key)
            .Select(k => new KindBreakdown(
                k.Key,
                ItemText.KindGlyph(k.Key),
                ItemText.KindHeading(k.Key),
                ItemText.KindExplanation(k.Key),
                k.Count(i => i.Item.Enabled),
                k.Count()))
            .ToList());
}
