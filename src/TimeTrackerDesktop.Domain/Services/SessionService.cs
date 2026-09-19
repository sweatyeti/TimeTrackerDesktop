namespace TimeTrackerDesktop.Domain;

/// <summary>
/// Every session mutation. Invariant 9 of the plan: all mutations flow through here, and view models
/// only invoke these commands and render the immutable results — they never edit state themselves.
///
/// Nothing here reads the wall clock or touches a disk: time arrives through <see cref="IClock"/> and
/// persistence is the caller's job, so every transition is testable without a UI host.
/// </summary>
public sealed class SessionService
{
    private readonly IClock _clock;

    public SessionState State { get; private set; }

    public SessionService(SessionState state, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(clock);

        _clock = clock;
        State = state;

        // invariant 1: a session has zero or one non-deleted incomplete entry. TTC refuses to open a
        // session that breaks this rather than guessing which entry is running, and so do we.
        State.Validate();
    }

    /// <summary>
    /// Starts a brand new session: fresh identity, name generated from the clock when blank, and an
    /// entry already tracking.
    ///
    /// <b>Tracking starts immediately, matching TTC's CLI <c>new</c>.</b> The entry is stamped at the
    /// current time and opens with the "none" task; the caller then prompts for a task and applies it
    /// with <see cref="UpdateActiveEntry"/>. That ordering is deliberate and is what TTC does — it stamps the
    /// start time <i>before</i> showing its task prompt, so time spent answering the prompt is tracked
    /// rather than lost. A caller that wants an idle session calls <see cref="StopCurrentEntry"/>
    /// straight after.
    /// </summary>
    public static SessionService StartNewSession(IClock clock, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(clock);

        SessionService session = new(SessionState.New(name, clock.Now), clock);
        session.StartEntry();

        return session;
    }

    /// <summary>
    /// <b>Start</b> — the operation for when time is <b>not</b> being tracked: opens an entry with the
    /// supplied task (blank becomes "none") and a blank description, stamped at the current time.
    ///
    /// <b>Already tracking is a no-op</b>, reported as <see cref="SessionChange.None"/>. Start is not
    /// the operation for switching tasks — that is <see cref="RestartEntry"/> ("stop and start"). A
    /// no-op is deliberate: if a stale command arrives while an entry is running, silently splitting
    /// that entry would rewrite the user's data, whereas doing nothing is recoverable.
    /// </summary>
    public SessionResult StartEntry(string? task = null) =>
        State.IsActive ? new SessionResult(State, SessionChange.None) : OpenEntry(task);

    /// <summary>
    /// <b>Stop and start</b> — the operation for when a task <b>is</b> active: completes the running
    /// entry and immediately opens a new one with the supplied task (blank becomes "none") and a blank
    /// description.
    ///
    /// Also starts an entry when nothing is running, because the command surfaces that always mean
    /// "switch to a new task" (the widget's start-new-task control, the tray's Start new task) must
    /// work from the idle state too. The reported change distinguishes the two cases:
    /// <see cref="SessionChange.EntryRestarted"/> when something was running,
    /// <see cref="SessionChange.EntryStarted"/> when nothing was.
    /// </summary>
    public SessionResult RestartEntry(string? task = null) => OpenEntry(task);

    /// <summary>
    /// Stop. Completes only the active entry and leaves the session open and idle (invariant 4).
    /// A no-op when nothing is running, so a double Stop cannot move the recorded end time.
    /// </summary>
    public SessionResult StopCurrentEntry()
    {
        if(State.ActiveEntry is not { } active)
        {
            return new SessionResult(State, SessionChange.None);
        }

        TimeEntry stopped = active.Complete(_clock.Now);
        Replace(stopped);

        return new SessionResult(State, SessionChange.EntryStopped, stopped);
    }

    /// <summary>
    /// The command surface's name for Stop ("Stop tracking"), kept as a call into
    /// <see cref="StopCurrentEntry"/> rather than a parallel implementation for the same reason:
    /// two copies of one transition eventually disagree.
    /// </summary>
    public SessionResult StopTracking() => StopCurrentEntry();

    /// <summary>
    /// Ends the session: stops any active entry, then stamps <c>EndedAt</c> (invariant 5).
    ///
    /// <b>Not idempotent — this matches TTC.</b> Every call restamps <c>EndedAt</c> with the current
    /// time, so calling it twice rewrites when the session ended. TTC behaves the same way, and TTC
    /// parity was chosen deliberately over protecting the original end time; a caller that must not
    /// move it should check <see cref="SessionResult.IsEnded"/> first.
    /// </summary>
    public SessionResult EndSession()
    {
        // capture the stopped entry before stamping, so the caller can still show what just stopped
        SessionResult stopped = StopCurrentEntry();
        State = State with { EndedAt = _clock.Now };

        return new SessionResult(State, SessionChange.SessionEnded, stopped.AffectedEntry);
    }

    /// <summary>
    /// <b>Edit</b> — the one place entry mutation rules live (plan Task 1.3):
    /// <list type="bullet">
    /// <item>a <c>null</c> field is left alone, so a caller states only what it wants changed;</item>
    /// <item>the task is trimmed and blank maps to <c>none</c>;</item>
    /// <item>the description is trimmed and blank stays an empty string;</item>
    /// <item>logged state is writable only for completed, named, non-deleted entries.</item>
    /// </list>
    ///
    /// Every refusal is named in the returned <see cref="EntryEditResult"/> rather than silently
    /// ignored, and nothing is written when a request is refused. Start and end times are not editable:
    /// there is no such operation in the v1 public domain API, and a name canary test enforces that.
    /// </summary>
    public EntryEditResult UpdateEntry(int id, EntryEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);

        TimeEntry? entry = State.Entries.SingleOrDefault(existing => existing.Id == id);

        if(entry is null)
        {
            return new EntryEditResult(State, EntryEditOutcome.EntryNotFound);
        }

        if(edit.IsEmpty)
        {
            return new EntryEditResult(State, EntryEditOutcome.NothingToDo, entry);
        }

        // deleted entries are kept for restore, not for editing
        if(!entry.IsEditable)
        {
            return new EntryEditResult(State, EntryEditOutcome.EntryDeleted, entry);
        }

        // TTC checks this before offering the logged prompt at all (HasLoggedState); doing it in the
        // service as well means a caller that forgets cannot write a logged state that cannot exist
        if(edit.Logged.HasValue && !entry.CanHoldLoggedState)
        {
            return new EntryEditResult(State, EntryEditOutcome.LoggedNotApplicable, entry);
        }

        TimeEntry edited = entry;

        if(edit.Task is not null) edited = edited.WithTask(edit.Task);
        if(edit.Description is not null) edited = edited.WithDescription(edit.Description);
        if(edit.Logged.HasValue) edited = edited.WithLogged(edit.Logged.Value);

        // normalization can make a request a no-op ("  weeding  " when the task is already "weeding")
        if(edited == entry)
        {
            return new EntryEditResult(State, EntryEditOutcome.Unchanged, entry);
        }

        Replace(edited);

        return new EntryEditResult(State, EntryEditOutcome.Applied, edited);
    }

    /// <summary>
    /// The live-edit path: applies an edit to whatever entry is currently running.
    ///
    /// View models call this instead of re-deriving "which entry is active" from a snapshot (invariant
    /// 9), and it reports <see cref="EntryEditOutcome.NoActiveEntry"/> rather than quietly doing
    /// nothing when the session is idle.
    /// </summary>
    public EntryEditResult UpdateActiveEntry(EntryEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);

        return State.ActiveEntry is { } active
            ? UpdateEntry(active.Id, edit)
            : new EntryEditResult(State, EntryEditOutcome.NoActiveEntry);
    }

    /// <summary>Soft-deletes a completed entry. Open entries are not deletable.</summary>
    public void Delete(int id)
    {
        TimeEntry entry = Find(id);

        if(entry.IsComplete && !entry.IsDeleted) Replace(entry with { IsDeleted = true });
    }

    public void Restore(int id)
    {
        TimeEntry entry = Find(id);

        if(entry.IsDeleted) Replace(entry with { IsDeleted = false });
    }

    /// <summary>
    /// Completed, undeleted, non-"none" time grouped by task, case-insensitively. Phase 1 Task 1.4
    /// owns the real projections; this stays as the bootstrap left it.
    /// </summary>
    public IReadOnlyDictionary<string, TimeSpan> Summary() =>
        State.Entries
            .Where(entry => entry.IsComplete && !entry.IsDeleted && !entry.HasNoTask && entry.Duration is not null)
            .GroupBy(entry => entry.Task, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.First().Task,
                group => TimeSpan.FromTicks(group.Sum(entry => entry.Duration!.Value.Ticks)),
                StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The one place an entry is opened: completes whatever is running, then adds a new entry at the
    /// current time and reports whether that was a start or a restart.
    /// </summary>
    private SessionResult OpenEntry(string? task)
    {
        DateTimeOffset now = _clock.Now;
        TimeEntry? active = State.ActiveEntry;
        List<TimeEntry> entries = [.. State.Entries];
        SessionChange change = SessionChange.EntryStarted;

        if(active is not null)
        {
            entries[entries.FindIndex(entry => entry.Id == active.Id)] = active.Complete(now);
            change = SessionChange.EntryRestarted;
        }

        TimeEntry opened = TimeEntry.Start(State.NextEntryId, now, task);
        entries.Add(opened);

        // starting work makes an ended session live again. TTC does the same when resuming:
        // "resuming means the session is active again" (Resume sets EndedAt back to null).
        State = State with { Entries = entries, EndedAt = null };

        return new SessionResult(State, change, opened);
    }

    private TimeEntry Find(int id) => State.Entries.Single(entry => entry.Id == id);

    private void Replace(TimeEntry entry)
    {
        List<TimeEntry> entries = [.. State.Entries];
        entries[entries.FindIndex(existing => existing.Id == entry.Id)] = entry;
        State = State with { Entries = entries };
    }
}