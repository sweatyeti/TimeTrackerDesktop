namespace TimeTrackerDesktop.Domain;

/// <summary>What happened when a loaded session was opened.</summary>
public enum SessionResumeOutcome
{
    /// <summary>The session opened. It is active only when it has one unfinished entry.</summary>
    Resumed,

    /// <summary>
    /// The session holds more than one unfinished non-deleted entry, so it is corrupt: nothing in the
    /// file says which entry is running. The plan's Q3 settles this as a refusal.
    /// </summary>
    CorruptMultipleUnfinishedEntries,
}

/// <summary>
/// The outcome of resuming a session.
///
/// <see cref="Session"/> is <c>null</c> exactly when the session was refused, so a caller cannot
/// accidentally open, repair, render read-only or export a corrupt session — there is nothing to hold
/// on to.
/// </summary>
public sealed record SessionResumeResult(
    SessionService? Session,
    SessionResumeOutcome Outcome,
    IReadOnlyList<int> UnfinishedEntryIds)
{
    /// <summary>True when the session opened.</summary>
    public bool Resumed => Outcome is SessionResumeOutcome.Resumed;

    /// <summary>True when the opened session has no running entry.</summary>
    public bool IsIdle => Session is { } session && !session.State.IsActive;

    /// <summary>A short explanation suitable for an error dialog or a log line.</summary>
    public string Reason => Outcome switch
    {
        SessionResumeOutcome.Resumed => "Session opened.",
        SessionResumeOutcome.CorruptMultipleUnfinishedEntries =>
            $"This session has {UnfinishedEntryIds.Count} unfinished entries "
            + $"({string.Join(", ", UnfinishedEntryIds)}), so it cannot be opened. "
            + "Restore it from a backup, or edit the session file so that only one entry is unfinished.",
        _ => "Unknown outcome.",
    };
}

/// <summary>
/// Opens a loaded session (plan Task 1.5).
///
/// Deliberately separate from <see cref="SessionService.StartNewSession"/>, which begins tracking
/// immediately: resuming must not mint, restamp or normalize anything away.
/// </summary>
public static class SessionResumeService
{
    /// <summary>
    /// Opens <paramref name="state"/> when it is loadable.
    ///
    /// Nothing is normalized away: a running entry keeps its original start time (invariant 6), the
    /// next id is <c>max(stored id) + 1</c> — which is what makes v1's id gaps safe — and a session with
    /// no unfinished entry opens idle rather than creating one. Soft-deleted entries are read like any
    /// other, but they never count as active.
    ///
    /// A session with more than one unfinished non-deleted entry is refused outright, with the
    /// offending ids named so a caller can say which entries are the problem.
    /// </summary>
    public static SessionResumeResult Resume(SessionState state, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(clock);

        List<int> unfinished = [.. state.Entries.Where(entry => entry.IsOpen).Select(entry => entry.Id)];

        if(unfinished.Count > 1)
        {
            // checked before constructing the service: its constructor throws on this, and a throw is
            // not something a chooser can render
            return new SessionResumeResult(
                null,
                SessionResumeOutcome.CorruptMultipleUnfinishedEntries,
                unfinished);
        }

        return new SessionResumeResult(new SessionService(state, clock), SessionResumeOutcome.Resumed, []);
    }
}
