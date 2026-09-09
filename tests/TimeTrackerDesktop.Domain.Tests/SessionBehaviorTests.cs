using TimeTrackerDesktop.Domain;

namespace TimeTrackerDesktop.Domain.Tests;

public sealed class SessionBehaviorTests
{
    private sealed class FakeClock(DateTimeOffset now) : IClock { public DateTimeOffset Now { get; set; } = now; }

    [Fact]
    public void PlayStopUsesInjectedTimeAndStartsAtOne()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero));
        var service = new SessionService(SessionState.New("demo", clock.Now), clock);
        service.StartEntry();
        Assert.Equal("none", service.State.ActiveEntry!.Task);
        Assert.Equal(1, service.State.ActiveEntry.Id);
        clock.Now = clock.Now.AddMinutes(3);
        service.StopCurrentEntry();
        Assert.True(service.State.Entries.Single().IsComplete);
        Assert.Equal(TimeSpan.FromMinutes(3), service.State.Entries.Single().EndTime - service.State.Entries.Single().StartTime);
    }

    [Fact]
    public void ResumeRejectsMultipleActiveEntries()
    {
        var t = DateTimeOffset.UtcNow;
        var state = SessionState.New("bad", t) with { Entries = [new(1,t,null,"a","",false,false,false), new(2,t,null,"b","",false,false,false)] };
        Assert.Throws<InvalidDataException>(() => new SessionService(state, new FakeClock(t)));
    }
}
