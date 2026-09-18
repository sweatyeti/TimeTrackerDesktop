namespace TimeTrackerDesktop.Domain;

/// <summary>
/// Wall-clock implementation of <see cref="IClock"/>. Returns a local offset
/// (<see cref="DateTimeOffset.Now"/>), matching TTC, which stamps <c>DateTime.Now</c> and persists the
/// machine's offset in every timestamp it writes.
/// </summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
}