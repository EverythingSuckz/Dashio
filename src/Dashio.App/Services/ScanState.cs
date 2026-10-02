using CommunityToolkit.Mvvm.ComponentModel;
using Dashio.Core.AdminScan;
using Dashio.Core.Journal;
using Dashio.Core.Models;

namespace Dashio.App.Services;

/// <summary>What the last scan found, grouped into apps. Pages rebuild from this when it changes.</summary>
public sealed partial class ScanState : ObservableObject
{
    private List<AutostartItem> _items = [];
    private IReadOnlyList<AppSource> _sources = [];

    [ObservableProperty]
    public partial bool IsScanning { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    public bool HasScanned { get; private set; }
    public IReadOnlyList<AppGroup> Groups { get; private set; } = [];
    public IReadOnlyList<CollectorError> Errors { get; private set; } = [];
    public IReadOnlyList<JournalEntry> Journal { get; private set; } = [];
    public int JournalSkippedLines { get; private set; }
    public bool AdminScanDone { get; private set; }

    /// <summary>Raised on the UI thread after the groups or the journal change.</summary>
    public event EventHandler? Changed;

    public async Task RefreshAsync()
    {
        if (IsScanning)
            return;

        IsScanning = true;
        StatusText = "Looking at what starts with Windows…";
        try
        {
            var progress = new Progress<string>(text => StatusText = text + "…");
            var snapshot = await AppServices.Scanner.ScanAsync(progress);
            var journal = await Task.Run(AppServices.Journal.Load);

            _items = snapshot.Items.ToList();
            _sources = snapshot.Sources;
            Errors = snapshot.Errors;
            Journal = journal.Entries;
            JournalSkippedLines = journal.SkippedLines;
            AdminScanDone = false;
            await RegroupAsync();
            HasScanned = true;
        }
        catch (Exception e)
        {
            Errors = [new CollectorError("Scan", e.Message)];
        }
        finally
        {
            IsScanning = false;
            StatusText = "";
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Swaps in items that were just re-read from Windows, without a full rescan.</summary>
    public async Task ReplaceItemsAsync(IEnumerable<AutostartItem> updated)
    {
        var byId = updated.ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase);
        _items = _items.Select(i => byId.TryGetValue(i.Id, out var fresh)
            ? fresh with { VisibleOnlyWithAdmin = i.VisibleOnlyWithAdmin, IsHiddenTask = i.IsHiddenTask }
            : i).ToList();

        var journal = await Task.Run(AppServices.Journal.Load);
        Journal = journal.Entries;
        JournalSkippedLines = journal.SkippedLines;
        await RegroupAsync();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Asks the elevated helper for every scheduled task and adds the ones this scan could not see.</summary>
    /// <returns>A sentence describing the result, and whether it is an error.</returns>
    public async Task<(string Message, bool IsError)> RunAdminScanAsync()
    {
        var result = await AppServices.Helper.ScanTasksAsync();
        if (result.Cancelled)
            return ("The administrator prompt was cancelled. Nothing was scanned.", false);
        if (result.Error is not null)
            return ($"The admin scan failed: {result.Error}", true);

        var added = AdminTaskScan.NewItems(_items, result.Items);
        _items.AddRange(added);
        AdminScanDone = true;
        await RegroupAsync();
        Changed?.Invoke(this, EventArgs.Empty);

        if (added.Count == 0)
            return ("The admin scan found no tasks beyond the ones already listed.", false);

        var hidden = added.Count(i => i.IsHiddenTask);
        var message = $"The admin scan found {added.Count} more scheduled task{(added.Count == 1 ? "" : "s")}" +
            (hidden > 0 ? $", {hidden} of them hidden from Task Scheduler." : ".");

        // Without this the number in the message looks far larger than what appears in the list.
        var partOfWindows = added.Count(i => i.IsProtected);
        if (partOfWindows > 0 && !AppServices.Settings.ShowWindowsComponents)
            message += $" {partOfWindows} belong to Windows and stay out of the list unless you choose View, Show Windows components.";
        return (message, false);
    }

    private async Task RegroupAsync()
    {
        var items = _items;
        var sources = _sources;
        Groups = await Task.Run(() => AppServices.Engine.Group(items, sources));
        AppServices.Monitor.UseScan(Groups, sources);
    }

    /// <summary>A group from the scan, or an app that is only known because it is running.</summary>
    public AppGroup? FindGroup(string groupId) =>
        Groups.FirstOrDefault(g => g.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase))
        ?? AppServices.Monitor.FindGroup(groupId);

    public AppGroup? GroupOfItem(string itemId) =>
        Groups.FirstOrDefault(g => g.Items.Any(i => i.Item.Id.Equals(itemId, StringComparison.OrdinalIgnoreCase)));
}
