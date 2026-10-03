using Dashio.App.Services;
using Dashio.Core.Inventory;

namespace Dashio.App.ViewModels;

/// <summary>How sizes and "last opened" are written, so every page agrees.</summary>
public static class InstalledText
{
    public static string Size(InstalledAppView app)
    {
        if (app.TotalBytes == 0)
            return app.IsMeasuring ? "Measuring" : "0 MB";
        var size = UsageText.Memory(app.TotalBytes);
        // The figure Windows recorded at install time is rough, and nothing could be measured to check it.
        return app.IsReportedOnly ? $"about {size}" : size;
    }

    /// <summary>"app 1.2 GB, data 3.3 GB".</summary>
    public static string Breakdown(InstalledAppView app)
    {
        if (app.IsReportedOnly)
            return "as recorded by Windows";
        var parts = new List<string>();
        if (app.ProgramBytes > 0)
            parts.Add($"app {UsageText.Memory(app.ProgramBytes)}");
        if (app.DataBytes > 0)
            parts.Add($"data {UsageText.Memory(app.DataBytes)}");
        if (app.App.ReportedBytes > 0)
            parts.Add($"other parts about {UsageText.Memory(app.App.ReportedBytes)}");
        return string.Join(", ", parts);
    }

    public static string LastOpened(AppUsageHistory usage) => usage.Verdict switch
    {
        UsageVerdict.RunningNow => "Open now",
        UsageVerdict.NotOpenedLately when usage.LastOpened is { } last => $"Last opened {Ago(last)}",
        UsageVerdict.NotOpenedLately when usage.NotSince is { } since => $"Not since {since:d MMM} or earlier",
        _ when usage.LastOpened is { } last => $"Opened {Ago(last)}",
        UsageVerdict.Unknown => "No record",
        _ => "",
    };

    public static string Ago(DateTimeOffset time)
    {
        var days = (int)(DateTimeOffset.Now.Date - time.LocalDateTime.Date).TotalDays;
        return days switch
        {
            <= 0 => "today",
            1 => "yesterday",
            < 14 => $"{days} days ago",
            < 60 => $"{days / 7} weeks ago",
            < 365 => $"{days / 30} months ago",
            _ => $"on {time.LocalDateTime:d MMM yyyy}",
        };
    }
}

/// <summary>What "not opened lately" rests on, for the pages that list it.</summary>
public sealed record LastOpenedEvidence(string Title, string Text, bool NeedsAdminCheck)
{
    public const string AccessTitle = "Administrator access needed";
    public const string AccessText =
        "Windows only lets an administrator see which programs it has run lately. " +
        "Dashio needs that list to tell which apps you have not opened.";

    public static LastOpenedEvidence Of(InventoryState inventory)
    {
        // Nothing on this PC can vouch for an app not having been opened until the admin check is run.
        var needsCheck = inventory.CoversFrom is null && !inventory.HasAdminCheck;
        if (needsCheck)
            return new LastOpenedEvidence(AccessTitle, AccessText, true);
        if (inventory.CoversFrom is { } since)
        {
            return new LastOpenedEvidence(
                $"No sign of being opened since {since:d MMMM}",
                "Only apps with something to open are listed: drivers and runtimes are never called unused. " +
                "An app opened in a way Windows does not record can be missed, so check before removing anything.",
                needsCheck);
        }
        if (inventory.ReachesBackTo is { } reach)
        {
            return new LastOpenedEvidence(
                "The records are too short to say",
                $"Windows only remembers which programs ran since {reach:d MMMM} on this PC. " +
                "That is too short to call any app unused.",
                needsCheck);
        }
        return new LastOpenedEvidence(
            "Windows keeps no record on this PC",
            "Neither the list of apps you open nor the list of programs Windows has run is being kept, " +
            "so Dashio can only go by the apps it sees open while it is running.",
            false);
    }
}
