namespace TimeTrackerDesktop.Domain;

/// <summary>
/// TTC's Summary table: the task-group rows, the totals over named groups, and the entry that is still
/// running.
///
/// <see cref="Running"/> is deliberately separate. The active entry is not completed work, so it must
/// never appear in a row or a total — the panel shows it in its own section, with a timer derived from
/// its persisted start time (invariant 8), not from any accumulated tick.
/// </summary>
public sealed record SessionSummary(
    IReadOnlyList<TaskGroupRow> Rows,
    TimeSpan NamedTotal,
    TimeSpan NamedUnlogged,
    TimeEntry? Running)
{
    /// <summary>True when there is any completed, non-deleted work to summarise.</summary>
    public bool HasWork => Rows.Count > 0;

    /// <summary>The untracked row ("none" or an empty task), when the session has one.</summary>
    public TaskGroupRow? Untracked => Rows.SingleOrDefault(row => row.IsUntracked);
}

/// <summary>
/// The summary calculation (plan Task 1.4), shared by the parity tests and the Summary panel.
/// </summary>
public static class SummaryProjection
{
    /// <summary>
    /// Builds the summary. Totals cover the <b>named</b> groups only: untracked time gets its own row
    /// but never inflates a total, which is TTC's issue #15 behaviour.
    /// </summary>
    public static SessionSummary From(SessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        IReadOnlyList<TaskGroupRow> rows = TaskGroupProjection.Rows(state);
        List<TaskGroupRow> named = [.. rows.Where(row => !row.IsUntracked)];

        return new SessionSummary(
            Rows: rows,
            NamedTotal: Sum(named, row => row.Total),
            NamedUnlogged: Sum(named, row => row.Unlogged),
            Running: state.ActiveEntry);
    }

    private static TimeSpan Sum(IEnumerable<TaskGroupRow> rows, Func<TaskGroupRow, TimeSpan> select) =>
        TimeSpan.FromTicks(rows.Sum(row => select(row).Ticks));
}
