using System.Globalization;
using TimeTrackerDesktop.Domain;
using TimeTrackerDesktop.Persistence;
using TimeTrackerDesktop.ViewModels;

namespace TimeTrackerDesktop.App.Tests;

/// <summary>
/// Plan Task 4.1: the session chooser and the app's entry state.
///
/// The chooser is the first thing the app shows, so its rules are the app's first impressions: newest first,
/// a row that says enough to choose from, a primary action that starts timing without asking for anything, and
/// a refusal that explains itself rather than a button that does nothing.
/// </summary>
public sealed class SessionChooserViewModelTests
{
    private static readonly DateTimeOffset Morning =
        DateTimeOffset.Parse("2026-09-18T09:30:00-05:00", CultureInfo.InvariantCulture);

    private static string NewFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ttd-chooser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void WriteSession(
        string folder,
        string name,
        DateTimeOffset startedAt,
        DateTimeOffset? endedAt = null,
        params (string Task, int Minutes, bool Complete)[] entries)
    {
        SessionState state = SessionState.New(name, startedAt);

        int id = 1;

        foreach((string task, int minutes, bool complete) in entries)
        {
            TimeEntry entry = TimeEntry.Start(id++, startedAt, task);

            if(complete)
            {
                entry = entry.Complete(startedAt.AddMinutes(minutes));
            }

            state = state with { Entries = [.. state.Entries, entry] };
        }

        if(endedAt is not null)
        {
            state = state with { EndedAt = endedAt };
        }

        string path = Path.Combine(folder, SessionFileNaming.Slugify(name) + ".json");

        new AtomicSessionStore(folder).Write(path, SessionDocument.FromSessionState(state));
    }

    private static SessionChooserViewModel CreateChooser(string folder, IClock clock) =>
        new(new AtomicSessionStore(folder), folder, clock);

    // ------------------------------------------------------------------ listing

    [Fact]
    public void Sessions_are_listed_newest_first()
    {
        string folder = NewFolder();

        WriteSession(folder, "oldest", Morning, Morning.AddHours(1), ("writing", 30, true));
        WriteSession(folder, "newest", Morning.AddDays(2), Morning.AddDays(2).AddHours(1), ("writing", 30, true));
        WriteSession(folder, "middle", Morning.AddDays(1), Morning.AddDays(1).AddHours(1), ("writing", 30, true));

        SessionChooserViewModel chooser = CreateChooser(folder, new FixedClock(Morning.AddDays(3)));
        chooser.Refresh();

        Assert.Equal(["newest", "middle", "oldest"], chooser.Choices.Select(choice => choice.Name));
        Assert.True(chooser.HasSessions);
    }

    [Fact]
    public void A_row_carries_the_name_start_time_state_and_tracked_time()
    {
        string folder = NewFolder();

        WriteSession(folder, "site visit", Morning, Morning.AddHours(2), ("survey", 45, true), ("report", 20, true));

        SessionChooserViewModel chooser = CreateChooser(folder, new FixedClock(Morning.AddDays(1)));
        chooser.Refresh();

        SessionChoice row = Assert.Single(chooser.Choices);

        Assert.Equal("site visit", row.Name);
        Assert.Equal(Morning, row.StartedAt);
        Assert.Equal("2026-09-18 09:30", row.StartedDisplay);
        Assert.Equal("Finished", row.StateLabel);
        Assert.Equal(TimeSpan.FromMinutes(65), row.Tracked);
        Assert.Equal("1h 05m", row.TrackedDisplay);
    }

    [Fact]
    public void A_running_session_is_labelled_active_not_merely_unfinished()
    {
        // "active" is the case a user most needs to recognise, and it is text rather than colour alone
        string folder = NewFolder();

        WriteSession(folder, "running", Morning, null, ("survey", 10, true), ("next", 0, false));
        WriteSession(folder, "abandoned", Morning.AddDays(-1), null, ("survey", 10, true));

        SessionChooserViewModel chooser = CreateChooser(folder, new FixedClock(Morning.AddDays(1)));
        chooser.Refresh();

        Assert.Equal("Active", chooser.Choices.Single(choice => choice.Name == "running").StateLabel);
        Assert.Equal("Unfinished", chooser.Choices.Single(choice => choice.Name == "abandoned").StateLabel);
    }

    [Fact]
    public void The_running_entry_is_not_counted_as_tracked_time()
    {
        // otherwise the number creeps upward while the chooser sits open
        string folder = NewFolder();

        WriteSession(folder, "running", Morning, null, ("survey", 30, true), ("in progress", 0, false));

        SessionChooserViewModel chooser = CreateChooser(folder, new FixedClock(Morning.AddHours(5)));
        chooser.Refresh();

        Assert.Equal("30m", Assert.Single(chooser.Choices).TrackedDisplay);
    }

    [Fact]
    public void An_unreadable_file_is_skipped_and_reported_rather_than_hiding_every_session()
    {
        string folder = NewFolder();

        WriteSession(folder, "good", Morning, Morning.AddHours(1), ("writing", 30, true));
        File.WriteAllText(Path.Combine(folder, "broken.json"), "{ not json");

        SessionChooserViewModel chooser = CreateChooser(folder, new FixedClock(Morning.AddDays(1)));
        chooser.Refresh();

        Assert.Single(chooser.Choices);
        Assert.Contains("broken.json", chooser.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_storage_folder_lists_nothing_and_says_nothing_alarming()
    {
        SessionChooserViewModel chooser = CreateChooser(NewFolder(), new FixedClock(Morning));
        chooser.Refresh();

        Assert.Empty(chooser.Choices);
        Assert.False(chooser.HasSessions);
        Assert.Equal(string.Empty, chooser.Message);
    }

    // ------------------------------------------------------------------ starting

    [Fact]
    public void Starting_a_new_session_times_immediately_with_no_task()
    {
        SessionChooserViewModel chooser = CreateChooser(NewFolder(), new FixedClock(Morning));

        SessionService session = chooser.StartNewSession();

        Assert.True(session.State.IsActive, "a new session must start timing immediately");

        TimeEntry? running = session.State.ActiveEntry;
        Assert.NotNull(running);
        Assert.Equal(Morning, running.StartTime);
        Assert.Equal(string.Empty, TaskDisplay.Visible(running.Task));
    }

    [Fact]
    public void Starting_a_new_session_asks_for_no_name()
    {
        // the plan is explicit: no naming prompt. The name is generated, so the call takes no name argument.
        SessionChooserViewModel chooser = CreateChooser(NewFolder(), new FixedClock(Morning));

        SessionService session = chooser.StartNewSession();

        Assert.False(string.IsNullOrWhiteSpace(session.State.Name));
    }

    [Fact]
    public void A_new_session_starts_with_no_ended_time()
    {
        SessionChooserViewModel chooser = CreateChooser(NewFolder(), new FixedClock(Morning));

        Assert.Null(chooser.StartNewSession().State.EndedAt);
    }

    // ------------------------------------------------------------------ resuming

    [Fact]
    public void Resuming_the_selected_session_keeps_its_identity_and_entries()
    {
        string folder = NewFolder();

        WriteSession(folder, "yesterday", Morning.AddDays(-1), Morning.AddDays(-1).AddHours(3), ("survey", 40, true));

        SessionChooserViewModel chooser = CreateChooser(folder, new FixedClock(Morning));
        chooser.Refresh();

        chooser.SelectedChoice = Assert.Single(chooser.Choices);
        SessionService session = Assert.IsType<SessionService>(chooser.ResumeSelected());

        Assert.Equal("yesterday", session.State.Name);
        Assert.Single(session.State.Entries);
        Assert.Equal("survey", session.State.Entries[0].Task);
        Assert.False(session.State.IsActive, "resuming must not invent a running entry");
    }

    [Fact]
    public void Resuming_without_a_selection_reports_that_one_must_be_chosen()
    {
        SessionChooserViewModel chooser = CreateChooser(NewFolder(), new FixedClock(Morning));

        Assert.Null(chooser.ResumeSelected());
        Assert.Equal("Select a session first.", chooser.Message);
        Assert.False(chooser.CanResume);
    }

    [Fact]
    public void Selecting_a_row_enables_resuming()
    {
        string folder = NewFolder();

        WriteSession(folder, "yesterday", Morning.AddDays(-1), Morning.AddDays(-1).AddHours(1), ("survey", 10, true));

        SessionChooserViewModel chooser = CreateChooser(folder, new FixedClock(Morning));
        chooser.Refresh();

        Assert.False(chooser.CanResume);

        chooser.SelectedChoice = Assert.Single(chooser.Choices);

        Assert.True(chooser.CanResume);
    }

    [Fact]
    public void A_corrupt_session_is_refused_with_a_reason_and_returns_no_session()
    {
        // more than one unfinished non-deleted entry: refused rather than repaired, and the user is told why
        string folder = NewFolder();

        SessionState corrupt = SessionState.New("corrupt", Morning) with
        {
            Entries =
            [
                TimeEntry.Start(1, Morning, "one"),
                TimeEntry.Start(2, Morning.AddMinutes(5), "two"),
            ],
        };

        new AtomicSessionStore(folder).Write(
            Path.Combine(folder, "corrupt.json"),
            SessionDocument.FromSessionState(corrupt));

        SessionChooserViewModel chooser = CreateChooser(folder, new FixedClock(Morning.AddHours(1)));
        chooser.Refresh();

        chooser.SelectedChoice = Assert.Single(chooser.Choices);

        Assert.Null(chooser.ResumeSelected());
        Assert.False(string.IsNullOrWhiteSpace(chooser.Message));
        Assert.Contains("1", chooser.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ display rules

    [Theory]
    [InlineData("none", "")]
    [InlineData("NONE", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("  ", "")]
    [InlineData("weeding", "weeding")]
    [InlineData("  weeding  ", "weeding")]
    public void A_task_of_none_reads_as_blank(string? task, string expected) =>
        Assert.Equal(expected, TaskDisplay.Visible(task));

    [Theory]
    [InlineData(0, "0m")]
    [InlineData(59, "59m")]
    [InlineData(60, "1h 00m")]
    [InlineData(65, "1h 05m")]
    [InlineData(125, "2h 05m")]
    public void Tracked_time_reads_as_hours_and_minutes(int minutes, string expected) =>
        Assert.Equal(expected, SessionChoice.FormatTracked(TimeSpan.FromMinutes(minutes)));

    /// <summary>A clock that does not move, so every assertion is about behaviour rather than the wall.</summary>
    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset Now { get; } = now;
    }
}
