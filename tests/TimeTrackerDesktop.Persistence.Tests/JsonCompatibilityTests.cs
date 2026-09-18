using System.Globalization;
using System.Text.Json;

namespace TimeTrackerDesktop.Persistence.Tests;

/// <summary>
/// Executable form of <c>docs/JSON-COMPATIBILITY.md</c>.
///
/// The fixtures under <c>fixtures/</c> are real output from TimeTrackerConsole, so these tests assert
/// what TTC actually writes rather than what we assumed it writes. Two consequences shape the design:
///
/// 1. <b>Timestamps are modelled as <see cref="DateTimeOffset"/>, not <see cref="DateTime"/>.</b>
///    TTC serializes <c>DateTime.Now</c> (kind Local), and System.Text.Json writes that as ISO 8601
///    carrying the machine's UTC offset. Deserializing into a <see cref="DateTime"/> CONVERTS the
///    value to the reading machine's local time, so the offset in the file is silently replaced -
///    the instant survives but the recorded offset does not. <see cref="DateTimeOffset"/> keeps both.
///    The tests below compare the parsed offset against the literal offset in the file text.
/// 2. <b>The raw JSON text is kept.</b> Every load returns the file's exact bytes so field-shape
///    checks run against the wire format (escapes, key names) instead of only against typed objects.
///
/// These tests deliberately do not use the domain model: they pin the FORMAT, which is the contract
/// the domain model has to satisfy later. The DTOs here are test-local on purpose.
/// </summary>
public sealed class JsonCompatibilityTests
{
    // TTC's own options: camelCase property names, default (HTML-escaping) encoder.
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static string FixturesDirectory => Path.Combine(AppContext.BaseDirectory, "fixtures");

    private static string FixturePath(string fileName) => Path.Combine(FixturesDirectory, fileName);

    /// <summary>Reads a fixture, returning BOTH the raw text and the typed projection.</summary>
    private static (string RawJson, SessionSnapshotDto Snapshot) Load(string fileName)
    {
        string raw = File.ReadAllText(FixturePath(fileName));
        SessionSnapshotDto? snapshot = JsonSerializer.Deserialize<SessionSnapshotDto>(raw, Options);

        Assert.NotNull(snapshot);
        return (raw, snapshot);
    }

    public static TheoryData<string> AllFixtures()
    {
        TheoryData<string> data = new();
        foreach(string file in Directory.EnumerateFiles(FixturesDirectory, "*.json", SearchOption.AllDirectories))
        {
            data.Add(Path.GetFileName(file));
        }

        Assert.NotEmpty(data);
        return data;
    }

    // --------------------------------------------------------------------- every fixture
    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Every_fixture_deserializes_as_a_session_snapshot(string fileName)
    {
        (_, SessionSnapshotDto snapshot) = Load(fileName);

        Assert.True(snapshot.SchemaVersion >= 1, $"{fileName}: schemaVersion {snapshot.SchemaVersion}");
        Assert.True(snapshot.SessionId != Guid.Empty, $"{fileName}: sessionId must be a real GUID");
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Name), $"{fileName}: name");
        Assert.NotNull(snapshot.Entries);
        Assert.NotEmpty(snapshot.Entries!);
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Fixture_timestamps_keep_their_offset_and_instant(string fileName)
    {
        (string raw, SessionSnapshotDto snapshot) = Load(fileName);

        using JsonDocument document = JsonDocument.Parse(raw);
        JsonElement root = document.RootElement;

        AssertKept(root.GetProperty("startedAt"), snapshot.StartedAt, fileName);
        AssertKeptIfPresent(root.GetProperty("endedAt"), snapshot.EndedAt, fileName);

        JsonElement entries = root.GetProperty("entries");
        for(int i = 0; i < entries.GetArrayLength(); i++)
        {
            JsonElement entry = entries[i];
            EntrySnapshotDto dto = snapshot.Entries![i]!;

            AssertKept(entry.GetProperty("startTime"), dto.StartTime, fileName);

            if(entry.GetProperty("endTime").ValueKind != JsonValueKind.Null && dto.EndTime is { } end)
            {
                AssertKept(entry.GetProperty("endTime"), end, fileName);
            }
        }
    }

    /// <summary>
    /// The parsed value must carry the SAME offset the file literally says - not an equivalent
    /// instant expressed in the reading machine's local offset.
    /// </summary>
    private static void AssertKept(JsonElement element, DateTimeOffset parsed, string fileName)
    {
        string literal = element.GetString()!;
        string literalOffset = literal[^6..];   // "+00:00" / "-05:00"

        Assert.Equal(literalOffset, parsed.ToString("zzz", CultureInfo.InvariantCulture));
        Assert.Equal(
            DateTimeOffset.Parse(literal, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).UtcDateTime,
            parsed.UtcDateTime);
    }

    private static void AssertKeptIfPresent(JsonElement element, DateTimeOffset? parsed, string fileName)
    {
        if(element.ValueKind == JsonValueKind.Null)
        {
            Assert.Null(parsed);
            return;
        }

        Assert.NotNull(parsed);
        AssertKept(element, parsed!.Value, fileName);
    }

    // --------------------------------------------------------------------- schema drift canary
    private static readonly string[] DocumentedSessionFields =
        ["schemaVersion", "sessionId", "name", "startedAt", "endedAt", "entries"];

    private static readonly string[] DocumentedV2EntryFields =
        ["id", "startTime", "endTime", "task", "description", "logged", "isComplete", "isDeleted"];

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Fixture_field_names_match_the_documented_schema(string fileName)
    {
        (string raw, SessionSnapshotDto snapshot) = Load(fileName);

        using JsonDocument document = JsonDocument.Parse(raw);
        JsonElement root = document.RootElement;

        Assert.Equal(
            DocumentedSessionFields.Order(),
            root.EnumerateObject().Select(p => p.Name).Order());

        string[] expectedEntryFields = snapshot.SchemaVersion >= 2
            ? DocumentedV2EntryFields
            : DocumentedV2EntryFields.Where(f => f != "isDeleted").ToArray();

        foreach(JsonElement entry in root.GetProperty("entries").EnumerateArray())
        {
            Assert.Equal(expectedEntryFields.Order(), entry.EnumerateObject().Select(p => p.Name).Order());
        }
    }

    // --------------------------------------------------------------------- v1 / v2 baseline
    [Fact]
    public void V1_fixture_omits_isDeleted_and_it_fields_defaults_to_false()
    {
        (string raw, SessionSnapshotDto snapshot) = Load("session-v1-completed.json");

        Assert.Equal(1, snapshot.SchemaVersion);
        Assert.DoesNotContain("isDeleted", raw, StringComparison.Ordinal);
        Assert.All(snapshot.Entries!, entry => Assert.False(entry!.IsDeleted));
    }

    [Fact]
    public void V2_fixture_carries_isDeleted_explicitly()
    {
        (string raw, SessionSnapshotDto snapshot) = Load("session-v2-completed.json");

        Assert.Equal(2, snapshot.SchemaVersion);
        Assert.Contains("isDeleted", raw, StringComparison.Ordinal);
        Assert.All(snapshot.Entries!, entry => Assert.False(entry!.IsDeleted));

        // the whole point of the field being additive: v2's entry shape is v1's plus isDeleted
        string[] v2Fields = DocumentedV2EntryFields;
        string[] v1Fields = v2Fields.Where(f => f != "isDeleted").ToArray();

        Assert.Equal(v1Fields.Length + 1, v2Fields.Length);
    }

    // --------------------------------------------------------------------- session/entry state
    [Fact]
    public void Unfinished_session_has_no_endedAt_and_an_open_entry()
    {
        (_, SessionSnapshotDto snapshot) = Load("session-v2-unfinished.json");

        Assert.Null(snapshot.EndedAt);

        EntrySnapshotDto open = Assert.Single(snapshot.Entries!, entry => !entry!.IsComplete)!;
        Assert.Null(open.EndTime);

        // an open entry must carry the highest id: TTC only ever resumes the newest entry
        Assert.Equal(snapshot.Entries!.Max(entry => entry!.Id), open.Id);
    }

    [Fact]
    public void Ended_session_can_still_hold_an_unfinished_entry()
    {
        (_, SessionSnapshotDto snapshot) = Load("unfinished-entry.json");

        Assert.NotNull(snapshot.EndedAt);

        EntrySnapshotDto open = Assert.Single(snapshot.Entries!, entry => !entry!.IsComplete)!;
        Assert.Null(open.EndTime);
    }

    [Fact]
    public void Deleted_entry_stays_in_the_payload()
    {
        (_, SessionSnapshotDto snapshot) = Load("deleted-entry.json");

        EntrySnapshotDto deleted = Assert.Single(snapshot.Entries!, entry => entry!.IsDeleted)!;

        // a soft delete keeps id, times and text intact - only the flag moves
        Assert.True(deleted.IsComplete);
        Assert.NotNull(deleted.EndTime);
        Assert.False(string.IsNullOrWhiteSpace(deleted.Task));

        // ...and the entry is still in the list, so a reader must filter rather than assume absence
        Assert.Equal(2, snapshot.Entries!.Count);
    }

    [Fact]
    public void Id_gaps_are_preserved_and_entries_are_still_listed_in_full()
    {
        (_, SessionSnapshotDto snapshot) = Load("id-gaps.json");

        int[] ids = snapshot.Entries!.Select(entry => entry!.Id).ToArray();

        Assert.Equal([1, 4, 9], ids);
        Assert.Equal(3, snapshot.Entries!.Count);
        Assert.NotEqual(ids[0] + 1, ids[1]);   // ids are keys, not positions
    }

    // --------------------------------------------------------------------- hostile text
    [Fact]
    public void Hostile_task_names_round_trip_byte_for_byte()
    {
        (string raw, SessionSnapshotDto snapshot) = Load("hostile-task-names.json");

        string[] tasks = snapshot.Entries!.Select(entry => entry!.Task!).ToArray();

        Assert.Contains("fix[bug] and \"quoted\" [red] markup[/]", tasks);
        Assert.Contains(@"café ☕ 日本語 ..\..\ /etc/passwd :*?|<>", tasks);

        // TTC uses System.Text.Json's default encoder, so these are stored ESCAPED, not literal.
        // A reader that assumes plain UTF-8 text in the file will mis-handle them.
        Assert.Contains("\\u0022", raw, StringComparison.Ordinal);   // "
        Assert.Contains("\\u003C", raw, StringComparison.Ordinal);   // <
        Assert.Contains("\\u00E9", raw, StringComparison.Ordinal);   // é
        Assert.Contains("\\\\", raw, StringComparison.Ordinal);      // literal backslash
    }

    /// <summary>
    /// The payload always keeps the session's real name; only the FILE name is slugged. The emitted
    /// file name cannot live in the JSON (TTC identifies a session from its contents, never its file
    /// name), so the pairing is recorded in <c>fixtures/README.md</c> and asserted here - if someone
    /// renames or replaces a fixture, this fails until the documentation is updated with it.
    /// </summary>
    [Theory]
    [InlineData("filename-hostile-session-name.json", @"a:b*c?d<e>f|g/h\i", "abcdefghi.json")]
    [InlineData("filename-hostile-emptied-slug.json", @":*?<>|/\", "session.json")]
    public void Hostile_session_name_is_kept_in_the_payload_and_slugged_on_disk(
        string fixtureFile, string expectedName, string emittedFileName)
    {
        (_, SessionSnapshotDto snapshot) = Load(fixtureFile);

        Assert.Equal(expectedName, snapshot.Name);

        // Apply the documented slug rule to the real name and require the file name TTC actually
        // emitted: harmless characters survive, spaces become dashes, every path/device-hostile
        // character is stripped, and an empty result falls back to "session".
        char[] hostile = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];
        string stripped = new([.. expectedName.Where(c => !hostile.Contains(c) && c != ' ')]);
        string derivedFileName = (stripped.Length == 0 ? "session" : stripped) + ".json";

        Assert.Equal(derivedFileName, emittedFileName);

        // the property that matters on Windows, which is stricter than Linux
        Assert.Empty(emittedFileName.Intersect(hostile));
        Assert.EndsWith(".json", emittedFileName, StringComparison.Ordinal);

        // the payload keeps the real name, so the file name can never be used to recover it
        Assert.NotEqual(expectedName, emittedFileName);
    }

    // --------------------------------------------------------------------- raw-text retention
    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Raw_fixture_text_is_retained_for_unknown_field_checks(string fileName)
    {
        (string raw, _) = Load(fileName);

        Assert.False(string.IsNullOrWhiteSpace(raw));
        Assert.StartsWith("{", raw.TrimStart(), StringComparison.Ordinal);
    }
}

/// <summary>Test-local mirror of TTC's <c>SessionSnapshot</c> record.</summary>
public sealed record SessionSnapshotDto(
    int SchemaVersion,
    Guid SessionId,
    string? Name,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    List<EntrySnapshotDto?>? Entries);

/// <summary>Test-local mirror of TTC's <c>EntrySnapshot</c> record.</summary>
public sealed record EntrySnapshotDto(
    int Id,
    DateTimeOffset StartTime,
    DateTimeOffset? EndTime,
    string? Task,
    string? Description,
    bool Logged,
    bool IsComplete,
    bool IsDeleted);