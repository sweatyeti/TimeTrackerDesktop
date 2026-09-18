using System.Globalization;

namespace TimeTrackerDesktop.Domain;

/// <summary>
/// The whole in-memory session: identity, name, lifecycle timestamps and the entry set. Immutable,
/// and free of any WinUI, filesystem or clock dependency — the clock arrives as an argument or via
/// <see cref="IClock"/> at the service layer.
///
/// Mirrors TTC's <c>SessionSnapshot</c> envelope (see <c>docs/JSON-COMPATIBILITY.md</c>).
/// </summary>
public sealed record SessionState(
    Guid SessionId,
    string Name,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    IReadOnlyList<TimeEntry> Entries)
{
    /// <summary>
    /// TTC's default session name. Kept as the bare date/time pattern because "Session" contains a
    /// reserved format character ('s' = seconds) and must stay outside the pattern.
    /// </summary>
    public const string NameTimestampFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>Starts a new, empty, unended session.</summary>
    public static SessionState New(string? name, DateTimeOffset startedAt) =>
        new(Guid.NewGuid(), ResolveName(name, startedAt), startedAt, EndedAt: null, Entries: []);

    /// <summary>
    /// A blank name is generated from <paramref name="at"/>; anything else is kept verbatim —
    /// including a whitespace-only name, because TTC tests with <c>IsNullOrEmpty</c> rather than
    /// <c>IsNullOrWhiteSpace</c>. Deviation: the timestamp is formatted with the invariant culture
    /// (TTC uses the current culture), so a session's name does not change with the machine locale.
    /// </summary>
    public static string ResolveName(string? name, DateTimeOffset at) =>
        string.IsNullOrEmpty(name)
            ? "Session " + at.ToString(NameTimestampFormat, CultureInfo.InvariantCulture)
            : name;

    /// <summary>
    /// The single open entry, or <c>null</c>. Throws if the session holds more than one — see
    /// <see cref="Validate"/>. Deleted entries never count as active.
    /// </summary>
    public TimeEntry? ActiveEntry => Entries.SingleOrDefault(entry => entry.IsOpen);

    /// <summary>True while any entry is open, i.e. time is being tracked.</summary>
    public bool IsActive => Entries.Any(entry => entry.IsOpen);

    /// <summary>
    /// The entries a user can still see. Soft-deleted entries stay in <see cref="Entries"/> (so they
    /// survive a restart and can be restored) but are excluded from every count, group and total.
    /// </summary>
    public IReadOnlyList<TimeEntry> LiveEntries => [.. Entries.Where(entry => !entry.IsDeleted)];

    /// <summary>
    /// The next id to mint: one past the highest id in the session, **not** the entry count.
    ///
    /// Ids are keys, and v1's hard delete left real gaps, so <c>max + 1</c> is the only rule that
    /// cannot collide with an existing entry. Deleted entries keep their id, so their slots are never
    /// reused. This is the same rule TTC applies when resuming (<c>ReseedId(maxId + 1)</c>), which
    /// means an id that was hard-deleted off the end of a v1 file can be minted again — matching TTC
    /// rather than inventing a stronger guarantee the file format cannot support.
    /// </summary>
    public int NextEntryId => Entries.Count == 0 ? 1 : Entries.Max(entry => entry.Id) + 1;

    /// <summary>
    /// Enforces the one-open-entry invariant. TTC refuses to open such a session rather than guessing
    /// which entry is running, so the load paths call this and surface the failure instead of
    /// silently dropping an entry.
    /// </summary>
    public void Validate()
    {
        if(Entries.Count(entry => entry.IsOpen) > 1)
        {
            throw new InvalidDataException("Session contains more than one unfinished entry.");
        }
    }
}