using TimeTrackerDesktop.Domain;

namespace TimeTrackerDesktop.Domain.Tests;

/// <summary>
/// Soft delete, task grouping and the summary projections (plan Task 1.4).
///
/// The numbers here are TTC's numbers, not plausible-looking ones: durations round each entry up to a
/// whole minute <i>before</i> summing, the running entry never counts as completed work, and untracked
/// time ("none", or an empty task from a TTC file) gets a row but never inflates a named total.
/// </summary>
public sealed class GroupingAndSummaryTests
{
    /// <summary>A fixed instant in a non-UTC offset, so a test that assumes UTC fails loudly.</summary>
    private static readonly DateTimeOffset Base = new(2026, 9, 18, 9, 0, 0, TimeSpan.FromHours(-5));

    /// <summary>A completed entry starting <paramref name="startMinutes"/> after the base.</summary>
    private static TimeEntry Done(
        int id,
        string task,
        int startMinutes,
        int minutes,
        bool logged = false,
        bool deleted = false) =>
        new(
            Id: id,
            StartTime: Base.AddMinutes(startMinutes),
            EndTime: Base.AddMinutes(startMinutes + minutes),
            Task: task,
            Description: string.Empty,
            Logged: logged,
            IsComplete: true,
            IsDeleted: deleted);

    /// <summary>The one open entry a session is allowed to have.</summary>
    private static TimeEntry Running(int id, string task, int startMinutes) =>
        new(
            Id: id,
            StartTime: Base.AddMinutes(startMinutes),
            EndTime: null,
            Task: task,
            Description: string.Empty,
            Logged: false,
            IsComplete: false,
            IsDeleted: false);

    private static SessionState StateWith(params TimeEntry[] entries) =>
        SessionState.New("fixture", Base) with { Entries = entries };

    private static SessionService ServiceWith(out FakeClock clock, params TimeEntry[] entries)
    {
        clock = new FakeClock(Base);
        return new SessionService(StateWith(entries), clock);
    }

    // ------------------------------------------------------------------
    // Soft delete and restore
    // ------------------------------------------------------------------

    [Fact]
    public void Delete_is_allowed_only_for_completed_undeleted_entries()
    {
        SessionService service = ServiceWith(
            out _,
            Running(1, "weeding", 0),
            Done(2, "reading", 60, 30),
            Done(3, "reading", 120, 30, deleted: true));

        // an in-progress entry cannot be deleted: it is still being tracked
        EntryVisibilityResult active = service.Delete(1);
        Assert.Equal(EntryVisibilityOutcome.NotDeletable, active.Outcome);

        // already-deleted work is not deleted again
        EntryVisibilityResult again = service.Delete(3);
        Assert.Equal(EntryVisibilityOutcome.NotDeletable, again.Outcome);

        EntryVisibilityResult ok = service.Delete(2);
        Assert.Equal(EntryVisibilityOutcome.Deleted, ok.Outcome);
        Assert.True(ok.Applied);
        Assert.True(service.State.Entries.Single(entry => entry.Id == 2).IsDeleted);
    }

    [Fact]
    public void Restore_reverses_only_the_deleted_flag()
    {
        TimeEntry original = Done(2, "reading", 60, 30, logged: true, deleted: true);
        SessionService service = ServiceWith(out _, original);

        EntryVisibilityResult result = service.Restore(2);

        Assert.Equal(EntryVisibilityOutcome.Restored, result.Outcome);

        // nothing but IsDeleted moves - not the times, task, description or logged state
        TimeEntry restored = service.State.Entries.Single();
        Assert.Equal(original with { IsDeleted = false }, restored);
        Assert.False(restored.IsDeleted);
    }

    [Fact]
    public void Restore_is_rejected_for_an_entry_that_is_not_deleted()
    {
        SessionService service = ServiceWith(out _, Done(1, "reading", 0, 30));

        EntryVisibilityResult result = service.Restore(1);

        Assert.Equal(EntryVisibilityOutcome.NotDeleted, result.Outcome);
        Assert.False(result.Applied);
        Assert.False(service.State.Entries.Single().IsDeleted);
    }

    [Fact]
    public void Delete_and_restore_report_an_unknown_id_rather_than_ignoring_it()
    {
        SessionService service = ServiceWith(out _, Done(1, "reading", 0, 30));

        Assert.Equal(EntryVisibilityOutcome.EntryNotFound, service.Delete(99).Outcome);
        Assert.Equal(EntryVisibilityOutcome.EntryNotFound, service.Restore(99).Outcome);
    }

    [Fact]
    public void Soft_deleted_work_stays_in_the_snapshot_and_leaves_every_projection()
    {
        SessionService service = ServiceWith(
            out _,
            Done(1, "weeding", 0, 30),
            Done(2, "weeding", 30, 30, deleted: true));

        // the raw snapshot keeps both, which is what makes restore possible
        Assert.Equal(2, service.State.Entries.Count);

        // the projections see only the live one
        Assert.Equal(1, TaskGroupProjection.Rows(service.State).Single().EntryCount);
        Assert.Equal(TimeSpan.FromMinutes(30), SummaryProjection.From(service.State).NamedTotal);
    }

    // ------------------------------------------------------------------
    // Grouping and canonical spelling
    // ------------------------------------------------------------------

    [Fact]
    public void Grouping_is_case_insensitive_and_uses_the_earliest_spelling()
    {
        SessionService service = ServiceWith(
            out _,
            Done(1, "client work", 0, 30),
            Done(2, "Client Work", 60, 30),
            Done(3, "CLIENT WORK", 120, 30));

        TaskGroupRow row = Assert.Single(TaskGroupProjection.Rows(service.State));

        // the earliest-starting entry supplies the spelling, not the last one and not a lowercase key
        Assert.Equal("client work", row.Task);
        Assert.Equal(3, row.EntryCount);
    }

    [Fact]
    public void A_start_time_tie_is_broken_by_entry_id()
    {
        SessionService service = ServiceWith(
            out _,
            Done(4, "weeding", 0, 30),
            Done(5, "Weeding", 0, 30));

        // same start time, so the lower id decides the spelling
        Assert.Equal("weeding", TaskGroupProjection.Rows(service.State).Single().Task);
    }

    [Fact]
    public void An_empty_task_forms_its_own_untracked_row()
    {
        // TTC's update path can write an empty-string task, so a loaded session may contain one. It is
        // a different task string from "none", so it groups separately - TTC does the same - and both
        // are untracked: neither inflates a named total.
        SessionService service = ServiceWith(
            out _,
            Done(1, "", 0, 30),
            Done(2, "weeding", 60, 30));

        IReadOnlyList<TaskGroupRow> rows = TaskGroupProjection.Rows(service.State);

        Assert.Equal(2, rows.Count);
        Assert.Single(rows, row => row.IsUntracked);
        Assert.Equal(TimeSpan.FromMinutes(30), SummaryProjection.From(service.State).NamedTotal);
    }

    // ------------------------------------------------------------------
    // Durations and totals
    // ------------------------------------------------------------------

    [Fact]
    public void Durations_round_each_entry_up_before_summing_not_the_total()
    {
        // TTC sums Math.Ceiling(entry.TotalMinutes) per entry, so two 30-second entries make 2
        // minutes. Rounding the summed duration instead would give 1 minute and quietly disagree.
        TimeEntry first = Done(1, "weeding", 0, 0) with { EndTime = Base.AddSeconds(30) };
        TimeEntry second = Done(2, "weeding", 1, 0) with { EndTime = Base.AddMinutes(1).AddSeconds(30) };

        SessionService service = ServiceWith(out _, first, second);
        TaskGroupRow row = Assert.Single(TaskGroupProjection.Rows(service.State));

        Assert.Equal(TimeSpan.FromMinutes(2), row.Total);
    }

    [Fact]
    public void The_running_entry_is_surfaced_but_never_counted_as_completed_work()
    {
        SessionService service = ServiceWith(
            out _,
            Running(1, "weeding", 0),
            Done(2, "weeding", 0, 30));

        SessionSummary summary = SummaryProjection.From(service.State);

        Assert.Equal(TimeSpan.FromMinutes(30), summary.NamedTotal);
        Assert.Equal(1, summary.Rows.Single().EntryCount);
        Assert.Equal(1, summary.Running!.Id);
    }

    [Fact]
    public void Untracked_time_gets_a_row_but_never_inflates_a_named_total()
    {
        SessionService service = ServiceWith(
            out _,
            Done(1, "none", 0, 30),
            Done(2, "weeding", 60, 45));

        SessionSummary summary = SummaryProjection.From(service.State);

        Assert.Equal(2, summary.Rows.Count);

        TaskGroupRow untracked = summary.Rows.Single(row => row.IsUntracked);
        Assert.Equal("none", untracked.Task);
        Assert.Equal(TimeSpan.FromMinutes(30), untracked.Total);

        // totals are the named groups only (TTC's issue #15)
        Assert.Equal(TimeSpan.FromMinutes(45), summary.NamedTotal);
    }

    [Fact]
    public void Unlogged_duration_counts_only_completed_named_unlogged_work()
    {
        SessionService service = ServiceWith(
            out _,
            Done(1, "weeding", 0, 30),
            Done(2, "weeding", 30, 20, logged: true),
            Done(3, "none", 60, 15));

        SessionSummary summary = SummaryProjection.From(service.State);
        TaskGroupRow named = summary.Rows.Single(row => !row.IsUntracked);

        Assert.Equal(TimeSpan.FromMinutes(50), named.Total);
        Assert.Equal(TimeSpan.FromMinutes(30), named.Unlogged);
        Assert.Equal(1, named.UnloggedEntryCount);

        Assert.Equal(TimeSpan.FromMinutes(30), summary.NamedUnlogged);
    }

    [Fact]
    public void The_log_group_choice_set_excludes_untracked_and_fully_logged_groups()
    {
        SessionService service = ServiceWith(
            out _,
            Done(1, "weeding", 0, 30),
            Done(2, "reading", 30, 30, logged: true),
            Done(3, "none", 60, 15));

        IReadOnlyList<TaskGroupRow> unlogged = TaskGroupProjection.Unlogged(service.State);

        Assert.Equal("weeding", Assert.Single(unlogged).Task);
    }

    // ------------------------------------------------------------------
    // Log Group
    // ------------------------------------------------------------------

    [Fact]
    public void Log_group_matches_case_insensitively_and_marks_only_eligible_entries()
    {
        SessionService service = ServiceWith(
            out _,
            Running(1, "weeding", 0),
            Done(2, "Weeding", 0, 30),
            Done(3, "WEEDING", 30, 30),
            Done(4, "weeding", 60, 30, deleted: true),
            Done(5, "reading", 90, 30));

        LogGroupResult result = service.LogTaskGroup("weeding");

        // the completed, named, undeleted entries are logged whichever way they are spelled
        Assert.Equal([2, 3], result.LoggedEntryIds);
        Assert.True(service.State.Entries.Single(entry => entry.Id == 2).Logged);
        Assert.True(service.State.Entries.Single(entry => entry.Id == 3).Logged);

        // the running entry is not completed work, and the deleted one is not live work
        Assert.False(service.State.Entries.Single(entry => entry.Id == 1).Logged);
        Assert.False(service.State.Entries.Single(entry => entry.Id == 4).Logged);

        // a different group is untouched
        Assert.False(service.State.Entries.Single(entry => entry.Id == 5).Logged);
    }

    [Fact]
    public void Log_group_with_a_blank_or_unknown_task_logs_nothing()
    {
        SessionService service = ServiceWith(out _, Done(1, "weeding", 0, 30));

        Assert.Empty(service.LogTaskGroup(null).LoggedEntryIds);
        Assert.Empty(service.LogTaskGroup("   ").LoggedEntryIds);
        Assert.Empty(service.LogTaskGroup("nothing like this").LoggedEntryIds);
        Assert.False(service.State.Entries.Single().Logged);
    }

    [Fact]
    public void Logging_a_group_twice_logs_nothing_the_second_time()
    {
        SessionService service = ServiceWith(out _, Done(1, "weeding", 0, 30));

        Assert.Single(service.LogTaskGroup("weeding").LoggedEntryIds);
        Assert.Empty(service.LogTaskGroup("weeding").LoggedEntryIds);
    }
}
