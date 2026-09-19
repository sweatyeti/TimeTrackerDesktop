using System.Globalization;
using TimeTrackerDesktop.Domain;
using TimeTrackerDesktop.ViewModels;

namespace TimeTrackerDesktop.App.Tests;

/// <summary>
/// Plan Task 4.2: the compact widget's active and idle behaviour.
///
/// The two rules with teeth are that the commands bind to the <see cref="SessionService"/> rather than
/// editing state themselves — so Play really is stop-and-start and Stop really only stops — and that the
/// active/idle distinction never rests on colour alone.
/// </summary>
public sealed class WidgetViewModelTests
{
    private static readonly DateTimeOffset Nine =
        DateTimeOffset.Parse("2026-09-19T09:00:00-05:00", CultureInfo.InvariantCulture);

    private static WidgetViewModel RunningWidget(out SessionService session, string? task = "weeding")
    {
        session = SessionService.StartNewSession(new FixedClock(Nine), "test session");
        session.UpdateActiveEntry(new EntryEdit(Task: task));

        return new WidgetViewModel(session, new FixedClock(Nine.AddMinutes(65)));
    }

    private static WidgetViewModel IdleWidget(out SessionService session)
    {
        session = SessionService.StartNewSession(new FixedClock(Nine), "test session");
        session.StopCurrentEntry();

        return new WidgetViewModel(session, new FixedClock(Nine.AddMinutes(65)));
    }

    // ------------------------------------------------------------------ idle state

    [Fact]
    public void Idle_shows_the_plans_literal_wording()
    {
        WidgetViewModel widget = IdleWidget(out _);

        Assert.True(widget.IsIdle);
        Assert.False(widget.IsActive);
        Assert.Equal("No task running", widget.StateLabel);
        Assert.Equal(WidgetViewModel.IdleText, widget.StateLabel);
    }

    [Fact]
    public void Idle_hides_the_task_field_and_the_description_control()
    {
        WidgetViewModel widget = IdleWidget(out _);

        Assert.False(widget.IsTaskFieldVisible);
        Assert.False(widget.IsDescriptionVisible);
    }

    [Fact]
    public void Idle_keeps_play_available_and_disables_stop()
    {
        WidgetViewModel widget = IdleWidget(out _);

        Assert.True(widget.PlayCommand.CanExecute(null));
        Assert.False(widget.StopCommand.CanExecute(null));
        Assert.False(widget.CanStop);
    }

    [Fact]
    public void Idle_has_no_started_or_duration_display()
    {
        WidgetViewModel widget = IdleWidget(out _);

        Assert.Equal(string.Empty, widget.StartedDisplay);
        Assert.Equal(string.Empty, widget.ElapsedDisplay);
    }

    // ------------------------------------------------------------------ active state

    [Fact]
    public void Active_shows_the_task_and_keeps_both_controls_visible()
    {
        WidgetViewModel widget = RunningWidget(out _);

        Assert.True(widget.IsActive);
        Assert.Equal("weeding", widget.StateLabel);
        Assert.Equal("weeding", widget.TaskText);
        Assert.True(widget.IsTaskFieldVisible);
        Assert.True(widget.IsDescriptionVisible);
    }

    [Fact]
    public void An_unset_task_reads_as_a_blank_field_not_as_none()
    {
        // TTC stores "none"; a user must never see that word
        WidgetViewModel widget = RunningWidget(out _, task: null);

        Assert.Equal(string.Empty, widget.TaskText);
        Assert.Equal(WidgetViewModel.NoTaskText, widget.StateLabel);
        Assert.DoesNotContain("none", widget.StateLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Active_shows_the_start_time_and_a_ticking_duration()
    {
        WidgetViewModel widget = RunningWidget(out _);

        Assert.Equal("Started 9:00 AM", widget.StartedDisplay);
        Assert.Equal("1:05:00", widget.ElapsedDisplay);
        Assert.Equal("1h 05m", widget.ElapsedRoundedDisplay);
    }

    [Fact]
    public void The_duration_is_derived_from_the_entry_start_so_it_cannot_drift()
    {
        SessionService session = SessionService.StartNewSession(new FixedClock(Nine), "test session");

        WidgetViewModel widget = new(session, new FixedClock(Nine.AddMinutes(5)));
        Assert.Equal("0:05:00", widget.ElapsedDisplay);

        // a clock that jumps forward — a suspended process, a missed tick — must not accumulate error
        WidgetViewModel later = new(session, new FixedClock(Nine.AddHours(3)));
        Assert.Equal("3:00:00", later.ElapsedDisplay);
    }

    [Fact]
    public void Active_keeps_play_available_and_enables_stop()
    {
        WidgetViewModel widget = RunningWidget(out _);

        Assert.True(widget.PlayCommand.CanExecute(null));
        Assert.True(widget.StopCommand.CanExecute(null));
        Assert.True(widget.CanStop);
    }

    // ------------------------------------------------------------------ Play: restart semantics

    [Fact]
    public void Play_from_idle_starts_a_task()
    {
        WidgetViewModel widget = IdleWidget(out SessionService session);

        widget.PlayCommand.Execute(null);

        Assert.True(widget.IsActive);
        Assert.NotNull(session.State.ActiveEntry);
        Assert.Equal("Started tracking.", widget.Message);
    }

    [Fact]
    public void Play_while_running_completes_the_previous_entry_and_opens_a_new_one()
    {
        // the plan's exact restart semantics: this is not a toggle, and not a second Stop
        WidgetViewModel widget = RunningWidget(out SessionService session);

        widget.PlayCommand.Execute(null);

        Assert.True(widget.IsActive);
        Assert.Equal(2, session.State.Entries.Count);
        Assert.Single(session.State.Entries, entry => entry.IsOpen);
        Assert.Single(session.State.Entries, entry => entry.IsComplete);
        Assert.Equal("Stopped the previous task and started a new one.", widget.Message);
    }

    [Fact]
    public void Play_starts_the_new_entry_with_no_task_so_the_field_is_blank()
    {
        WidgetViewModel widget = RunningWidget(out SessionService session);

        widget.PlayCommand.Execute(null);

        Assert.Equal(string.Empty, widget.TaskText);
        Assert.Equal(WidgetViewModel.NoTaskText, widget.StateLabel);
        Assert.True(session.State.ActiveEntry!.HasNoTask);
    }

    // ------------------------------------------------------------------ Stop: stops only

    [Fact]
    public void Stop_while_running_stops_and_opens_nothing()
    {
        WidgetViewModel widget = RunningWidget(out SessionService session);

        widget.StopCommand.Execute(null);

        Assert.True(widget.IsIdle);
        Assert.Null(session.State.ActiveEntry);
        Assert.Single(session.State.Entries);
        Assert.Equal("Stopped tracking.", widget.Message);
    }

    [Fact]
    public void Stop_while_idle_is_a_no_op_and_does_not_open_an_entry()
    {
        WidgetViewModel widget = IdleWidget(out SessionService session);

        int before = session.State.Entries.Count;

        // the command is disabled while idle — the plan's "disable/visually unavailable Stop" — so this is
        // blocked before it ever reaches the session
        Assert.False(widget.StopCommand.CanExecute(null));

        widget.StopCommand.Execute(null);

        Assert.Equal(before, session.State.Entries.Count);
        Assert.DoesNotContain(session.State.Entries, entry => entry.IsOpen);

        // and the operation is a safe no-op even if something calls it directly
        widget.Stop();

        Assert.Equal(before, session.State.Entries.Count);
        Assert.Equal("Nothing was running.", widget.Message);
    }

    [Fact]
    public void Stopping_leaves_the_session_open()
    {
        // stopping is not ending: the session must stay usable for the next task
        WidgetViewModel widget = RunningWidget(out SessionService session);

        widget.Stop();

        Assert.Null(session.State.EndedAt);
        Assert.False(session.State.IsActive);
    }

    // ------------------------------------------------------------------ editing

    [Fact]
    public void Committing_a_task_writes_it_to_the_running_entry()
    {
        WidgetViewModel widget = RunningWidget(out _);

        widget.TaskText = "painting";
        widget.CommitTask();

        Assert.Equal("painting", widget.StateLabel);
        Assert.Equal("painting", widget.Session.State.ActiveEntry!.Task);
    }

    [Fact]
    public void Committing_a_blank_task_writes_no_task()
    {
        WidgetViewModel widget = RunningWidget(out _);

        widget.TaskText = string.Empty;
        widget.CommitTask();

        Assert.True(widget.Session.State.ActiveEntry!.HasNoTask);
        Assert.Equal(WidgetViewModel.NoTaskText, widget.StateLabel);
    }

    [Fact]
    public void Committing_a_description_leaves_the_task_alone()
    {
        // the edit is per-field: a description-only change must not restate the task
        WidgetViewModel widget = RunningWidget(out _);

        widget.DescriptionText = "the north bed";
        widget.CommitDescription();

        Assert.Equal("the north bed", widget.Session.State.ActiveEntry!.Description);
        Assert.Equal("weeding", widget.Session.State.ActiveEntry!.Task);
    }

    [Fact]
    public void Editing_is_refused_when_nothing_is_running()
    {
        WidgetViewModel widget = IdleWidget(out _);

        widget.TaskText = "painting";
        widget.CommitTask();

        Assert.Equal("No entry is running to edit.", widget.Message);
    }

    [Fact]
    public void An_editing_session_does_not_survive_the_entry_stopping()
    {
        // the idle state hides the field, so leaving it open would be an invisible editor
        WidgetViewModel widget = RunningWidget(out _);

        widget.EditTaskCommand.Execute(null);
        Assert.True(widget.IsEditingTask);

        widget.Stop();

        Assert.False(widget.IsEditingTask);
    }

    // ------------------------------------------------------------------ accessibility

    [Fact]
    public void Every_icon_button_has_a_name_and_a_tooltip()
    {
        WidgetViewModel active = RunningWidget(out _);
        WidgetViewModel idle = IdleWidget(out _);

        foreach(WidgetViewModel widget in new[] { active, idle })
        {
            Assert.False(string.IsNullOrWhiteSpace(widget.PlayAccessibleName));
            Assert.False(string.IsNullOrWhiteSpace(widget.StopAccessibleName));
            Assert.False(string.IsNullOrWhiteSpace(widget.PlayTooltip));
            Assert.False(string.IsNullOrWhiteSpace(widget.StopTooltip));
        }
    }

    [Fact]
    public void Play_and_stop_are_distinguishable_without_seeing_them()
    {
        // colour alone must never carry the meaning: the two commands differ by wording in every state
        WidgetViewModel widget = RunningWidget(out _);

        Assert.NotEqual(widget.PlayAccessibleName, widget.StopAccessibleName);
        Assert.NotEqual(widget.PlayTooltip, widget.StopTooltip);
    }

    [Fact]
    public void Play_explains_that_it_restarts_rather_than_merely_starting()
    {
        WidgetViewModel widget = RunningWidget(out _);

        Assert.Contains("new one", widget.PlayTooltip, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Active_and_idle_differ_by_wording_not_only_by_colour()
    {
        WidgetViewModel active = RunningWidget(out _);
        WidgetViewModel idle = IdleWidget(out _);

        Assert.NotEqual(active.StateLabel, idle.StateLabel);
        Assert.Equal("No task running", idle.StateLabel);
    }

    /// <summary>A clock that does not move, so assertions are about behaviour rather than the wall.</summary>
    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset Now { get; } = now;
    }
}
