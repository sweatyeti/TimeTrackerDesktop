namespace TimeTrackerDesktop.Domain;

public sealed record TimeEntry(int Id, DateTimeOffset StartTime, DateTimeOffset? EndTime, string Task, string Description, bool Logged, bool IsComplete, bool IsDeleted)
{
    public TimeEntry Start(DateTimeOffset at, string task) => this with { StartTime = at, EndTime = null, Task = NormalizeTask(task), Description = string.Empty, IsComplete = false, Logged = false, IsDeleted = false };
    public TimeEntry Complete(DateTimeOffset at) => this with { EndTime = at, IsComplete = true };
    public static string NormalizeTask(string? task) => string.IsNullOrWhiteSpace(task) ? "none" : task.Trim();
}

public sealed record SessionState(Guid SessionId, string Name, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, IReadOnlyList<TimeEntry> Entries)
{
    public static SessionState New(string name, DateTimeOffset startedAt) => new(Guid.NewGuid(), name, startedAt, null, []);
    public TimeEntry? ActiveEntry => Entries.SingleOrDefault(e => !e.IsDeleted && !e.IsComplete);
    public int NextEntryId => Entries.Count == 0 ? 1 : Entries.Max(e => e.Id) + 1;
}

public interface IClock { DateTimeOffset Now { get; } }
public sealed class SystemClock : IClock { public DateTimeOffset Now => DateTimeOffset.Now; }

public sealed class SessionService
{
    private readonly IClock _clock;
    public SessionState State { get; private set; }
    public SessionService(SessionState state, IClock clock) { State = state; _clock = clock; Validate(); }
    public void StartEntry(string? task = null)
    {
        var now = _clock.Now; var active = State.ActiveEntry;
        var entries = State.Entries.ToList();
        if (active is not null) entries[entries.IndexOf(active)] = active.Complete(now);
        entries.Add(new TimeEntry(State.NextEntryId, now, null, TimeEntry.NormalizeTask(task), string.Empty, false, false, false));
        State = State with { Entries = entries, EndedAt = null };
    }
    public void StopCurrentEntry() { var active = State.ActiveEntry; if (active is null) return; Replace(active.Complete(_clock.Now)); }
    public void EndSession() { StopCurrentEntry(); State = State with { EndedAt = _clock.Now }; }
    public void UpdateTask(string? task) { var a = State.ActiveEntry; if (a is not null) Replace(a with { Task = TimeEntry.NormalizeTask(task) }); }
    public void UpdateDescription(string? description) { var a = State.ActiveEntry; if (a is not null) Replace(a with { Description = description ?? string.Empty }); }
    public void Delete(int id) { var e = Find(id); if (e.IsComplete && !e.IsDeleted) Replace(e with { IsDeleted = true }); }
    public void Restore(int id) { var e = Find(id); if (e.IsDeleted) Replace(e with { IsDeleted = false }); }
    public IReadOnlyDictionary<string, TimeSpan> Summary() => State.Entries.Where(e => e.IsComplete && !e.IsDeleted && !e.Task.Equals("none", StringComparison.OrdinalIgnoreCase)).GroupBy(e => e.Task, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.First().Task, g => TimeSpan.FromTicks(g.Sum(e => (e.EndTime!.Value - e.StartTime).Ticks)), StringComparer.OrdinalIgnoreCase);
    private TimeEntry Find(int id) => State.Entries.Single(e => e.Id == id);
    private void Replace(TimeEntry entry) { var entries = State.Entries.ToList(); entries[entries.FindIndex(e => e.Id == entry.Id)] = entry; State = State with { Entries = entries }; }
    private void Validate() { if (State.Entries.Count(e => !e.IsDeleted && !e.IsComplete) > 1) throw new InvalidDataException("Session contains more than one unfinished entry."); }
}
