using Dashio.Core.Attribution;
using Dashio.Core.Changes;
using Dashio.Core.Journal;
using Dashio.Core.Scanning;

namespace Dashio.App.Services;

/// <summary>The app's long-lived objects. Created once at launch.</summary>
public static class AppServices
{
    public static SettingsStore Settings { get; } = SettingsStore.Load();
    public static SystemScanner Scanner { get; } = new();
    public static AttributionEngine Engine { get; } = new();
    public static ChangeJournal Journal { get; } = new(ChangeJournal.DefaultPath);
    public static IHelperLauncher Helper { get; } = new HelperLauncher();
    public static ChangeCoordinator Coordinator { get; } =
        new(Scanner.Find, new ChangeExecutor().Apply, Helper, Journal);
    public static IconService Icons { get; } = new();
    public static ScanState State { get; } = new();
    public static PendingChanges Pending { get; } = new();
    public static ShellState Shell { get; } = new();
}
