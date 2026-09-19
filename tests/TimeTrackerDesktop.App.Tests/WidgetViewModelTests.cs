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

    // ------------------------------------------------------------------ timer display (Task 4.3)

    private static WidgetViewModel WidgetWith(UserPreferences preferences, out SessionService session)
    {
        session = SessionService.StartNewSession(new FixedClock(Nine), "test session");
        session.UpdateActiveEntry(new EntryEdit(Task: "weeding"));

        return new WidgetViewModel(session, new FixedClock(Nine.AddMinutes(65)), preferences);
    }

    [Fact]
    public void Elapsed_mode_shows_a_ticking_duration_and_keeps_the_start_time_in_the_tooltip()
    {
        WidgetViewModel widget = WidgetWith(
            UserPreferences.Default with { TimerDisplay = TimerDisplayMode.Elapsed }, out _);

        Assert.Equal("1:05:00", widget.TimerText);
        Assert.Contains("9:00 AM", widget.TimerTooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void StartedTime_mode_shows_the_start_time_and_keeps_the_duration_in_the_tooltip()
    {
        WidgetViewModel widget = WidgetWith(
            UserPreferences.Default with { TimerDisplay = TimerDisplayMode.StartedTime }, out _);

        Assert.Equal("Started 9:00 AM", widget.TimerText);
        Assert.Contains("1:05:00", widget.TimerTooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void The_hidden_half_of_the_timer_stays_reachable_as_text()
    {
        // the preference chooses what is emphasised, not what exists: whichever half is not on the face is
        // still spelled out in the tooltip, so the timer never loses information silently
        WidgetViewModel elapsed = WidgetWith(
            UserPreferences.Default with { TimerDisplay = TimerDisplayMode.Elapsed }, out _);

        WidgetViewModel started = WidgetWith(
            UserPreferences.Default with { TimerDisplay = TimerDisplayMode.StartedTime }, out _);

        Assert.NotEqual(elapsed.TimerText, started.TimerText);
        Assert.NotEqual(elapsed.TimerTooltip, started.TimerTooltip);
        Assert.False(string.IsNullOrWhiteSpace(elapsed.TimerTooltip));
        Assert.False(string.IsNullOrWhiteSpace(started.TimerTooltip));
    }

    [Fact]
    public void Ticking_refreshes_the_timer_without_moving_the_persisted_start()
    {
        // the plan's rule with teeth: the clock drives the display, and only the display. A tick that
        // restamped the entry would rewrite the user's recorded work every second.
        SessionService session = SessionService.StartNewSession(new FixedClock(Nine), "test session");

        MutableClock clock = new(Nine.AddMinutes(5));

        WidgetViewModel widget = new(
            session, clock, UserPreferences.Default with { TimerDisplay = TimerDisplayMode.Elapsed });

        DateTimeOffset persistedStart = session.State.ActiveEntry!.StartTime;
        DateTimeOffset persistedNow = session.State.ActiveEntry!.StartTime;
        string before = widget.TimerText;

        clock.Now = Nine.AddMinutes(6);
        widget.Tick();

        Assert.Equal("0:05:00", before);
        Assert.Equal("0:06:00", widget.TimerText);
        Assert.Equal(persistedStart, session.State.ActiveEntry!.StartTime);
        Assert.Equal(persistedNow, session.State.ActiveEntry!.StartTime);
        Assert.Single(session.State.Entries);
    }

    [Fact]
    public void Idle_shows_no_timer_at_all()
    {
        WidgetViewModel widget = IdleWidget(out _);

        Assert.Equal(string.Empty, widget.TimerText);
        Assert.Equal(string.Empty, widget.TimerTooltip);
    }

    // ------------------------------------------------------------------ size and topmost (Task 4.3)

    [Theory]
    [InlineData(WidgetSizePreset.Compact, 380)]
    [InlineData(WidgetSizePreset.Comfortable, 420)]
    [InlineData(WidgetSizePreset.Expanded, 520)]
    public void Each_size_preset_has_the_agreed_width(WidgetSizePreset preset, int width)
    {
        WidgetViewModel widget = WidgetWith(UserPreferences.Default with { WidgetSize = preset }, out _);

        Assert.Equal(width, widget.WidgetWidth);
    }

    [Theory]
    [InlineData(WidgetSizePreset.Compact, 300)]
    [InlineData(WidgetSizePreset.Comfortable, 430)]
    [InlineData(WidgetSizePreset.Expanded, 560)]
    public void Each_size_preset_has_the_agreed_height_at_100_percent(WidgetSizePreset preset, int height)
    {
        WidgetViewModel widget = WidgetWith(UserPreferences.Default with { WidgetSize = preset }, out _);

        Assert.Equal(height, widget.WidgetHeight);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.25)]
    public void No_preset_can_be_shorter_than_the_content_needs_at_any_text_scaling(double textScale)
    {
        // The plan's explicit minimum. It cannot be a fixed number: measured on Windows 11, Compact at 380x300
        // rendered fully at 100% and clipped its button row clean off the card at 150%, while the controls still
        // measured 44x44 and still reported Visible.
        foreach(WidgetSizePreset preset in Enum.GetValues<WidgetSizePreset>())
        {
            WidgetViewModel widget = WidgetWith(UserPreferences.Default with { WidgetSize = preset }, out _);
            widget.TextScale = textScale;

            Assert.True(
                widget.WidgetHeight >= WidgetViewModel.MinimumContentHeightFor(textScale),
                $"{preset} at {textScale:P0} is {widget.WidgetHeight}px, below the "
                + $"{WidgetViewModel.MinimumContentHeightFor(textScale)}px content minimum");
        }
    }

    [Fact]
    public void Text_scaling_raises_compact_above_its_nominal_preset()
    {
        // the preset states an intent; the content decides. At 150% the height has to grow past 300.
        WidgetViewModel widget = WidgetWith(
            UserPreferences.Default with { WidgetSize = WidgetSizePreset.Compact }, out _);

        Assert.Equal(300, widget.WidgetHeight);

        widget.TextScale = 1.5;

        Assert.Equal(450, widget.WidgetHeight);
        Assert.Equal(380, widget.WidgetWidth);
    }

    [Fact]
    public void The_content_minimum_grows_with_text_scaling_and_never_shrinks_below_100_percent()
    {
        Assert.Equal(300, WidgetViewModel.MinimumContentHeightFor(1.0));
        Assert.True(WidgetViewModel.MinimumContentHeightFor(1.5) > 300);
        Assert.True(
            WidgetViewModel.MinimumContentHeightFor(2.25) > WidgetViewModel.MinimumContentHeightFor(1.5));

        // text smaller than 100% does not make the card's controls any shorter
        Assert.Equal(300, WidgetViewModel.MinimumContentHeightFor(0.5));
        Assert.Equal(300, WidgetViewModel.MinimumContentHeightFor(0.0));
    }

    [Fact]
    public void Always_on_top_follows_the_preference()
    {
        Assert.True(WidgetWith(UserPreferences.Default with { AlwaysOnTop = true }, out _).AlwaysOnTop);
        Assert.False(WidgetWith(UserPreferences.Default with { AlwaysOnTop = false }, out _).AlwaysOnTop);
    }

    /// <summary>A clock that does not move, so assertions are about behaviour rather than the wall.</summary>
    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset Now { get; } = now;
    }

    /// <summary>A clock whose reading the test moves by hand, for the tick rules.</summary>
    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset Now { get; set; } = now;
    }
}
