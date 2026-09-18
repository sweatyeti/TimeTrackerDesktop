namespace TimeTrackerDesktop.Domain;

/// <summary>
/// One tracked interval. Immutable — every transition returns a new instance, so a caller can never
/// mutate state it has already handed to the UI.
///
/// Field for field compatible with TTC's <c>EntrySnapshot</c> (see <c>docs/JSON-COMPATIBILITY.md</c>):
/// <see cref="EndTime"/> is nullable rather than <see cref="DateTime.MinValue"/>, so "still running"
/// is represented honestly instead of by a sentinel that leaks into arithmetic. <c>IsValid</c> is
/// deliberately absent — it is a TTC prompt-cancel artifact, not domain state.
/// </summary>
public sealed record TimeEntry(
    int Id,
    DateTimeOffset StartTime,
    DateTimeOffset? EndTime,
    string Task,
    string Description,
    bool Logged,
    bool IsComplete,
    bool IsDeleted)
{
    /// <summary>The task name used for untracked time. TTC treats it as an ordinary value.</summary>
    public const string NoTask = "none";

    /// <summary>Creates an open entry started at <paramref name="at"/> with a blank description.</summary>
    public static TimeEntry Start(int id, DateTimeOffset at, string? task = null) =>
        new(
            Id: id,
            StartTime: at,
            EndTime: null,
            Task: NormalizeTask(task),
            Description: string.Empty,
            Logged: false,
            IsComplete: false,
            IsDeleted: false);

    /// <summary>Still being tracked: neither completed nor soft-deleted.</summary>
    public bool IsOpen => !IsComplete && !IsDeleted;

    /// <summary>
    /// Tracked duration, or <c>null</c> while the entry has no end time. Nullable rather than zero so
    /// an open entry cannot contribute a bogus zero-length interval to a total.
    /// </summary>
    public TimeSpan? Duration => EndTime is { } end ? end - StartTime : null;

    /// <summary>
    /// True for the "no task" pseudo-task. Compared case-insensitively, matching TTC's <c>IsNoTask</c>,
    /// so a user typing "None" is treated as untracked rather than as a distinct task group.
    /// </summary>
    public bool HasNoTask => Task.Equals(NoTask, StringComparison.OrdinalIgnoreCase);

    /// <summary>Completes the entry at <paramref name="at"/>. Idempotent in effect: the last stop wins.</summary>
    public TimeEntry Complete(DateTimeOffset at) => this with { EndTime = at, IsComplete = true };

    /// <summary>
    /// Blank input means "no task": trims, then maps empty to <see cref="NoTask"/> — exactly TTC's
    /// insert path (<c>InsertNewEntry</c>). A task that is only whitespace is not preserved.
    /// </summary>
    public static string NormalizeTask(string? task)
    {
        string trimmed = (task ?? string.Empty).Trim();
        return string.IsNullOrEmpty(trimmed) ? NoTask : trimmed;
    }
}