using Dashio.Core.Models;

namespace Dashio.App.Services;

/// <summary>One app, whichever way Dashio knows it: it starts things, it is installed, or it is running.</summary>
/// <param name="Group">Set when the app has items that start by themselves.</param>
/// <param name="Installed">Set when Windows lists the app as installed.</param>
public sealed record AppEntry(
    string Id, string Name, string? Publisher, string? IconPath, AppGroup? Group, InstalledAppView? Installed, bool IsWindows)
{
    /// <summary>Real apps first, then the vendor leftovers, the unmatched items, and Windows itself.</summary>
    public int Rank => Group switch
    {
        { IsWindows: true } => 3,
        { IsUnmatched: true } => 2,
        { IsVendorBucket: true } => 1,
        _ => IsWindows ? 3 : 0,
    };

    /// <summary>
    /// Not an app a person would look for: a runtime, a driver or a codec that starts nothing, or
    /// a program that is running without being installed. The Apps page leaves these out unless
    /// asked; Processes and the search still show them.
    /// </summary>
    public bool IsComponent => Group is not { Items.Count: > 0 } && (Installed is null || Installed.App.IsComponent);
}

/// <summary>Every app on this PC, joined from the scan, the installed list and what is running.</summary>
public static class AppCatalog
{
    public static List<AppEntry> All(bool includeWindows)
    {
        var byId = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in AppServices.State.Groups)
            byId[group.Id] = new AppEntry(group.Id, group.Name, group.Publisher, group.IconPath, group, null, group.IsWindows);

        foreach (var app in AppServices.Inventory.Apps)
        {
            var owner = app.App.Owner;
            var icon = owner.IconPath ?? app.App.Programs.FirstOrDefault();
            byId[app.Id] = byId.TryGetValue(app.Id, out var known)
                ? known with { Installed = app, IconPath = known.IconPath ?? icon }
                : new AppEntry(app.Id, owner.Name, owner.Publisher, icon, null, app, owner.IsWindows);
        }

        foreach (var usage in AppServices.Monitor.Latest?.Apps ?? [])
        {
            var owner = usage.Owner;
            byId.TryAdd(usage.GroupId, new AppEntry(
                usage.GroupId, owner.Name, owner.Publisher, owner.IconPath, null, null, owner.IsWindows));
        }

        return byId.Values.Where(a => includeWindows || !a.IsWindows).ToList();
    }
}
