using System.Text.Json;
using TimeTrackerDesktop.Domain;

namespace TimeTrackerDesktop.Persistence;

/// <summary>
/// One entry from a session file: the domain value plus every field this build did not recognise.
/// </summary>
public sealed record EntryDocument(TimeEntry Entry, IReadOnlyDictionary<string, JsonElement> UnknownFields);

/// <summary>
/// A session file, parsed but not yet interpreted: the envelope, the entries, and any field this build
/// does not understand.
///
/// Wire-faithful on purpose. <see cref="Name"/> and the rest hold what the FILE said, so a document that
/// is read and written again round-trips byte for byte. The repairs TTC applies on load — a blank name
/// becomes "Unnamed session", a null task becomes "none" — happen in <see cref="ToSessionState"/>, where
/// they cannot rewrite the file behind the user's back.
/// </summary>
public sealed record SessionDocument(
    int SchemaVersion,
    Guid SessionId,
    string? Name,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    IReadOnlyList<EntryDocument> Entries,
    IReadOnlyDictionary<string, JsonElement> UnknownFields)
{
    /// <summary>The version every write produces.</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>
    /// The oldest version that can be read. A file whose <c>schemaVersion</c> key is missing reads as
    /// <c>0</c>, which is "absent or unsupported-old" rather than "version 0 is fine".
    /// </summary>
    public const int OldestReadableSchemaVersion = 1;

    /// <summary>An empty retained-field set, for documents built from the domain rather than a file.</summary>
    public static IReadOnlyDictionary<string, JsonElement> NoUnknownFields { get; } =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    /// <summary>True when <see cref="SchemaVersion"/> is one this build promises to render.</summary>
    public bool HasSupportedSchemaVersion =>
        SchemaVersion >= OldestReadableSchemaVersion && SchemaVersion <= CurrentSchemaVersion;

    /// <summary>
    /// The domain view, with TTC's load-time repairs applied. Every repair is a display, grouping or
    /// write concern; none of them is written back into the document.
    /// </summary>
    public SessionState ToSessionState() =>
        new(
            SessionId,
            string.IsNullOrWhiteSpace(Name) ? SessionListProjection.UnnamedFallback : Name,
            StartedAt,
            EndedAt,
            [.. Entries.Select(entry => entry.Entry)]);

    /// <summary>
    /// The document for a live session, optionally retaining the unrecognised fields of the file it came
    /// from. Retained entry fields are matched by <b>id</b>, because ids are keys and survive both edits
    /// and soft deletes.
    ///
    /// Always writes <see cref="CurrentSchemaVersion"/>, whatever the original file claimed.
    /// </summary>
    public static SessionDocument FromSessionState(SessionState state, SessionDocument? retaining = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        Dictionary<int, IReadOnlyDictionary<string, JsonElement>> retained = [];

        if(retaining is not null)
        {
            foreach(EntryDocument entry in retaining.Entries)
            {
                retained[entry.Entry.Id] = entry.UnknownFields;
            }
        }

        return new SessionDocument(
            CurrentSchemaVersion,
            state.SessionId,
            state.Name,
            state.StartedAt,
            state.EndedAt,
            [
                .. state.Entries
                    .OrderBy(entry => entry.Id)
                    .Select(entry => new EntryDocument(
                        entry,
                        retained.TryGetValue(entry.Id, out IReadOnlyDictionary<string, JsonElement>? unknown)
                            ? unknown
                            : NoUnknownFields)),
            ],
            retaining?.UnknownFields ?? NoUnknownFields);
    }
}
