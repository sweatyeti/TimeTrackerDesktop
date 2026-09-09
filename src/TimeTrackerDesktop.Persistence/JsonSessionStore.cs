using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TimeTrackerDesktop.Domain;

namespace TimeTrackerDesktop.Persistence;

public sealed class JsonSessionStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static SessionState Read(string json)
    {
        var node = JsonNode.Parse(json) ?? throw new JsonException("Session JSON is empty.");
        var state = node.Deserialize<SessionDocument>(Options) ?? throw new JsonException("Session JSON is invalid.");
        var entries = state.Entries.Select(e => new TimeEntry(e.Id, e.StartTime, e.EndTime, e.Task ?? "none", e.Description ?? string.Empty, e.Logged, e.IsComplete, e.IsDeleted)).ToArray();
        return new SessionState(state.SessionId, state.Name ?? "Unnamed session", state.StartedAt, state.EndedAt, entries);
    }
    public static string Write(SessionState state) => JsonSerializer.Serialize(new SessionDocument(2, state.SessionId, state.Name, state.StartedAt, state.EndedAt, state.Entries.Select(e => new EntryDocument(e.Id, e.StartTime, e.EndTime, e.Task, e.Description, e.Logged, e.IsComplete, e.IsDeleted)).ToArray()), Options);
    private sealed record SessionDocument(int SchemaVersion, Guid SessionId, string? Name, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, EntryDocument[] Entries);
    private sealed record EntryDocument(int Id, DateTimeOffset StartTime, DateTimeOffset? EndTime, string? Task, string? Description, bool Logged, bool IsComplete, bool IsDeleted = false);
}
