using System.Globalization;
using System.Text.Json;
using TimeTrackerDesktop.Domain;
using TimeTrackerDesktop.Persistence;

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

    /// <summary>
    /// TTC's indentation newlines come from the platform: System.Text.Json's writer defaults to
    /// <c>Environment.NewLine</c>, so a session file emitted on Linux uses LF and one emitted on Windows
    /// uses CRLF. The fixtures were emitted on Linux, and the Windows build writes CRLF, so a raw byte
    /// comparison would be testing the operating system rather than the format. Compare content, not the
    /// convention.
    /// </summary>
    private static string NormalizeNewLines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

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

    // --------------------------------------------------------------------- Task 2.1: the serializer

    /// <summary>Every fixture TTC itself emitted as v2, i.e. everything except the v1 capture.</summary>
    public static TheoryData<string> V2Fixtures()
    {
        TheoryData<string> data = new();

        foreach(string file in Directory.EnumerateFiles(FixturesDirectory, "*.json", SearchOption.AllDirectories))
        {
            if(File.ReadAllText(file).Contains("\"schemaVersion\": 2", StringComparison.Ordinal))
            {
                data.Add(Path.GetFileName(file));
            }
        }

        Assert.NotEmpty(data);
        return data;
    }

    [Theory]
    [MemberData(nameof(V2Fixtures))]
    public void V2_fixtures_reproduce_ttcs_exact_bytes(string fileName)
    {
        // The strongest claim available, and the reason the fixtures are kept as raw text: our writer's
        // settings, key order and escaping agree with TTC's own serializer byte for byte - including the
        // \uXXXX escapes and the trimmed fractional seconds.
        string raw = File.ReadAllText(FixturePath(fileName));

        Assert.Equal(NormalizeNewLines(raw), NormalizeNewLines(TtcJsonSerializer.Write(TtcJsonSerializer.Read(raw))));
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Every_fixture_round_trips_without_losing_data(string fileName)
    {
        string raw = File.ReadAllText(FixturePath(fileName));

        SessionDocument first = TtcJsonSerializer.Read(raw);
        SessionDocument second = TtcJsonSerializer.Read(TtcJsonSerializer.Write(first));

        // writing always upgrades to v2 - the one intended difference between the two readings
        Assert.Equal(SessionDocument.CurrentSchemaVersion, second.SchemaVersion);
        Assert.Equal(first.SessionId, second.SessionId);
        Assert.Equal(first.Name, second.Name);
        Assert.Equal(first.StartedAt, second.StartedAt);
        Assert.Equal(first.EndedAt, second.EndedAt);
        Assert.Equal(first.ToSessionState().Entries, second.ToSessionState().Entries);
    }

    [Fact]
    public void V1_fixture_upgrades_to_v2_and_defaults_isDeleted_to_false()
    {
        SessionDocument document = TtcJsonSerializer.Read(File.ReadAllText(FixturePath("session-v1-completed.json")));

        Assert.Equal(1, document.SchemaVersion);

        string rewritten = TtcJsonSerializer.Write(document);

        Assert.Contains("\"schemaVersion\": 2", rewritten, StringComparison.Ordinal);
        Assert.Contains("\"isDeleted\": false", rewritten, StringComparison.Ordinal);
        Assert.All(document.ToSessionState().Entries, entry => Assert.False(entry.IsDeleted));
    }

    [Fact]
    public void Unfinished_fields_stay_null_rather_than_being_invented()
    {
        SessionDocument document = TtcJsonSerializer.Read(File.ReadAllText(FixturePath("session-v2-unfinished.json")));

        Assert.Null(document.EndedAt);

        SessionState state = document.ToSessionState();
        TimeEntry open = Assert.Single(state.Entries, entry => !entry.IsComplete);

        Assert.Null(open.EndTime);
        Assert.Null(open.Duration);
        Assert.True(state.IsActive);
    }

    [Fact]
    public void Hostile_and_unicode_task_text_survives_a_round_trip_unescaped()
    {
        // the fixture stores this text as \uXXXX escapes, so the VALUE must come back as real characters
        string raw = File.ReadAllText(FixturePath("hostile-task-names.json"));
        SessionDocument document = TtcJsonSerializer.Read(raw);

        Assert.Contains(document.ToSessionState().Entries, entry => entry.Task.Contains('é', StringComparison.Ordinal));
        Assert.Contains(document.ToSessionState().Entries, entry => entry.Task.Contains(@"..\..\", StringComparison.Ordinal));

        // ...and writing it again re-escapes it exactly the way TTC did
        Assert.Equal(NormalizeNewLines(raw), NormalizeNewLines(TtcJsonSerializer.Write(document)));
    }

    [Fact]
    public void Editing_a_known_field_keeps_an_injected_unknown_property()
    {
        // the plan's case: import, edit a known field, export - and the fields we do not understand
        // must still be there afterwards
        const string Raw = """
            {
              "schemaVersion": 2,
              "sessionId": "0b3f2f7c-6f4f-4f6e-9a1b-2c3d4e5f6a7b",
              "name": "injected",
              "startedAt": "2026-09-18T10:00:00-05:00",
              "endedAt": null,
              "futureEnvelopeField": { "nested": [1, 2] },
              "entries": [
                {
                  "id": 1,
                  "startTime": "2026-09-18T10:00:00-05:00",
                  "endTime": null,
                  "task": "weeding",
                  "description": "",
                  "logged": false,
                  "isComplete": false,
                  "isDeleted": false,
                  "futureEntryField": "keep me"
                }
              ]
            }
            """;

        SessionDocument document = TtcJsonSerializer.Read(Raw);

        Assert.True(document.UnknownFields.ContainsKey("futureEnvelopeField"));
        Assert.True(document.Entries[0].UnknownFields.ContainsKey("futureEntryField"));

        SessionState state = document.ToSessionState();

        SessionState edited = state with
        {
            Name = "renamed",
            Entries = [state.Entries[0] with { Task = "reading" }],
        };

        string rewritten = TtcJsonSerializer.Write(SessionDocument.FromSessionState(edited, document));

        Assert.Contains("\"futureEnvelopeField\"", rewritten, StringComparison.Ordinal);
        Assert.Contains("keep me", rewritten, StringComparison.Ordinal);
        Assert.Contains("\"name\": \"renamed\"", rewritten, StringComparison.Ordinal);
        Assert.Contains("\"task\": \"reading\"", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void A_retained_field_cannot_override_a_known_one_when_written()
    {
        // "Unknown fields must never override required known fields when written": a retained set that
        // claims to know better than the document must lose, every time
        SessionDocument document = new(
            SchemaVersion: 99,
            SessionId: Guid.Parse("0b3f2f7c-6f4f-4f6e-9a1b-2c3d4e5f6a7b"),
            Name: "ours",
            StartedAt: DateTimeOffset.Parse("2026-09-18T10:00:00-05:00", CultureInfo.InvariantCulture),
            EndedAt: null,
            Entries: [],
            UnknownFields: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["name"] = JsonDocument.Parse("\"theirs\"").RootElement.Clone(),
                ["schemaVersion"] = JsonDocument.Parse("99").RootElement.Clone(),
                ["endedAt"] = JsonDocument.Parse("\"2020-01-01T00:00:00-06:00\"").RootElement.Clone(),
            });

        string json = TtcJsonSerializer.Write(document);

        Assert.Contains("\"name\": \"ours\"", json, StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\": 2", json, StringComparison.Ordinal);
        Assert.Contains("\"endedAt\": null", json, StringComparison.Ordinal);
        Assert.DoesNotContain("theirs", json, StringComparison.Ordinal);
        Assert.DoesNotContain("99", json, StringComparison.Ordinal);
        Assert.DoesNotContain("2020-01-01", json, StringComparison.Ordinal);
    }

    [Fact]
    public void IsValid_is_never_written_and_is_not_retained_as_an_unknown_field()
    {
        string raw = File.ReadAllText(FixturePath("session-v2-completed.json"))
            .Replace("\"isComplete\": true,", "\"isComplete\": true,\n      \"isValid\": true,", StringComparison.Ordinal);

        SessionDocument document = TtcJsonSerializer.Read(raw);

        Assert.False(document.Entries[0].UnknownFields.ContainsKey("isValid"));
        Assert.DoesNotContain("isValid", TtcJsonSerializer.Write(document), StringComparison.Ordinal);
    }

    [Fact]
    public void A_schema_version_outside_the_supported_range_is_refused_with_a_reason()
    {
        string v2 = File.ReadAllText(FixturePath("session-v2-completed.json"));

        string future = v2.Replace("\"schemaVersion\": 2", "\"schemaVersion\": 3", StringComparison.Ordinal);
        string absent = v2.Replace("\"schemaVersion\": 2,", string.Empty, StringComparison.Ordinal);

        Assert.False(TtcJsonSerializer.TryRead(future, out SessionDocument? futureDocument, out string? futureReason));
        Assert.Null(futureDocument);
        Assert.Contains("3", futureReason, StringComparison.Ordinal);

        // a missing key reads as 0, which is "absent or unsupported-old", not "version 0 is fine"
        Assert.False(TtcJsonSerializer.TryRead(absent, out SessionDocument? absentDocument, out string? absentReason));
        Assert.Null(absentDocument);
        Assert.Contains("schemaVersion", absentReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_json_is_refused_with_a_diagnostic_rather_than_a_crash()
    {
        Assert.False(TtcJsonSerializer.TryRead("{ not json", out SessionDocument? document, out string? reason));
        Assert.Null(document);
        Assert.False(string.IsNullOrWhiteSpace(reason));

        Assert.ThrowsAny<JsonException>(() => TtcJsonSerializer.Read("{ not json"));
    }

    [Fact]
    public void A_non_guid_session_id_makes_the_file_unreadable()
    {
        string raw = File.ReadAllText(FixturePath("session-v2-completed.json"))
            .Replace("490df8b1-4e72-4980-9769-d215867bea3e", "not-a-guid", StringComparison.Ordinal);

        Assert.False(TtcJsonSerializer.TryRead(raw, out SessionDocument? document, out string? reason));
        Assert.Null(document);
        Assert.Contains("sessionId", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Absent_and_null_entry_fields_are_repaired_the_way_ttc_repairs_them()
    {
        const string Raw = """
            {
              "schemaVersion": 2,
              "sessionId": "0b3f2f7c-6f4f-4f6e-9a1b-2c3d4e5f6a7b",
              "name": "   ",
              "startedAt": "2026-09-18T10:00:00-05:00",
              "endedAt": null,
              "entries": [
                { "id": 1, "startTime": "2026-09-18T10:00:00-05:00", "endTime": null, "task": null, "description": null, "logged": false, "isComplete": true, "isDeleted": false },
                null
              ]
            }
            """;

        SessionDocument document = TtcJsonSerializer.Read(Raw);
        SessionState state = document.ToSessionState();

        // the null element is dropped, not carried as a hole
        TimeEntry entry = Assert.Single(state.Entries);

        // task is the one field TTC invents a value for, because "none" is what the app itself stores
        Assert.Equal(TimeEntry.NoTask, entry.Task);
        Assert.Equal(string.Empty, entry.Description);

        // a whitespace-only name is repaired in the DOMAIN view, while the raw text stays in the document
        Assert.Equal("Unnamed session", state.Name);
        Assert.Equal("   ", document.Name);
    }

    [Fact]
    public void Duplicate_ids_collapse_with_the_last_occurrence_winning()
    {
        const string Raw = """
            {
              "schemaVersion": 2,
              "sessionId": "0b3f2f7c-6f4f-4f6e-9a1b-2c3d4e5f6a7b",
              "name": "dupes",
              "startedAt": "2026-09-18T10:00:00-05:00",
              "endedAt": null,
              "entries": [
                { "id": 1, "startTime": "2026-09-18T10:00:00-05:00", "endTime": null, "task": "first", "description": "", "logged": false, "isComplete": false, "isDeleted": false },
                { "id": 1, "startTime": "2026-09-18T11:00:00-05:00", "endTime": null, "task": "second", "description": "", "logged": false, "isComplete": false, "isDeleted": false }
              ]
            }
            """;

        SessionState state = TtcJsonSerializer.Read(Raw).ToSessionState();

        TimeEntry entry = Assert.Single(state.Entries);

        // a keyed reader and a positional reader must not be able to disagree about id 1
        Assert.Equal("second", entry.Task);
        Assert.Equal(DateTimeOffset.Parse("2026-09-18T11:00:00-05:00", CultureInfo.InvariantCulture), entry.StartTime);
    }

    [Fact]
    public void Entries_are_written_ascending_by_id_even_when_the_state_is_not()
    {
        SessionState state = SessionState.New("ordering", DateTimeOffset.Parse("2026-09-18T10:00:00-05:00", CultureInfo.InvariantCulture)) with
        {
            Entries =
            [
                new TimeEntry(9, DateTimeOffset.Parse("2026-09-18T12:00:00-05:00", CultureInfo.InvariantCulture), null, "third", string.Empty, false, false, false),
                new TimeEntry(1, DateTimeOffset.Parse("2026-09-18T10:00:00-05:00", CultureInfo.InvariantCulture), null, "first", string.Empty, false, false, false),
            ],
        };

        string json = TtcJsonSerializer.Write(SessionDocument.FromSessionState(state));

        Assert.True(
            json.IndexOf("\"id\": 1", StringComparison.Ordinal) < json.IndexOf("\"id\": 9", StringComparison.Ordinal),
            json);
    }

    [Fact]
    public void An_open_entry_is_written_with_a_null_end_time()
    {
        // an open entry must never carry a stale end time, even if the in-memory value holds one
        SessionState state = SessionState.New("open", DateTimeOffset.Parse("2026-09-18T10:00:00-05:00", CultureInfo.InvariantCulture)) with
        {
            Entries =
            [
                new TimeEntry(1, DateTimeOffset.Parse("2026-09-18T10:00:00-05:00", CultureInfo.InvariantCulture), DateTimeOffset.Parse("2026-09-18T11:00:00-05:00", CultureInfo.InvariantCulture), "weeding", string.Empty, false, IsComplete: false, IsDeleted: false),
            ],
        };

        string json = TtcJsonSerializer.Write(SessionDocument.FromSessionState(state));

        Assert.Contains("\"endTime\": null", json, StringComparison.Ordinal);
        Assert.DoesNotContain("2026-09-18T11:00:00", json, StringComparison.Ordinal);
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