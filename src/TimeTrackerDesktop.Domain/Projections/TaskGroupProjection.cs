namespace TimeTrackerDesktop.Domain;

/// <summary>
/// One row of completed work grouped by task, case-insensitively.
///
/// <see cref="Total"/> and <see cref="Unlogged"/> are whole minutes because TTC renders and totals
/// whole minutes — see <see cref="TaskGroupProjection"/> for the rounding rule, which is applied per
/// entry rather than to a sum.
/// </summary>
public sealed record TaskGroupRow(
    string Task,
    int EntryCount,
    TimeSpan Total,
    TimeSpan Unlogged,
    int UnloggedEntryCount,
    bool IsUntracked)
{
    /// <summary>True when this group still has completed work that has not been logged.</summary>
    public bool HasUnloggedWork => UnloggedEntryCount > 0;
}

/// <summary>
/// Completed, non-deleted work grouped by task (plan Task 1.4).
///
/// Pure calculation over a snapshot: no clock, no disk, no XAML — so the console's numbers and the
/// widget's numbers cannot drift apart, and every rule below is testable without a UI host.
/// </summary>
public static class TaskGroupProjection
{
    /// <summary>
    /// The rows, earliest group first. Untracked time ("none", or an empty task from a TTC file) is
    /// included as its own row and flagged, because TTC's summary shows it — it is the *totals* that
    /// exclude it.
    /// </summary>
    public static IReadOnlyList<TaskGroupRow> Rows(SessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return
        [
            .. state.Entries
                .Where(entry => entry.IsComplete && !entry.IsDeleted)
                .GroupBy(entry => entry.Task, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Min(entry => entry.StartTime))
                .ThenBy(group => group.Min(entry => entry.Id))
                .Select(BuildRow),
        ];
    }

    /// <summary>
    /// The groups that still have unlogged completed work — the Log Group choice set. Untracked time
    /// is not a task group, so it is never offered.
    /// </summary>
    public static IReadOnlyList<TaskGroupRow> Unlogged(SessionState state) =>
        [.. Rows(state).Where(row => row.HasUnloggedWork && !row.IsUntracked)];

    /// <summary>
    /// The group's readable spelling: the first non-empty task spelling encountered by start time,
    /// then id.
    ///
    /// TTC groups on <c>entry.Task.ToLower()</c> and displays that lowercased key, so work the user
    /// typed as "Client Work" is shown as "client work". The plan settles on the original spelling
    /// (its Q2), which is both readable and deterministic — TTC's alternative depends on the order a
    /// <c>Dictionary</c> happens to enumerate in, which v1's hard deletes could perturb.
    /// </summary>
    internal static string CanonicalSpelling(IEnumerable<TimeEntry> group) =>
        group
            .Where(entry => !string.IsNullOrEmpty(entry.Task))
            .OrderBy(entry => entry.StartTime)
            .ThenBy(entry => entry.Id)
            .Select(entry => entry.Task)
            .FirstOrDefault() ?? string.Empty;

    /// <summary>
    /// One entry's duration in whole minutes, rounded up.
    ///
    /// TTC sums <c>Math.Ceiling(entry.TotalMinutes)</c> across a group, so two 30-second entries make
    /// 2 minutes rather than the 1 minute that rounding the summed duration would give. An open entry
    /// has no duration and contributes nothing.
    /// </summary>
    internal static TimeSpan WholeMinutes(TimeEntry entry) =>
        entry.Duration is { } duration
            ? TimeSpan.FromMinutes(Math.Ceiling(duration.TotalMinutes))
            : TimeSpan.Zero;

    private static TaskGroupRow BuildRow(IGrouping<string, TimeEntry> group)
    {
        List<TimeEntry> entries = [.. group];
        List<TimeEntry> unlogged = [.. entries.Where(entry => !entry.Logged)];

        return new TaskGroupRow(
            Task: CanonicalSpelling(entries),
            EntryCount: entries.Count,
            Total: Sum(entries),
            Unlogged: Sum(unlogged),
            UnloggedEntryCount: unlogged.Count,
            IsUntracked: IsUntracked(group.Key));
    }

    private static TimeSpan Sum(IEnumerable<TimeEntry> entries) =>
        TimeSpan.FromTicks(entries.Sum(entry => WholeMinutes(entry).Ticks));

    /// <summary>
    /// True for untracked time. TTC's summary treats an empty task the same as "none" (its
    /// <c>emptyTask</c> test) even though its log-group choice set does not; the reader can meet an
    /// empty task in a TTC file, because TTC's update path trims without mapping blank to "none".
    /// </summary>
    private static bool IsUntracked(string task) =>
        string.IsNullOrEmpty(task) || task.Equals(TimeEntry.NoTask, StringComparison.OrdinalIgnoreCase);
}
