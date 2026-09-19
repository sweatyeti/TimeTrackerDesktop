using System.Globalization;
using System.Reflection;
using TimeTrackerDesktop.Domain;

namespace TimeTrackerDesktop.Domain.Tests;

/// <summary>
/// Domain behaviour for session state and the clock abstraction (plan Task 1.1).
///
/// Everything here runs without WinUI, a filesystem or the wall clock: the domain takes an
/// <see cref="IClock"/>, so "now" is whatever the test says it is.
/// </summary>
public sealed class SessionBehaviorTests
{
    /// <summary>A fixed instant in a non-UTC offset, so a test that assumes UTC fails loudly.</summary>
    private static readonly DateTimeOffset Noon = new(2026, 9, 18, 12, 0, 0, TimeSpan.FromHours(-5));

    private static TimeEntry Entry(int id, string task = "none", bool complete = false, bool deleted = false) =>
        new(
            Id: id,
            StartTime: Noon,
            EndTime: complete ? Noon.AddMinutes(1) : null,
            Task: task,
            Description: string.Empty,
            Logged: false,
            IsComplete: complete,
            IsDeleted: deleted);

    private static SessionState StateWith(params TimeEntry[] entries) =>
        SessionState.New("fixture", Noon) with { Entries = entries };

    // ------------------------------------------------------------------ session identity and name
    [Fact]
    public void New_session_generates_its_name_from_the_clock_when_blank()
    {
        FakeClock clock = FakeClock.AtCentral(2026, 9, 18, 14, 5, 0);

        SessionState session = SessionState.New(null, clock.Now);

        Assert.Equal("Session 2026-09-18 14:05:00", session.Name);
    }

    [Fact]
    public void New_session_generates_a_name_for_an_empty_string_as_well()
    {
        Assert.Equal("Session 2026-09-18 12:00:00", SessionState.New(string.Empty, Noon).Name);
    }

    [Fact]
    public void New_session_keeps_an_explicit_name_verbatim()
    {
        Assert.Equal("site review", SessionState.New("site review", Noon).Name);
        Assert.Equal("Session 2026-09-18", SessionState.New("Session 2026-09-18", Noon).Name);
    }

    [Fact]
    public void A_whitespace_only_name_counts_as_blank_and_is_generated()
    {
        // Deliberate deviation from TTC, which tests IsNullOrEmpty: a name of only spaces is kept
        // there and slugs to "---.json", so it is treated as blank here instead.
        Assert.Equal("Session 2026-09-18 12:00:00", SessionState.New("   ", Noon).Name);
        Assert.Equal("Session 2026-09-18 12:00:00", SessionState.New("\t \n", Noon).Name);
    }

    [Fact]
    public void Generated_name_does_not_depend_on_the_machine_culture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            // fi-FI renders times with '.' separators, which would change the generated name if the
            // format were culture-sensitive
            CultureInfo.CurrentCulture = new CultureInfo("fi-FI");

            Assert.Equal("Session 2026-09-18 12:00:00", SessionState.ResolveName(null, Noon));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void New_session_starts_unended_with_a_fresh_identity_and_no_entries()
    {
        SessionState first = SessionState.New("one", Noon);
        SessionState second = SessionState.New("two", Noon);

        Assert.NotEqual(Guid.Empty, first.SessionId);
        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.Null(first.EndedAt);
        Assert.Equal(Noon, first.StartedAt);
        Assert.Empty(first.Entries);
    }

    // ------------------------------------------------------------------ entry creation
    [Fact]
    public void Starting_an_entry_uses_the_clock_time_with_a_blank_description()
    {
        FakeClock clock = FakeClock.AtCentral(2026, 9, 18, 14, 5, 0);
        SessionService service = new(SessionState.New("demo", clock.Now), clock);

        service.StartEntry();

        TimeEntry entry = Assert.Single(service.State.Entries);
        Assert.Equal(clock.Now, entry.StartTime);
        Assert.Equal(string.Empty, entry.Description);
        Assert.Null(entry.EndTime);
        Assert.Equal(TimeEntry.NoTask, entry.Task);
        Assert.False(entry.IsComplete);
        Assert.False(entry.IsDeleted);
        Assert.False(entry.Logged);
    }

    [Fact]
    public void Entry_ids_begin_at_one()
    {
        FakeClock clock = FakeClock.AtCentral(2026, 9, 18, 9, 0, 0);
        SessionService service = new(SessionState.New("demo", clock.Now), clock);

        service.StartEntry();

        Assert.Equal(1, Assert.Single(service.State.Entries).Id);
        Assert.Equal(2, service.State.NextEntryId);
    }

    [Fact]
    public void Stop_and_start_completes_the_open_entry_at_the_new_clock_time()
    {
        FakeClock clock = FakeClock.AtCentral(2026, 9, 18, 9, 0, 0);
        SessionService service = new(SessionState.New("demo", clock.Now), clock);

        service.StartEntry("first");
        clock.Advance(TimeSpan.FromMinutes(5));
        service.RestartEntry("second");

        Assert.Equal([1, 2], service.State.Entries.Select(entry => entry.Id));

        TimeEntry first = service.State.Entries[0];
        Assert.True(first.IsComplete);
        Assert.Equal(clock.Now, first.EndTime);

        TimeEntry second = service.State.ActiveEntry!;
        Assert.Equal(2, second.Id);
        Assert.Equal("second", second.Task);
        Assert.Equal(clock.Now, second.StartTime);
    }

    [Fact]
    public void Play_stop_uses_injected_time_and_starts_at_one()
    {
        FakeClock clock = FakeClock.AtCentral(2026, 9, 9, 12, 0, 0);
        SessionService service = new(SessionState.New("demo", clock.Now), clock);

        service.StartEntry();
        Assert.Equal(TimeEntry.NoTask, service.State.ActiveEntry!.Task);
        Assert.Equal(1, service.State.ActiveEntry.Id);

        clock.Advance(TimeSpan.FromMinutes(3));
        service.StopCurrentEntry();

        TimeEntry entry = Assert.Single(service.State.Entries);
        Assert.True(entry.IsComplete);
        Assert.Equal(TimeSpan.FromMinutes(3), entry.Duration);
        Assert.Null(service.State.ActiveEntry);
    }

    // ------------------------------------------------------------------ id allocation
    [Fact]
    public void Next_entry_id_is_one_for_an_empty_session()
    {
        Assert.Equal(1, SessionState.New("empty", Noon).NextEntryId);
    }

    [Fact]
    public void Next_entry_id_is_one_past_the_highest_id_not_the_entry_count()
    {
        // ids are keys: a v1 hard delete left holes, so counting entries would re-mint an existing id
        SessionState state = StateWith(Entry(1, complete: true), Entry(4, complete: true), Entry(9, complete: true));

        Assert.Equal(10, state.NextEntryId);
    }

    [Fact]
    public void Next_entry_id_does_not_reuse_a_soft_deleted_id()
    {
        SessionState state = StateWith(Entry(1, complete: true), Entry(2, complete: true, deleted: true));

        Assert.Equal(3, state.NextEntryId);
    }

    [Fact]
    public void Next_entry_id_skips_a_gap_created_by_an_open_entry_above_a_hole()
    {
        SessionState state = StateWith(Entry(2, "running"), Entry(7, "later", complete: true));

        Assert.Equal(8, state.NextEntryId);
        Assert.Equal(2, state.ActiveEntry!.Id);
    }

    // ------------------------------------------------------------------ one-open-entry invariant
    [Fact]
    public void Session_with_two_open_entries_is_rejected_on_load()
    {
        SessionState state = StateWith(Entry(1, "a"), Entry(2, "b"));

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => new SessionService(state, new FakeClock(Noon)));
        Assert.Contains("more than one unfinished entry", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_deleted_open_entry_is_neither_active_nor_a_validation_failure()
    {
        // TTC's rule is !IsDeleted && !IsComplete, so a deleted entry can never count as the active one
        SessionState state = StateWith(Entry(1, "a", deleted: true), Entry(2, "b", complete: true));

        state.Validate();
        Assert.Null(state.ActiveEntry);
        Assert.False(state.IsActive);
    }

    [Fact]
    public void Deleted_entries_are_excluded_from_the_live_set_but_kept_in_the_session()
    {
        SessionState state = StateWith(Entry(1, complete: true), Entry(2, complete: true, deleted: true));

        Assert.Equal(2, state.Entries.Count);
        Assert.Equal([1], state.LiveEntries.Select(entry => entry.Id));
    }

    // ------------------------------------------------------------------ entry semantics
    [Theory]
    [InlineData(null, "none")]
    [InlineData("", "none")]
    [InlineData("   ", "none")]
    [InlineData("  site visit  ", "site visit")]
    [InlineData("none", "none")]
    public void Task_input_is_trimmed_and_blank_becomes_none(string? input, string expected) =>
        Assert.Equal(expected, TimeEntry.NormalizeTask(input));

    [Fact]
    public void No_task_is_recognised_case_insensitively()
    {
        Assert.True(Entry(1, "NONE").HasNoTask);
        Assert.True(Entry(1, "None").HasNoTask);
        Assert.False(Entry(1, "none of your business").HasNoTask);
        Assert.False(Entry(1, "gamma").HasNoTask);
    }

    [Fact]
    public void Duration_is_null_while_open_and_measured_once_complete()
    {
        TimeEntry open = Entry(1, "a");
        Assert.Null(open.Duration);

        TimeEntry done = open.Complete(Noon.AddMinutes(90));
        Assert.Equal(TimeSpan.FromMinutes(90), done.Duration);
        Assert.True(done.IsComplete);
    }

    [Fact]
    public void Completing_an_entry_does_not_move_its_start_time_or_clear_its_task()
    {
        TimeEntry open = TimeEntry.Start(3, Noon, "  site visit  ");

        TimeEntry done = open.Complete(Noon.AddMinutes(10));

        Assert.Equal(Noon, done.StartTime);
        Assert.Equal("site visit", done.Task);
        Assert.Equal(3, done.Id);
    }

    // ================================================================== Task 1.2: transitions
    // Play uses the exact restart semantics; Stop stops only (plan Task 4.2).

    /// <summary>
    /// An <b>idle</b> session with no entries. StartNewSession auto-starts an entry (TTC's CLI `new`
    /// behaviour), so a test that needs the idle state builds the session directly.
    /// </summary>
    private static SessionService NewIdleService(out FakeClock clock)
    {
        clock = FakeClock.AtCentral(2026, 9, 18, 9, 0, 0);
        return new SessionService(SessionState.New("demo", clock.Now), clock);
    }

    [Fact]
    public void Idle_play_starts_an_entry_with_none_a_blank_description_and_the_clock_time()
    {
        SessionService service = NewIdleService(out FakeClock clock);

        SessionResult result = service.StartEntry();

        Assert.Equal(SessionChange.EntryStarted, result.Change);
        Assert.True(result.IsActive);
        Assert.False(result.IsEnded);

        TimeEntry entry = result.ActiveEntry!;
        Assert.Equal(TimeEntry.NoTask, entry.Task);
        Assert.Equal(string.Empty, entry.Description);
        Assert.Equal(clock.Now, entry.StartTime);
        Assert.Null(entry.EndTime);
        Assert.Equal(entry, result.AffectedEntry);
    }

    [Fact]
    public void Stop_completes_that_exact_entry_at_the_clock_time_and_leaves_the_session_unended_and_idle()
    {
        SessionService service = NewIdleService(out FakeClock clock);
        service.StartEntry("site visit");
        TimeEntry running = service.State.ActiveEntry!;

        clock.Advance(TimeSpan.FromMinutes(25));
        SessionResult result = service.StopCurrentEntry();

        Assert.Equal(SessionChange.EntryStopped, result.Change);
        Assert.False(result.IsActive);
        Assert.False(result.IsEnded);
        Assert.Null(result.ActiveEntry);

        TimeEntry stopped = Assert.Single(service.State.Entries);
        Assert.Equal(running.Id, stopped.Id);
        Assert.Equal(running.StartTime, stopped.StartTime);
        Assert.Equal(running.Task, stopped.Task);
        Assert.Equal(clock.Now, stopped.EndTime);
        Assert.True(stopped.IsComplete);
    }

    [Fact]
    public void Stop_and_start_completes_the_old_entry_and_starts_a_new_one_with_the_supplied_task()
    {
        SessionService service = NewIdleService(out FakeClock clock);
        service.StartEntry("site visit");

        clock.Advance(TimeSpan.FromMinutes(10));
        SessionResult result = service.RestartEntry("code review");

        Assert.Equal(SessionChange.EntryRestarted, result.Change);
        Assert.Equal([1, 2], service.State.Entries.Select(entry => entry.Id));

        TimeEntry previous = service.State.Entries[0];
        Assert.True(previous.IsComplete);
        Assert.Equal(clock.Now, previous.EndTime);
        Assert.Equal("site visit", previous.Task);

        TimeEntry current = result.ActiveEntry!;
        Assert.Equal(2, current.Id);
        Assert.Equal("code review", current.Task);
        Assert.Equal(string.Empty, current.Description);
        Assert.Equal(clock.Now, current.StartTime);
    }

    [Fact]
    public void Start_while_already_tracking_is_a_no_op()
    {
        SessionService service = NewIdleService(out FakeClock clock);
        service.StartEntry("site visit");
        SessionState before = service.State;

        clock.Advance(TimeSpan.FromMinutes(10));
        SessionResult result = service.StartEntry("code review");

        // Start is the NOT-tracking operation; switching tasks is Stop and start. A no-op is
        // recoverable, whereas splitting the running entry would rewrite the user's data.
        Assert.Equal(SessionChange.None, result.Change);
        Assert.False(result.Changed);
        Assert.Equal(before, service.State);
        Assert.Equal(TimeSpan.FromMinutes(10), clock.Now - service.State.ActiveEntry!.StartTime);
    }

    [Fact]
    public void Stop_and_start_with_no_task_supplied_uses_none()
    {
        SessionService service = NewIdleService(out FakeClock clock);
        service.StartEntry("site visit");

        clock.Advance(TimeSpan.FromMinutes(1));
        SessionResult result = service.RestartEntry();

        Assert.Equal(SessionChange.EntryRestarted, result.Change);
        Assert.Equal(TimeEntry.NoTask, result.ActiveEntry!.Task);
    }

    [Fact]
    public void Restart_entry_from_idle_starts_a_new_entry()
    {
        SessionService service = NewIdleService(out FakeClock clock);

        SessionResult result = service.RestartEntry("site visit");

        Assert.Equal(SessionChange.EntryStarted, result.Change);
        Assert.Equal(1, result.ActiveEntry!.Id);
        Assert.Equal("site visit", result.ActiveEntry.Task);
    }

    [Fact]
    public void Restart_entry_while_active_reports_a_restart()
    {
        SessionService service = NewIdleService(out FakeClock clock);
        service.StartEntry("site visit");
        clock.Advance(TimeSpan.FromMinutes(3));

        SessionResult result = service.RestartEntry("code review");

        Assert.Equal(SessionChange.EntryRestarted, result.Change);
        Assert.Equal([1, 2], service.State.Entries.Select(entry => entry.Id));
        Assert.Equal(2, result.ActiveEntry!.Id);
        Assert.Equal(clock.Now, service.State.Entries[0].EndTime);
    }

    [Fact]
    public void End_session_stops_active_work_and_stamps_ended_at()
    {
        SessionService service = NewIdleService(out FakeClock clock);
        service.StartEntry("site visit");

        clock.Advance(TimeSpan.FromMinutes(40));
        SessionResult result = service.EndSession();

        Assert.Equal(SessionChange.SessionEnded, result.Change);
        Assert.True(result.IsEnded);
        Assert.False(result.IsActive);

        TimeEntry entry = Assert.Single(service.State.Entries);
        Assert.True(entry.IsComplete);
        Assert.Equal(clock.Now, entry.EndTime);
        Assert.Equal(clock.Now, service.State.EndedAt);
    }

    [Fact]
    public void End_session_restamps_ended_at_on_every_call_matching_ttc()
    {
        SessionService service = NewIdleService(out FakeClock clock);
        service.StartEntry("site visit");
        clock.Advance(TimeSpan.FromMinutes(40));

        SessionResult first = service.EndSession();
        DateTimeOffset firstEnd = service.State.EndedAt!.Value;
        Assert.Equal(clock.Now, firstEnd);
        Assert.NotNull(first.AffectedEntry);

        clock.Advance(TimeSpan.FromHours(3));
        SessionResult second = service.EndSession();

        // TTC parity (decided): every call restamps EndedAt, so a repeat End rewrites when the
        // session ended. The entries are untouched, because nothing was running on the second call.
        Assert.Equal(SessionChange.SessionEnded, second.Change);
        Assert.True(second.Changed);
        Assert.Equal(clock.Now, service.State.EndedAt);
        Assert.NotEqual(firstEnd, service.State.EndedAt);
        Assert.Equal(first.State.Entries, second.State.Entries);
        Assert.Null(second.AffectedEntry);
    }

    [Fact]
    public void End_session_with_no_work_still_stamps_ended_at()
    {
        SessionService service = NewIdleService(out FakeClock clock);

        SessionResult result = service.EndSession();

        Assert.Equal(SessionChange.SessionEnded, result.Change);
        Assert.Equal(clock.Now, service.State.EndedAt);
        Assert.Empty(service.State.Entries);
    }

    [Fact]
    public void Stop_is_a_no_op_when_nothing_is_running()
    {
        SessionService service = NewIdleService(out FakeClock clock);
        service.StartEntry("site visit");
        service.StopCurrentEntry();
        SessionState before = service.State;

        SessionResult result = service.StopCurrentEntry();

        Assert.Equal(SessionChange.None, result.Change);
        Assert.False(result.Changed);
        Assert.Equal(before, service.State);
    }

    [Fact]
    public void Stopping_twice_does_not_move_the_first_end_time()
    {
        SessionService service = NewIdleService(out FakeClock clock);
        service.StartEntry("site visit");

        clock.Advance(TimeSpan.FromMinutes(5));
        service.StopCurrentEntry();
        DateTimeOffset? stoppedAt = Assert.Single(service.State.Entries).EndTime;

        clock.Advance(TimeSpan.FromHours(2));
        service.StopCurrentEntry();

        Assert.Equal(stoppedAt, Assert.Single(service.State.Entries).EndTime);
    }

    [Fact]
    public void Stop_tracking_performs_the_same_transition_as_stop_current_entry()
    {
        SessionService service = NewIdleService(out FakeClock clock);
        service.StartEntry("site visit");

        clock.Advance(TimeSpan.FromMinutes(7));
        SessionResult result = service.StopTracking();

        Assert.Equal(SessionChange.EntryStopped, result.Change);
        Assert.False(service.State.IsActive);
        Assert.Equal(clock.Now, Assert.Single(service.State.Entries).EndTime);
    }

    [Fact]
    public void Stop_tracking_is_a_no_op_when_nothing_is_running()
    {
        SessionService service = NewIdleService(out FakeClock clock);

        SessionResult result = service.StopTracking();

        Assert.Equal(SessionChange.None, result.Change);
        Assert.Empty(service.State.Entries);
    }

    [Fact]
    public void Play_reopens_an_ended_session()
    {
        // TTC: "resuming means the session is active again" - Resume sets EndedAt back to null
        SessionService service = NewIdleService(out FakeClock clock);
        service.StartEntry("site visit");
        service.EndSession();

        clock.Advance(TimeSpan.FromMinutes(15));
        SessionResult result = service.StartEntry("code review");

        Assert.Equal(SessionChange.EntryStarted, result.Change);
        Assert.False(result.IsEnded);
        Assert.Null(service.State.EndedAt);
        Assert.Equal(2, result.ActiveEntry!.Id);
    }

    [Fact]
    public void Entry_ids_keep_incrementing_across_repeated_restarts()
    {
        SessionService service = NewIdleService(out FakeClock clock);

        service.StartEntry("a");
        service.RestartEntry("b");
        service.RestartEntry("c");
        service.StopCurrentEntry();

        Assert.Equal([1, 2, 3], service.State.Entries.Select(entry => entry.Id));
        Assert.Equal(4, service.State.NextEntryId);
    }

    [Fact]
    public void Start_new_session_generates_its_name_and_starts_tracking_immediately()
    {
        FakeClock clock = FakeClock.AtCentral(2026, 9, 18, 16, 45, 30);

        SessionService service = SessionService.StartNewSession(clock);

        Assert.Equal("Session 2026-09-18 16:45:30", service.State.Name);
        Assert.Equal(clock.Now, service.State.StartedAt);
        Assert.Null(service.State.EndedAt);

        // TTC's CLI `new` opens an entry immediately, stamped BEFORE it prompts for a task
        TimeEntry entry = Assert.Single(service.State.Entries);
        Assert.Equal(1, entry.Id);
        Assert.Equal(TimeEntry.NoTask, entry.Task);
        Assert.Equal(string.Empty, entry.Description);
        Assert.Equal(clock.Now, entry.StartTime);
        Assert.True(service.State.IsActive);
    }

    [Fact]
    public void Start_new_session_keeps_a_supplied_name_and_still_starts_tracking()
    {
        FakeClock clock = FakeClock.AtCentral(2026, 9, 18, 16, 45, 30);

        SessionService service = SessionService.StartNewSession(clock, "site review");

        Assert.Equal("site review", service.State.Name);
        Assert.Single(service.State.Entries);
        Assert.True(service.State.IsActive);
    }

    [Fact]
    public void Applying_the_prompted_task_keeps_the_stamp_taken_before_the_prompt()
    {
        FakeClock clock = FakeClock.AtCentral(2026, 9, 18, 16, 45, 30);
        SessionService service = SessionService.StartNewSession(clock);
        DateTimeOffset stamped = service.State.ActiveEntry!.StartTime;

        // the UI's task prompt happens here, which is why the stamp is taken first
        clock.Advance(TimeSpan.FromSeconds(20));
        EntryEditResult edit = service.UpdateActiveEntry(new EntryEdit(Task: "site visit"));

        Assert.Equal(EntryEditOutcome.Applied, edit.Outcome);

        TimeEntry entry = Assert.Single(service.State.Entries);
        Assert.Equal("site visit", entry.Task);
        Assert.Equal(stamped, entry.StartTime);
    }

    [Fact]
    public void Result_exposes_the_cues_a_status_display_needs()
    {
        SessionService service = NewIdleService(out FakeClock clock);

        SessionResult idle = service.StartEntry();
        SessionResult running = service.RestartEntry("site visit");
        SessionResult stopped = service.StopCurrentEntry();

        // idle -> running: the status cue has a task to show
        Assert.True(idle.IsActive);
        Assert.Equal(TimeEntry.NoTask, idle.ActiveEntry!.Task);

        // restarting keeps a task to show and a new start time to render
        Assert.Equal("site visit", running.ActiveEntry!.Task);
        Assert.Equal(clock.Now, running.ActiveEntry.StartTime);

        // stopped: no active entry, so the status cue falls back to the idle text
        Assert.False(stopped.IsActive);
        Assert.Null(stopped.ActiveEntry);
        Assert.NotNull(stopped.AffectedEntry);
    }

    // ------------------------------------------------------------------
    // Task 1.3 - live task/description/logged editing rules
    // ------------------------------------------------------------------

    [Fact]
    public void Editing_a_task_trims_it_and_maps_blank_to_none()
    {
        FakeClock clock = new(Noon);
        SessionService service = new(StateWith(Entry(1, "weeding")), clock);

        EntryEditResult padded = service.UpdateEntry(1, new EntryEdit(Task: "  code review  "));
        EntryEditResult blank = service.UpdateEntry(1, new EntryEdit(Task: "   "));

        Assert.Equal(EntryEditOutcome.Applied, padded.Outcome);
        Assert.Equal("code review", padded.Entry!.Task);

        // blank visible text is "no task", exactly as on the insert path - not an empty task group
        Assert.Equal(TimeEntry.NoTask, blank.Entry!.Task);
    }

    [Fact]
    public void A_live_task_edit_keeps_the_original_start_time()
    {
        FakeClock clock = new(Noon);
        SessionService service = new(StateWith(Entry(1, "weeding")), clock);

        clock.Advance(TimeSpan.FromMinutes(25));
        EntryEditResult result = service.UpdateEntry(1, new EntryEdit(Task: "weeding the beds"));

        // the timer renders from the persisted start time (invariant 8), so an edit must not move it
        Assert.Equal(Noon, result.Entry!.StartTime);
        Assert.Equal(Noon.AddMinutes(25), clock.Now);
        Assert.Null(result.Entry.EndTime);
        Assert.True(result.Entry.IsOpen);
    }

    [Fact]
    public void A_description_edit_trims_and_keeps_blank_as_an_empty_string()
    {
        FakeClock clock = new(Noon);
        SessionService service = new(StateWith(Entry(1, "weeding")), clock);

        EntryEditResult padded = service.UpdateEntry(1, new EntryEdit(Description: "  back bed  "));
        EntryEditResult blank = service.UpdateEntry(1, new EntryEdit(Description: "   "));

        Assert.Equal("back bed", padded.Entry!.Description);

        // blank is an empty string, never the "none" sentinel the task field uses
        Assert.Equal(string.Empty, blank.Entry!.Description);
    }

    [Fact]
    public void Task_and_description_edits_are_allowed_on_completed_entries()
    {
        FakeClock clock = new(Noon);
        SessionService service = new(StateWith(Entry(1, "weeding", complete: true)), clock);

        EntryEditResult result = service.UpdateEntry(1, new EntryEdit(Task: "weeding", Description: "front bed"));

        Assert.Equal(EntryEditOutcome.Applied, result.Outcome);
        Assert.Equal("front bed", result.Entry!.Description);
        Assert.True(result.Entry.IsComplete);
    }

    [Fact]
    public void Editing_a_soft_deleted_entry_is_rejected_and_changes_nothing()
    {
        FakeClock clock = new(Noon);
        SessionState before = StateWith(Entry(1, "weeding", complete: true, deleted: true));
        SessionService service = new(before, clock);

        EntryEditResult result = service.UpdateEntry(
            1,
            new EntryEdit(Task: "renamed", Description: "x", Logged: true));

        Assert.Equal(EntryEditOutcome.EntryDeleted, result.Outcome);
        Assert.False(result.Applied);
        Assert.Equal(before, result.State);
    }

    [Fact]
    public void An_unknown_entry_id_is_reported_rather_than_ignored()
    {
        FakeClock clock = new(Noon);
        SessionState before = StateWith(Entry(1, "weeding"));
        SessionService service = new(before, clock);

        EntryEditResult result = service.UpdateEntry(99, new EntryEdit(Task: "x"));

        Assert.Equal(EntryEditOutcome.EntryNotFound, result.Outcome);
        Assert.Equal(before, result.State);
    }

    [Fact]
    public void An_edit_request_with_no_fields_is_reported_rather_than_written()
    {
        FakeClock clock = new(Noon);
        SessionState before = StateWith(Entry(1, "weeding"));
        SessionService service = new(before, clock);

        EntryEditResult result = service.UpdateEntry(1, new EntryEdit());

        Assert.Equal(EntryEditOutcome.NothingToDo, result.Outcome);
        Assert.Equal(before, result.State);
    }

    [Fact]
    public void Logged_state_is_writable_only_for_completed_named_undeleted_entries()
    {
        FakeClock clock = new(Noon);
        SessionService service = new(
            StateWith(
                Entry(1, "weeding"),
                Entry(2, "reading", complete: true),
                Entry(3, "none", complete: true)),
            clock);

        // completed and named: the one case that has a logged state
        Assert.Equal(EntryEditOutcome.Applied, service.UpdateEntry(2, new EntryEdit(Logged: true)).Outcome);
        Assert.True(service.State.Entries.Single(entry => entry.Id == 2).Logged);

        // still in progress: nothing to log yet
        Assert.Equal(
            EntryEditOutcome.LoggedNotApplicable,
            service.UpdateEntry(1, new EntryEdit(Logged: true)).Outcome);
        Assert.False(service.State.Entries.Single(entry => entry.Id == 1).Logged);

        // "none" is untracked time, not a task group, so it has no logged state either
        Assert.Equal(
            EntryEditOutcome.LoggedNotApplicable,
            service.UpdateEntry(3, new EntryEdit(Logged: true)).Outcome);
        Assert.False(service.State.Entries.Single(entry => entry.Id == 3).Logged);
    }

    [Fact]
    public void A_none_task_is_recognised_case_insensitively_for_logged_state()
    {
        FakeClock clock = new(Noon);
        SessionService service = new(StateWith(Entry(1, "None", complete: true)), clock);

        Assert.Equal(
            EntryEditOutcome.LoggedNotApplicable,
            service.UpdateEntry(1, new EntryEdit(Logged: true)).Outcome);
    }

    [Fact]
    public void A_combined_edit_applies_every_field_in_one_call()
    {
        FakeClock clock = new(Noon);
        SessionService service = new(StateWith(Entry(1, "reading", complete: true)), clock);

        EntryEditResult result = service.UpdateEntry(
            1,
            new EntryEdit(Task: "reading", Description: "chapter 4", Logged: true));

        Assert.Equal(EntryEditOutcome.Applied, result.Outcome);
        Assert.Equal("reading", result.Entry!.Task);
        Assert.Equal("chapter 4", result.Entry.Description);
        Assert.True(result.Entry.Logged);
    }

    [Fact]
    public void An_edit_that_would_change_nothing_reports_unchanged()
    {
        FakeClock clock = new(Noon);
        SessionService service = new(StateWith(Entry(1, "weeding")), clock);

        service.UpdateEntry(1, new EntryEdit(Task: "weeding", Description: "back bed"));
        SessionState afterFirst = service.State;

        // same values again, this time padded - normalization means it is still a no-op
        EntryEditResult again = service.UpdateEntry(1, new EntryEdit(Task: "  weeding  ", Description: "back bed"));

        Assert.Equal(EntryEditOutcome.Unchanged, again.Outcome);
        Assert.Equal(afterFirst, again.State);
    }

    [Fact]
    public void The_live_edit_operation_targets_the_active_entry_only()
    {
        FakeClock clock = new(Noon);
        SessionService service = new(
            StateWith(Entry(1, "weeding", complete: true), Entry(2, "reading")),
            clock);

        EntryEditResult result = service.UpdateActiveEntry(new EntryEdit(Task: "reading aloud"));

        Assert.Equal(EntryEditOutcome.Applied, result.Outcome);
        Assert.Equal(2, result.Entry!.Id);

        // the completed entry is not the active one, so it must be left exactly as it was
        Assert.Equal("weeding", service.State.Entries.Single(entry => entry.Id == 1).Task);
    }

    [Fact]
    public void The_live_edit_operation_reports_when_nothing_is_running()
    {
        FakeClock clock = new(Noon);
        SessionState before = StateWith(Entry(1, "weeding", complete: true));
        SessionService service = new(before, clock);

        EntryEditResult result = service.UpdateActiveEntry(new EntryEdit(Task: "x"));

        Assert.Equal(EntryEditOutcome.NoActiveEntry, result.Outcome);
        Assert.Equal(before, result.State);
    }

    [Fact]
    public void The_public_domain_api_exposes_no_start_or_end_time_editing()
    {
        // the plan forbids time editing in the v1 public API. A name canary is the cheap way to stop
        // one creeping in later, the same way the field-name drift canary guards the JSON contract.
        string[] offenders =
        [
            .. typeof(SessionService)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Select(method => method.Name)
                .Where(name => name.Contains("time", StringComparison.OrdinalIgnoreCase)),
        ];

        Assert.Empty(offenders);
    }

    // ------------------------------------------------------------------
    // Task 1.5 - resume normalization and chooser metadata
    // ------------------------------------------------------------------

    [Fact]
    public void Resuming_a_session_with_no_unfinished_entry_leaves_it_idle()
    {
        SessionState state = StateWith(Entry(1, "weeding", complete: true));

        SessionResumeResult result = SessionResumeService.Resume(state, new FakeClock(Noon));

        Assert.True(result.Resumed);
        Assert.False(result.Session!.State.IsActive);

        // resuming must not mint an entry: a finished session opens idle until the user starts work
        Assert.Single(result.Session.State.Entries);
        Assert.Null(result.Session.State.ActiveEntry);
    }

    [Fact]
    public void Resuming_an_unfinished_session_keeps_the_running_entry_and_its_start_time()
    {
        SessionState state = StateWith(Entry(1, "weeding"), Entry(2, "reading", complete: true));
        FakeClock clock = new(Noon.AddHours(2));

        SessionResumeResult result = SessionResumeService.Resume(state, clock);

        Assert.True(result.Resumed);
        Assert.True(result.Session!.State.IsActive);

        // the original stamp IS the data: resuming must never restamp it to "now"
        Assert.Equal(Noon, result.Session.State.ActiveEntry!.StartTime);
        Assert.Equal(Noon.AddHours(2), clock.Now);
    }

    [Fact]
    public void A_resumed_session_mints_ids_past_the_highest_stored_id()
    {
        // v1's hard delete left real gaps, so max+1 - never the entry count - is the safe next id
        SessionState state = StateWith(
            Entry(1, "weeding", complete: true),
            Entry(7, "reading", complete: true, deleted: true));

        SessionResumeResult result = SessionResumeService.Resume(state, new FakeClock(Noon));

        Assert.Equal(8, result.Session!.State.NextEntryId);
    }

    [Fact]
    public void A_corrupt_session_with_two_unfinished_entries_is_refused_not_repaired()
    {
        // plan Q3: refuse it, name the entries, and do not silently pick one
        SessionState state = StateWith(Entry(1, "weeding"), Entry(2, "reading"));

        SessionResumeResult result = SessionResumeService.Resume(state, new FakeClock(Noon));

        Assert.False(result.Resumed);
        Assert.Equal(SessionResumeOutcome.CorruptMultipleUnfinishedEntries, result.Outcome);
        Assert.Equal([1, 2], result.UnfinishedEntryIds);

        // no session comes back at all, so nothing can be opened read-only, repaired or exported
        Assert.Null(result.Session);
        Assert.NotEmpty(result.Reason);
    }

    [Fact]
    public void A_deleted_unfinished_entry_does_not_make_a_session_corrupt()
    {
        // active state is derived from non-deleted incomplete entries, so a deleted stub cannot block a load
        SessionState state = StateWith(Entry(1, "weeding"), Entry(2, "reading", deleted: true));

        SessionResumeResult result = SessionResumeService.Resume(state, new FakeClock(Noon));

        Assert.True(result.Resumed);
        Assert.Equal(1, result.Session!.State.ActiveEntry!.Id);
    }

    [Fact]
    public void Chooser_metadata_carries_name_start_time_state_and_tracked_time()
    {
        SessionState state = StateWith(
            Entry(1, "weeding", complete: true),
            Entry(2, "none", complete: true));

        SessionListItem item = SessionListProjection.From(state);

        Assert.Equal("fixture", item.Name);
        Assert.Equal(Noon, item.StartedAt);
        Assert.True(item.IsUnfinished);
        Assert.False(item.IsActive);

        // tracked time is the session's own total, so untracked ("none") time counts here - unlike the
        // summary's named totals, which exist to answer a different question
        Assert.Equal(TimeSpan.FromMinutes(2), item.Tracked);
    }

    [Fact]
    public void Chooser_tracked_time_excludes_the_running_entry_and_deleted_work()
    {
        SessionState state = StateWith(
            Entry(1, "weeding", complete: true),
            Entry(2, "reading", complete: true, deleted: true),
            Entry(3, "reading"));

        SessionListItem item = SessionListProjection.From(state);

        Assert.Equal(TimeSpan.FromMinutes(1), item.Tracked);
        Assert.True(item.IsActive);
    }

    [Fact]
    public void Chooser_rows_are_newest_first_with_a_deterministic_tie_break()
    {
        SessionState oldest = SessionState.New("oldest", Noon);
        SessionState middle = SessionState.New("middle", Noon.AddHours(1));
        SessionState newest = SessionState.New("newest", Noon.AddHours(2));
        SessionState tied = SessionState.New("tied", Noon.AddHours(1));

        IReadOnlyList<SessionListItem> items =
            SessionListProjection.NewestFirst([middle, oldest, newest, tied]);

        // the tie is broken by name, so the order does not depend on the order files were listed in
        Assert.Equal(["newest", "middle", "tied", "oldest"], items.Select(item => item.Name));
    }

    [Fact]
    public void Chooser_falls_back_to_a_readable_name_for_an_unnamed_session()
    {
        SessionState state = SessionState.New("placeholder", Noon) with { Name = "   " };

        Assert.Equal("Unnamed session", SessionListProjection.From(state).Name);
    }
}