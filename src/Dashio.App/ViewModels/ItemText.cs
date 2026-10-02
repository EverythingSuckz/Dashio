using Dashio.Core.Changes;
using Dashio.Core.Models;

namespace Dashio.App.ViewModels;

/// <summary>The words and glyphs used for autostart items, in one place so every page agrees.</summary>
public static class ItemText
{
    public static string KindName(AutostartKind kind) => kind switch
    {
        AutostartKind.Service => "Service",
        AutostartKind.ScheduledTask => "Scheduled task",
        AutostartKind.RunKey => "Startup entry",
        AutostartKind.StartupFolder => "Startup folder",
        AutostartKind.PackagedStartupTask => "Store app startup",
        AutostartKind.Process => "Running program",
        _ => kind.ToString(),
    };

    public static string KindHeading(AutostartKind kind) => kind switch
    {
        AutostartKind.Service => "Services",
        AutostartKind.ScheduledTask => "Scheduled tasks",
        AutostartKind.RunKey => "Startup entries",
        AutostartKind.StartupFolder => "Startup folder",
        AutostartKind.PackagedStartupTask => "Store app startup",
        _ => kind.ToString(),
    };

    /// <summary>"3 services", "1 scheduled task".</summary>
    public static string KindCount(AutostartKind kind, int count) => kind switch
    {
        AutostartKind.Service => Plural(count, "service"),
        AutostartKind.ScheduledTask => Plural(count, "scheduled task"),
        AutostartKind.RunKey => Plural(count, "startup entry", "startup entries"),
        AutostartKind.StartupFolder => Plural(count, "Startup folder item"),
        AutostartKind.PackagedStartupTask => Plural(count, "Store app startup task"),
        _ => Plural(count, "item"),
    };

    /// <summary>What this kind of item is, in a sentence.</summary>
    public static string KindExplanation(AutostartKind kind) => kind switch
    {
        AutostartKind.Service => "Background programs Windows starts and keeps running, with no window.",
        AutostartKind.ScheduledTask => "Programs Windows runs at a set time or event, such as sign-in.",
        AutostartKind.RunKey => "Programs listed in the registry to start when you sign in.",
        AutostartKind.StartupFolder => "Shortcuts in the Startup folder, opened when you sign in.",
        AutostartKind.PackagedStartupTask => "Store apps that asked to start when you sign in.",
        _ => "",
    };

    /// <summary>Segoe Fluent Icons glyphs.</summary>
    public static string KindGlyph(AutostartKind kind) => kind switch
    {
        AutostartKind.Service => "\uE9F5",              // Processing
        AutostartKind.ScheduledTask => "\uE823",        // Recent
        AutostartKind.RunKey => "\uE7E8",               // PowerButton
        AutostartKind.StartupFolder => "\uE8B7",        // Folder
        AutostartKind.PackagedStartupTask => "\uE7B8",  // Package
        _ => "\uE74C",
    };

    public static string Trigger(AutostartItem item) => item.Trigger switch
    {
        StartTrigger.Boot => "Starts with Windows",
        StartTrigger.Logon => "Starts when you sign in",
        StartTrigger.Schedule => "Runs on a schedule",
        _ => "Starts when needed",
    };

    public static string TriggerGlyph(AutostartItem item) => item.Trigger switch
    {
        StartTrigger.Boot => "\uE7E8",      // PowerButton
        StartTrigger.Logon => "\uE77B",     // Contact
        StartTrigger.Schedule => "\uE787",  // Calendar
        _ => "\uE768",                      // Play
    };

    public static string StartType(ServiceStartType? startType) => startType switch
    {
        ServiceStartType.Automatic => "Automatic",
        ServiceStartType.AutomaticDelayed => "Automatic (delayed)",
        ServiceStartType.Manual => "Manual",
        ServiceStartType.Disabled => "Disabled",
        _ => "",
    };

    /// <summary>"Turn off Killer Analytics Service" and similar, for the review dialog and history.</summary>
    public static string Describe(PlannedChange change)
    {
        var verb = Verb(change.Action);
        return $"{char.ToUpperInvariant(verb[0])}{verb[1..]} {change.Item.DisplayName}";
    }

    /// <summary>"turn off", "stop": what was asked for.</summary>
    public static string Verb(ChangeAction action) => action switch
    {
        ChangeAction.Disable => "turn off",
        ChangeAction.Enable => "turn on",
        ChangeAction.Stop => "stop",
        ChangeAction.Start => "start",
        _ => "end",
    };

    /// <summary>"Turned off", "Stopped": what happened.</summary>
    public static string Done(ChangeAction action) => action switch
    {
        ChangeAction.Disable => "Turned off",
        ChangeAction.Enable => "Turned on",
        ChangeAction.Stop => "Stopped",
        ChangeAction.Start => "Started",
        _ => "Ended",
    };

    public static string ActionGlyph(ChangeAction action) => action switch
    {
        ChangeAction.Disable => "\uE7E8",   // PowerButton
        ChangeAction.Stop => "\uE71A",      // Stop
        ChangeAction.End => "\uE711",       // Cancel
        _ => "\uE768",                      // Play
    };

    public static string Plural(int count, string singular, string? plural = null) =>
        count == 1 ? $"1 {singular}" : $"{count} {plural ?? singular + "s"}";
}
