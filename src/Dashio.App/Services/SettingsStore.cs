using System.Text.Json;
using Microsoft.UI.Xaml;

namespace Dashio.App.Services;

/// <summary>The user's preferences, kept as a small JSON file next to the change log.</summary>
public sealed class SettingsStore
{
    private sealed record Model(
        string? Theme, bool ShowWindowsComponents, bool AppsAsGrid = false, int RefreshSeconds = DefaultRefreshSeconds);

    public const int DefaultRefreshSeconds = 2;

    /// <summary>The intervals offered in Settings. Zero pauses the live figures.</summary>
    public static readonly int[] RefreshChoices = [1, 2, 5, 10, 0];

    private static string FilePath => Dashio.Core.DashioPaths.Settings;

    public ElementTheme Theme { get; set; } = ElementTheme.Default;
    public bool ShowWindowsComponents { get; set; }
    public bool AppsAsGrid { get; set; }

    /// <summary>How often memory and processor figures are measured. Zero means paused.</summary>
    public int RefreshSeconds { get; set; } = DefaultRefreshSeconds;

    public event EventHandler? Changed;

    public static SettingsStore Load()
    {
        var store = new SettingsStore();
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<Model>(File.ReadAllText(FilePath)) is { } model)
            {
                store.Theme = Enum.TryParse<ElementTheme>(model.Theme, out var theme) ? theme : ElementTheme.Default;
                store.ShowWindowsComponents = model.ShowWindowsComponents;
                store.AppsAsGrid = model.AppsAsGrid;
                store.RefreshSeconds = RefreshChoices.Contains(model.RefreshSeconds)
                    ? model.RefreshSeconds
                    : DefaultRefreshSeconds;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable settings fall back to the defaults.
        }
        return store;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Model(Theme.ToString(), ShowWindowsComponents, AppsAsGrid, RefreshSeconds)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
