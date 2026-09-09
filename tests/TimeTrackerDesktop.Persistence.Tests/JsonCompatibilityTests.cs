using TimeTrackerDesktop.Domain;
using TimeTrackerDesktop.Persistence;

namespace TimeTrackerDesktop.Persistence.Tests;

public sealed class JsonCompatibilityTests
{
    [Fact]
    public void V1MissingIsDeletedDefaultsFalseAndWritesV2()
    {
        var json = """{"schemaVersion":1,"sessionId":"00000000-0000-0000-0000-000000000001","name":"demo","startedAt":"2026-09-09T12:00:00+00:00","endedAt":null,"entries":[{"id":1,"startTime":"2026-09-09T12:00:00+00:00","endTime":null,"task":"none","description":"[safe]","logged":false,"isComplete":false}]}""";
        var state = JsonSessionStore.Read(json);
        Assert.False(state.Entries.Single().IsDeleted);
        Assert.Contains("\"schemaVersion\": 2", JsonSessionStore.Write(state));
        Assert.Equal("[safe]", state.Entries.Single().Description);
    }
}
