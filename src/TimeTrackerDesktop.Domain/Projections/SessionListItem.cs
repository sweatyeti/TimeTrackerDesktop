namespace TimeTrackerDesktop.Domain;

/// <summary>
/// One row of the session chooser: what a user needs in order to pick a session to open.
///
/// Carries no file path or session id on purpose — the caller pairs rows with the sessions it loaded,
/// so this stays a pure display projection with no way to reach the disk.
/// </summary>
public sealed record SessionListItem(
    string Name,
    DateTimeOffset StartedAt,
    bool IsUnfinished,
    bool IsActive,
    TimeSpan Tracked)
{
    /// <summary>True when the session has been ended (TTC's <c>endedAt</c> is set).</summary>
    public bool IsFinished => !IsUnfinished;
}

/// <summary>
/// The session chooser's rows (plan Task 1.5).
///
/// TTC's own chooser shows <c>{name} - yyyy-MM-dd HH:mm</c> plus an "(unfinished)" marker and nothing
/// else. The plan asks for more, and the user settled the ambiguity on 2026-09-18: show the session's
/// **start time** and the **amount of tracked time**, with no entry count.
/// </summary>
public static class SessionListProjection
{
    /// <summary>TTC's fallback for a session whose name did not survive a round trip.</summary>
    public const string UnnamedFallback = "Unnamed session";

    /// <summary>Builds one row.</summary>
    public static SessionListItem From(SessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new SessionListItem(
            Name: string.IsNullOrWhiteSpace(state.Name) ? UnnamedFallback : state.Name,
            StartedAt: state.StartedAt,
            IsUnfinished: state.EndedAt is null,
            IsActive: state.IsActive,
            Tracked: Tracked(state));
    }

    /// <summary>
    /// The sessions in chooser order: newest first, ties broken on the name so the order never depends on the
    /// order the sessions happened to be listed in.
    ///
    /// Separate from <see cref="NewestFirst"/> so a caller that needs the session behind each row — the
    /// chooser does, to resume it — gets the same order without reimplementing the rule and risking a
    /// mismatch between a row and the session it points at.
    /// </summary>
    public static IReadOnlyList<SessionState> Ordered(IEnumerable<SessionState> states)
    {
        ArgumentNullException.ThrowIfNull(states);

        return
        [
            .. states
                .OrderByDescending(state => state.StartedAt)
                .ThenBy(state => state.Name, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Builds the rows newest first. Ties break on the name so the order never depends on the order the
    /// sessions happened to be listed in — "stable" has to mean reproducible, not incidental.
    /// </summary>
    public static IReadOnlyList<SessionListItem> NewestFirst(IEnumerable<SessionState> states) =>
        [.. Ordered(states).Select(From)];

    /// <summary>
    /// The session's total tracked time: every completed, non-deleted entry, untracked ("none") time
    /// included, rounded up per entry exactly as the summary does.
    ///
    /// The running entry is excluded — it is not tracked time yet — so the number does not creep upward
    /// while the chooser sits open. This is a different question from the summary's named totals, which
    /// deliberately exclude untracked time.
    /// </summary>
    private static TimeSpan Tracked(SessionState state) =>
        TimeSpan.FromTicks(TaskGroupProjection.Rows(state).Sum(row => row.Total.Ticks));
}
