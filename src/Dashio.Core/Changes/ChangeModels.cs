using Dashio.Core.Models;

namespace Dashio.Core.Changes;

public enum ChangeAction
{
    Disable,
    Enable,
}

/// <summary>
/// The part of an item's state that a change touches and an undo restores.
/// <paramref name="StartType"/> and <paramref name="Running"/> apply to services only;
/// a null <paramref name="Running"/> in a target means "leave it as it is".
/// </summary>
public sealed record ItemState(bool Enabled, ServiceStartType? StartType = null, bool? Running = null)
{
    public static ItemState Of(AutostartItem item) => new(item.Enabled, item.ServiceStartType, item.IsRunning);

    /// <summary>Whether <paramref name="actual"/> is what a change to this target should have produced.</summary>
    public bool IsSatisfiedBy(ItemState actual) =>
        actual.Enabled == Enabled && (StartType is null || actual.StartType == StartType);
}

/// <summary>What the helper is asked to do: bring one item to a target state.</summary>
public sealed record ChangeRequest(string ItemId, ItemState Target);

public sealed record ChangeResult(string ItemId, bool Success, string? Error = null, bool AccessDenied = false);

/// <summary>A change the user has queued.</summary>
public sealed record PlannedChange(
    AutostartItem Item, string AppName, ChangeAction Action, ItemState Target, Guid? UndoOf = null);

public enum JournalResult
{
    Applied,
    Failed,
    Cancelled,
}

public sealed record ChangeOutcome(PlannedChange Change, JournalResult Result, string? Error, AutostartItem? Current);

public sealed record BatchOutcome(Guid BatchId, IReadOnlyList<ChangeOutcome> Outcomes, bool AdminPromptCancelled)
{
    public int AppliedCount => Outcomes.Count(o => o.Result == JournalResult.Applied);
    public int FailedCount => Outcomes.Count(o => o.Result == JournalResult.Failed);
}
