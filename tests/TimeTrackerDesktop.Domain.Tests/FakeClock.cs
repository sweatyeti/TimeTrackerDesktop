namespace TimeTrackerDesktop.Domain.Tests;

/// <summary>
/// Deterministic clock for domain tests: time moves only when a test moves it, so every transition
/// can be asserted exactly. The domain takes <see cref="IClock"/>, so nothing else is needed to make
/// a session reproducible.
/// </summary>
internal sealed class FakeClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset Now { get; set; } = now;

    /// <summary>A fixed instant in a non-UTC offset, so a test that assumes UTC fails loudly.</summary>
    public static FakeClock AtCentral(int year, int month, int day, int hour, int minute, int second) =>
        new(new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.FromHours(-5)));

    public FakeClock Advance(TimeSpan by)
    {
        Now += by;
        return this;
    }

    public FakeClock SetTo(DateTimeOffset at)
    {
        Now = at;
        return this;
    }
}