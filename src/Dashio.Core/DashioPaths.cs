namespace Dashio.Core;

/// <summary>Where Dashio keeps its own files.</summary>
public static class DashioPaths
{
    /// <summary>Set this environment variable to keep the data somewhere else, as the UI tests do.</summary>
    public const string DataFolderVariable = "DASHIO_DATA_DIR";

    public static string DataFolder =>
        Environment.GetEnvironmentVariable(DataFolderVariable) is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dashio");

    public static string Journal => Path.Combine(DataFolder, "journal.jsonl");
    public static string Settings => Path.Combine(DataFolder, "settings.json");
    public static string Requests => Path.Combine(DataFolder, "requests");
    public static string Sizes => Path.Combine(DataFolder, "sizes.json");
    public static string Sightings => Path.Combine(DataFolder, "sightings.json");

    /// <summary>The last reading of a drive's folders, kept so the Storage page has something to show at once.</summary>
    public static string DriveReading(char letter) => Path.Combine(DataFolder, "drives", $"drive-{char.ToUpperInvariant(letter)}.bin");

    /// <summary>Uninstalls that were started and whose end has not been seen yet.</summary>
    public static string PendingUninstalls => Path.Combine(DataFolder, "pending-uninstalls.json");

    /// <summary>Which startup items have been seen before, and when each first appeared.</summary>
    public static string SeenItems => Path.Combine(DataFolder, "seen-items.json");
}
