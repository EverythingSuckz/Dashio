using Dashio.Core.Changes;
using Dashio.Core.Journal;
using Dashio.Core.Models;

namespace Dashio.App.Services;

/// <summary>Changes the user has queued by flipping switches. Nothing here has been applied yet.</summary>
public sealed class PendingChanges
{
    private readonly Dictionary<string, PlannedChange> _changes = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? Changed;

    public int Count => _changes.Count;
    public bool NeedsAdmin => _changes.Values.Any(c => c.Item.RequiresAdmin);
    public IReadOnlyList<PlannedChange> All => _changes.Values
        .OrderBy(c => c.AppName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(c => c.Item.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>The on/off state the user has asked for, or null when nothing is queued for the item.</summary>
    public bool? Desired(string itemId) =>
        _changes.TryGetValue(itemId, out var change) ? change.Target.Enabled : null;

    /// <summary>Queues a change, or removes the queued one when the item is put back as it is now.</summary>
    public void SetDesired(AutostartItem item, string appName, bool on)
    {
        if (item.IsProtected || item.IsHiddenTask)
            return;

        var had = _changes.Remove(item.Id);
        if (on != item.Enabled)
        {
            _changes[item.Id] = on
                ? new PlannedChange(item, appName, ChangeAction.Enable,
                    ChangePlanner.EnableTarget(item, AppServices.State.Journal))
                : new PlannedChange(item, appName, ChangeAction.Disable, ChangePlanner.DisableTarget(item));
            Changed?.Invoke(this, EventArgs.Empty);
        }
        else if (had)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Remove(IEnumerable<string> itemIds)
    {
        var removed = false;
        foreach (var id in itemIds)
            removed |= _changes.Remove(id);
        if (removed)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        if (_changes.Count == 0)
            return;
        _changes.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
