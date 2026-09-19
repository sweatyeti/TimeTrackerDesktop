using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using TimeTrackerDesktop.Domain;

namespace TimeTrackerDesktop.ViewModels;

/// <summary>
/// The compact widget (plan Tasks 4.2 and 4.3): an active state with the task, description and primary
/// commands, an idle state that says so, and a timer whose presentation follows the user's preferences.
///
/// The rules with teeth:
///
/// - <b>Commands bind to the <see cref="SessionService"/> and nothing else.</b> The widget never edits session
///   state directly, so the domain's invariants hold no matter which surface is driving.
/// - <b>Colour is never the only signal.</b> Active and idle differ by wording (<see cref="StateLabel"/>),
///   by which controls exist at all, and by accessible names and tooltips — not by a green dot.
/// - <b>Play uses the exact restart semantics</b> (decision 4): it completes the running entry and opens a new
///   one, and from idle it simply starts. Stop only stops. Neither is an alias for the other.
/// - <b>The clock drives the display, and only the display.</b> <see cref="Tick"/> re-reads the clock; it never
///   restamps the entry, because a tick that moved the start time would rewrite the user's recorded work every
///   second. See <see cref="TimerText"/>.
/// </summary>
public sealed class WidgetViewModel : INotifyPropertyChanged
{
    /// <summary>The plan's literal idle wording.</summary>
    public const string IdleText = "No task running";

    /// <summary>Shown in the task position while active but with no task set.</summary>
    public const string NoTaskText = "No task";

    /// <summary>
    /// The shortest the card may be before its own controls start falling off it, <b>at 100% text scaling</b>.
    ///
    /// This is the plan's "explicit minimum" (Task 4.3), and it is a CONTENT requirement: a preset states a
    /// height, but the content has the last word. At 260px the widget's button row was pushed clean past the
    /// bottom edge and rendered nothing while still measuring 44x44 and reporting <c>Visible</c>, and at 150%
    /// text scaling the same thing happened again at 300px. Use <see cref="MinimumContentHeightFor"/> rather
    /// than this constant directly — the minimum grows with the user's text size.
    /// </summary>
    public const int MinimumContentHeight = 300;

    private static readonly UserPreferences FallbackPreferences = UserPreferences.Default;

    private readonly SessionService _session;
    private readonly IClock _clock;

    private UserPreferences _preferences;
    private double _textScale = 1.0;

    private string _taskText = string.Empty;
    private string _descriptionText = string.Empty;
    private string _message = string.Empty;
    private bool _isEditingTask;

    public WidgetViewModel(SessionService session, IClock clock, UserPreferences? preferences = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(clock);

        _session = session;
        _clock = clock;
        _preferences = preferences ?? FallbackPreferences;

        PlayCommand = new RelayCommand(Play);
        StopCommand = new RelayCommand(Stop, () => CanStop);
        EditTaskCommand = new RelayCommand(() => IsEditingTask = true, () => IsActive);

        SyncFromSession();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Stop-and-start. From idle this starts; while running it restarts.</summary>
    public ICommand PlayCommand { get; }

    /// <summary>Stops the running entry and does not open another.</summary>
    public ICommand StopCommand { get; }

    /// <summary>Reveals the task field for editing (the plan's secondary edit control).</summary>
    public ICommand EditTaskCommand { get; }

    public SessionService Session => _session;

    /// <summary>
    /// The preferences in force. Settable because a change is applied to the running widget rather than only
    /// taking effect on the next launch — persistence and application are Task 4.3; the editing surface is 4.4.
    /// </summary>
    public UserPreferences Preferences
    {
        get => _preferences;
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            _preferences = value;
            RaisePresentation();
        }
    }

    /// <summary>True while an entry is open.</summary>
    public bool IsActive => _session.State.IsActive;

    /// <summary>True when nothing is running.</summary>
    public bool IsIdle => !IsActive;

    /// <summary>
    /// The line the widget always shows: the running task while active, and the plan's literal
    /// <c>No task running</c> while idle.
    /// </summary>
    public string StateLabel => IsActive
        ? TaskDisplay.Visible(_session.State.ActiveEntry?.Task) is { Length: > 0 } task ? task : NoTaskText
        : IdleText;

    /// <summary>
    /// The task field. Visible only while active, and blank when the task is unset — TTC stores <c>none</c>,
    /// which is a storage word rather than something to put in front of a user.
    /// </summary>
    public string TaskText
    {
        get => _taskText;
        set => Set(ref _taskText, value);
    }

    /// <summary>The description control. Visible only while active.</summary>
    public string DescriptionText
    {
        get => _descriptionText;
        set => Set(ref _descriptionText, value);
    }

    /// <summary>Whether the task field is currently open for editing.</summary>
    public bool IsEditingTask
    {
        get => _isEditingTask;
        set => Set(ref _isEditingTask, value);
    }

    /// <summary>Stop is available only while something is running, so it can be disabled rather than inert.</summary>
    public bool CanStop => IsActive;

    /// <summary>The plan hides the task field while idle; exposed so that rule is testable, not just styled.</summary>
    public bool IsTaskFieldVisible => IsActive;

    /// <summary>The plan hides the description control while idle.</summary>
    public bool IsDescriptionVisible => IsActive;

    /// <summary>When the running entry began, spelled out.</summary>
    public string StartedDisplay =>
        _session.State.ActiveEntry is { } entry ? $"Started {TimeDisplay.Clock(entry.StartTime)}" : string.Empty;

    /// <summary>The ticking duration, recomputed from the persisted start time rather than accumulated.</summary>
    public string ElapsedDisplay =>
        _session.State.ActiveEntry is { } entry ? TimeDisplay.FormatTicking(_clock.Now - entry.StartTime) : string.Empty;

    /// <summary>The duration rounded to the minute, for places that want a stable number.</summary>
    public string ElapsedRoundedDisplay =>
        _session.State.ActiveEntry is { } entry ? TimeDisplay.Format(_clock.Now - entry.StartTime) : string.Empty;

    /// <summary>
    /// The timer line (plan Task 4.3). The preference chooses which half is shown — either the ticking
    /// duration or the start time — and the other half moves to <see cref="TimerTooltip"/>, so the choice is
    /// one of emphasis rather than of losing the information.
    /// </summary>
    public string TimerText => IsActive
        ? _preferences.TimerDisplay is TimerDisplayMode.StartedTime ? StartedDisplay : ElapsedDisplay
        : string.Empty;

    /// <summary>The half of the timer that <see cref="TimerText"/> is not showing, worded so it reads alone.</summary>
    public string TimerTooltip => IsActive
        ? _preferences.TimerDisplay is TimerDisplayMode.StartedTime
            ? $"Running for {ElapsedDisplay}"
            : StartedDisplay
        : string.Empty;

    /// <summary>The card's width for the chosen preset.</summary>
    public int WidgetWidth => _preferences.WidgetSize switch
    {
        WidgetSizePreset.Compact => 380,
        WidgetSizePreset.Expanded => 520,
        _ => 420,
    };

    /// <summary>
    /// The card's height for the chosen preset, never below the content minimum for the current text scaling.
    /// A preset is a preference; fitting its own controls is not optional.
    /// </summary>
    public int WidgetHeight => Math.Max(PresetHeight, MinimumContentHeightFor(_textScale));

    /// <summary>
    /// Text scaling in force, where 1.0 means 100%.
    ///
    /// Read from the platform by the window and set here as a plain number, deliberately: a view model that
    /// referenced a WinUI type would no longer be testable without a UI thread, which is where this project
    /// catches its real bugs.
    /// </summary>
    public double TextScale
    {
        get => _textScale;
        set
        {
            _textScale = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(WidgetHeight));
        }
    }

    /// <summary>
    /// The content minimum for a given text scaling.
    ///
    /// Measured, not assumed: Compact at 380x300 rendered its full control set at 100% and clipped the button
    /// row clean off the card at 150%, with the controls still measuring and still reporting <c>Visible</c>. A
    /// fixed 300 therefore cannot be right for every scaling.
    ///
    /// Scaling is linear and deliberately conservative, because part of the height is chrome that does not grow:
    /// this asks for slightly more room than strictly needed. Too tall is a cosmetic complaint; too short hides
    /// the controls.
    /// </summary>
    public static int MinimumContentHeightFor(double textScale) =>
        textScale <= 1.0 ? MinimumContentHeight : (int)Math.Ceiling(MinimumContentHeight * textScale);

    /// <summary>The preset's own height, before the content minimum has its say.</summary>
    private int PresetHeight => _preferences.WidgetSize switch
    {
        WidgetSizePreset.Compact => 300,
        WidgetSizePreset.Expanded => 560,
        _ => 430,
    };

    /// <summary>Whether the widget asks to stay above other windows (default true in the first-run preferences).</summary>
    public bool AlwaysOnTop => _preferences.AlwaysOnTop;

    /// <summary>What just happened, for a status line or a screen reader.</summary>
    public string Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    public string PlayAccessibleName => IsActive ? "Stop and start a new task" : "Start tracking";

    public string StopAccessibleName => "Stop tracking";

    public string PlayTooltip => IsActive
        ? "Stop the current task and start a new one"
        : "Start tracking a new task";

    public string StopTooltip => "Stop the current task";

    /// <summary>
    /// Stop-and-start, exactly as the plan requires. From idle this starts a task, which is why Play stays
    /// available when nothing is running.
    /// </summary>
    public void Play()
    {
        SessionResult result = _session.RestartEntry();

        Message = result.Change switch
        {
            SessionChange.EntryStarted => "Started tracking.",
            SessionChange.EntryRestarted => "Stopped the previous task and started a new one.",
            _ => "Nothing changed.",
        };

        SyncFromSession();
    }

    /// <summary>Stops only. It never opens a new entry, and it is a no-op when nothing is running.</summary>
    public void Stop()
    {
        SessionResult result = _session.StopCurrentEntry();

        Message = result.Change is SessionChange.EntryStopped ? "Stopped tracking." : "Nothing was running.";
        SyncFromSession();
    }

    /// <summary>Applies the task field to the running entry. A blank task is written as "no task".</summary>
    public void CommitTask()
    {
        if(!IsActive)
        {
            Message = "No entry is running to edit.";
            return;
        }

        EntryEditResult result = _session.UpdateActiveEntry(new EntryEdit(Task: _taskText));

        Message = result.Reason;
        IsEditingTask = false;
        SyncFromSession();
    }

    /// <summary>Applies the description control to the running entry.</summary>
    public void CommitDescription()
    {
        if(!IsActive)
        {
            Message = "No entry is running to edit.";
            return;
        }

        EntryEditResult result = _session.UpdateActiveEntry(new EntryEdit(Description: _descriptionText));

        Message = result.Reason;
        SyncFromSession();
    }

    /// <summary>
    /// Re-reads the clock for the duration display. The UI calls this on a timer; the elapsed time is always
    /// derived from the entry's persisted start, so a missed tick or a suspended process cannot drift it — and
    /// nothing here writes to the session, so a tick can never move the recorded start time.
    /// </summary>
    public void Tick()
    {
        OnPropertyChanged(nameof(ElapsedDisplay));
        OnPropertyChanged(nameof(ElapsedRoundedDisplay));
        OnPropertyChanged(nameof(TimerText));
        OnPropertyChanged(nameof(TimerTooltip));
    }

    /// <summary>Pulls every display value back into line with the session.</summary>
    public void SyncFromSession()
    {
        _taskText = TaskDisplay.Visible(_session.State.ActiveEntry?.Task);
        _descriptionText = _session.State.ActiveEntry?.Description ?? string.Empty;

        if(!IsActive)
        {
            // the idle state hides both controls, so an editing session must not survive the entry stopping
            _isEditingTask = false;
        }

        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(TaskText));
        OnPropertyChanged(nameof(DescriptionText));
        OnPropertyChanged(nameof(IsEditingTask));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(IsTaskFieldVisible));
        OnPropertyChanged(nameof(IsDescriptionVisible));
        OnPropertyChanged(nameof(StartedDisplay));
        OnPropertyChanged(nameof(ElapsedDisplay));
        OnPropertyChanged(nameof(ElapsedRoundedDisplay));
        OnPropertyChanged(nameof(PlayAccessibleName));
        OnPropertyChanged(nameof(PlayTooltip));

        RaisePresentation();
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if(EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    /// <summary>Announces everything that follows from the preferences or the clock.</summary>
    private void RaisePresentation()
    {
        OnPropertyChanged(nameof(Preferences));
        OnPropertyChanged(nameof(TimerText));
        OnPropertyChanged(nameof(TimerTooltip));
        OnPropertyChanged(nameof(WidgetWidth));
        OnPropertyChanged(nameof(WidgetHeight));
        OnPropertyChanged(nameof(TextScale));
        OnPropertyChanged(nameof(AlwaysOnTop));

        (StopCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (EditTaskCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}