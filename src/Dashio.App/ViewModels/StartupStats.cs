using Dashio.App.Services;
using Dashio.Core.Models;

namespace Dashio.App.ViewModels;

/// <summary>One kind of autostart item with how many are on, for the "What starts" panel.</summary>
public sealed record KindBreakdown(string Glyph, string Name, string Explanation, string OnText, string TotalText)
{
    public string AccessibleName => $"{Name}: {OnText}, {TotalText}";
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
                ItemText.KindGlyph(k.Key),
                ItemText.KindHeading(k.Key),
                ItemText.KindExplanation(k.Key),
                $"{k.Count(i => i.Item.Enabled)} on",
                $"of {k.Count()}"))
            .ToList());
}
