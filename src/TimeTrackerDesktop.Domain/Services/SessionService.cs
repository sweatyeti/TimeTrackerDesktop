namespace TimeTrackerDesktop.Domain;

/// <summary>
/// The session's operations, driven entirely by the injected <see cref="IClock"/> — no wall-clock
/// reads and no persistence, so every transition is testable without a UI host or a disk.
///
/// Scope note: Task 1.1 relocates this out of the Phase 0 bootstrap file and onto the immutable model
/// API. The transition rules are the bootstrap's, kept deliberately unchanged so the move is
/// verifiable; Task 1.2 implements them properly (explicit operations and immutable results for UI
/// status cues).
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

        // a session with two open entries cannot be represented: TTC refuses to open one rather than
        // guessing which entry is running, and so do we
        State.Validate();
    }

    /// <summary>
    /// Starts tracking: completes any open entry at the current time, then opens a new one with the
    /// supplied task (blank becomes "none") and a blank description.
    /// </summary>
    public void StartEntry(string? task = null)
    {
        DateTimeOffset now = _clock.Now;
        TimeEntry? active = State.ActiveEntry;

        List<TimeEntry> entries = [.. State.Entries];

        if(active is not null)
        {
            entries[entries.FindIndex(entry => entry.Id == active.Id)] = active.Complete(now);
        }

        entries.Add(TimeEntry.Start(State.NextEntryId, now, task));

        // starting work makes the session live again
        State = State with { Entries = entries, EndedAt = null };
    }

    /// <summary>Completes the open entry at the current time, leaving the session unended and idle.</summary>
    public void StopCurrentEntry()
    {
        if(State.ActiveEntry is not { } active) return;

        Replace(active.Complete(_clock.Now));
    }

    /// <summary>Stops any open work, then stamps the session as ended.</summary>
    public void EndSession()
    {
        StopCurrentEntry();
        State = State with { EndedAt = _clock.Now };
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

    private TimeEntry Find(int id) => State.Entries.Single(entry => entry.Id == id);

    private void Replace(TimeEntry entry)
    {
        List<TimeEntry> entries = [.. State.Entries];
        entries[entries.FindIndex(existing => existing.Id == entry.Id)] = entry;
        State = State with { Entries = entries };
    }
}