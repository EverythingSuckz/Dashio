using System.Text.Json;
using Microsoft.UI.Xaml;

namespace Dashio.App.Services;

/// <summary>The user's preferences, kept as a small JSON file next to the change log.</summary>
public sealed class SettingsStore
{
    private sealed record Model(string? Theme, bool ShowWindowsComponents, bool AppsAsGrid = false);

    private static string FilePath => Dashio.Core.DashioPaths.Settings;

    public ElementTheme Theme { get; set; } = ElementTheme.Default;
    public bool ShowWindowsComponents { get; set; }
    public bool AppsAsGrid { get; set; }

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
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Model(Theme.ToString(), ShowWindowsComponents, AppsAsGrid)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
