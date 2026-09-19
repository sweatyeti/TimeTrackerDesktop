namespace TimeTrackerDesktop.Domain;

/// <summary>
/// The outcome of logging a task group: the resulting state, the group that was asked for, and the
/// entries this call actually logged.
///
/// TTC's <c>ApplyLogTaskGroup</c> returns a bare <c>bool</c>, so a caller cannot tell "no such group"
/// from "already logged" from "nothing was eligible". The ids are returned instead: an empty list
/// means nothing needed logging, and a panel can say so without re-deriving the eligibility rules.
/// </summary>
public sealed record LogGroupResult(SessionState State, string? TaskGroup, IReadOnlyList<int> LoggedEntryIds)
{
    /// <summary>True when at least one entry was logged by this call.</summary>
    public bool Applied => LoggedEntryIds.Count > 0;

    /// <summary>How many entries this call logged.</summary>
    public int Count => LoggedEntryIds.Count;

    /// <summary>A short explanation suitable for a status line.</summary>
    public string Reason => Applied
        ? $"Logged {Count} {(Count == 1 ? "entry" : "entries")}."
        : "Nothing to log for that task group.";
}
