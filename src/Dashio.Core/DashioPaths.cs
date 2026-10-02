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
}
