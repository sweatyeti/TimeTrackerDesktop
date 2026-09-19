namespace TimeTrackerDesktop.Domain;

/// <summary>What a soft-delete or restore request actually did.</summary>
public enum EntryVisibilityOutcome
{
    /// <summary>The entry was soft-deleted: kept in the snapshot, excluded from normal projections.</summary>
    Deleted,

    /// <summary>The entry was restored.</summary>
    Restored,

    /// <summary>No entry with that id exists in this session.</summary>
    EntryNotFound,

    /// <summary>Only completed, non-deleted entries can be deleted — TTC's <c>IsDeletableEntry</c>.</summary>
    NotDeletable,

    /// <summary>Only a soft-deleted entry can be restored.</summary>
    NotDeleted,
}

/// <summary>
/// The outcome of a soft delete or restore: the resulting state, what happened, and the entry it
/// touched.
///
/// "Delete" here is TTC's soft delete: the entry stays in the snapshot and stays restorable
/// (invariant 7), so nothing is ever destroyed. Refusals are named for the same reason the edit
/// results are — a silent no-op hides a UI that is offering an action it should not.
/// </summary>
public sealed record EntryVisibilityResult(
    SessionState State,
    EntryVisibilityOutcome Outcome,
    TimeEntry? Entry = null)
{
    /// <summary>True only when the entry's visibility actually changed.</summary>
    public bool Applied => Outcome is EntryVisibilityOutcome.Deleted or EntryVisibilityOutcome.Restored;

    /// <summary>True when the request was refused.</summary>
    public bool Rejected => !Applied;

    /// <summary>A short explanation suitable for a status line, a tooltip or a log.</summary>
    public string Reason => Outcome switch
    {
        EntryVisibilityOutcome.Deleted => "Entry deleted.",
        EntryVisibilityOutcome.Restored => "Entry restored.",
        EntryVisibilityOutcome.EntryNotFound => "No entry with that id in this session.",
        EntryVisibilityOutcome.NotDeletable => "Only completed, undeleted entries can be deleted.",
        EntryVisibilityOutcome.NotDeleted => "Only a deleted entry can be restored.",
        _ => "Unknown outcome.",
    };
}
