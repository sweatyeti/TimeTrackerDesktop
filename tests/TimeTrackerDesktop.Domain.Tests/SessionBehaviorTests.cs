using System.Globalization;
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

        // TTC tests IsNullOrEmpty, not IsNullOrWhiteSpace - a whitespace-only name is KEPT, not
        // replaced with a generated one. Preserved deliberately; it changes the file name a session
        // produces, so "tidying" it here would silently move where a session is written.
        Assert.Equal("   ", SessionState.New("   ", Noon).Name);
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
    public void Starting_again_completes_the_open_entry_at_the_new_clock_time()
    {
        FakeClock clock = FakeClock.AtCentral(2026, 9, 18, 9, 0, 0);
        SessionService service = new(SessionState.New("demo", clock.Now), clock);

        service.StartEntry("first");
        clock.Advance(TimeSpan.FromMinutes(5));
        service.StartEntry("second");

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
}