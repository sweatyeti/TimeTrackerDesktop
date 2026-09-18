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
    /// with <see cref="UpdateTask"/>. That ordering is deliberate and is what TTC does — it stamps the
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
    /// Play. Idle opens an entry with the supplied task (blank becomes "none") and a blank
    /// description; already running completes the current entry and opens a new one. Invariants 2 and 3.
    /// </summary>
    public SessionResult StartEntry(string? task = null) => OpenEntry(task);

    /// <summary>
    /// The restart transition on its own, for command surfaces that always mean "switch to a new
    /// task" — the tray's Start new task, and the widget's secondary "start new task" control.
    ///
    /// Deliberately the same transition as <see cref="StartEntry"/>: the plan states that Play "uses
    /// the exact restart semantics", so one implementation serves both and the two command surfaces
    /// cannot drift apart.
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

    public void UpdateTask(string? task)
    {
        if(State.ActiveEntry is not { } active) return;

        Replace(active with { Task = TimeEntry.NormalizeTask(task) });
    }

    public void UpdateDescription(string? description)
    {
        if(State.ActiveEntry is not { } active) return;

        Replace(active with { Description = description ?? string.Empty });
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