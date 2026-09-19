using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using TimeTrackerDesktop.Domain;

namespace TimeTrackerDesktop.Persistence;

/// <summary>
/// Reads and writes TTC's session files (plan Task 2.1).
///
/// The writer reproduces TTC's own settings exactly — camelCase, two-space indent, the default
/// (HTML-escaping) encoder, no trailing newline — so re-writing a file we read changes nothing except
/// the schemaVersion upgrade. The compatibility tests assert that byte for byte against the captured
/// fixtures, which is the only version of "compatible" worth claiming.
///
/// Reading uses <see cref="JsonNode"/> rather than a typed deserialize because the point is to keep the
/// fields we do <b>not</b> understand: a typed reader would drop them silently, and an older build
/// sharing a session directory with a newer one would then quietly delete the newer build's data.
/// </summary>
public static class TtcJsonSerializer
{
    /// <summary>
    /// Fields this build understands and therefore never treats as unknown.
    ///
    /// <c>isValid</c> is listed deliberately: TTC keeps it as a runtime sentinel and never persists it,
    /// so we drop it rather than round-tripping a field that is not part of the format.
    /// </summary>
    private static readonly HashSet<string> KnownEnvelopeFields = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "sessionId",
        "name",
        "startedAt",
        "endedAt",
        "entries",
    };

    private static readonly HashSet<string> KnownEntryFields = new(StringComparer.Ordinal)
    {
        "id",
        "startTime",
        "endTime",
        "task",
        "description",
        "logged",
        "isComplete",
        "isDeleted",
        "isValid",
    };

    /// <summary>Reads a session file, throwing when it cannot be used.</summary>
    /// <exception cref="JsonException">The text is not valid JSON.</exception>
    /// <exception cref="InvalidDataException">
    /// The text is JSON but not a session this build can open — an unsupported <c>schemaVersion</c>, or a
    /// <c>sessionId</c> that is not a GUID.
    /// </exception>
    public static SessionDocument Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        // parse outside the interpreter so malformed JSON stays a JSON problem for the caller
        JsonNode? root = JsonNode.Parse(json);

        return Interpret(root, out SessionDocument? document, out string? failure)
            ? document!
            : throw new InvalidDataException(failure);
    }

    /// <summary>
    /// Reads a session file, reporting why a file was refused instead of swallowing it. TTC's <c>dev</c>
    /// reader behaves this way so a skipped file can be listed to the user.
    /// </summary>
    public static bool TryRead(string json, out SessionDocument? document, out string? failure)
    {
        ArgumentNullException.ThrowIfNull(json);

        document = null;
        failure = null;

        JsonNode? root;

        try
        {
            root = JsonNode.Parse(json);
        }
        catch(JsonException exception)
        {
            failure = $"The file is not valid JSON: {exception.Message}";
            return false;
        }

        return Interpret(root, out document, out failure);
    }

    /// <summary>
    /// Writes a document as v2 JSON, merging in any retained fields this build does not understand.
    ///
    /// Built with <see cref="Utf8JsonWriter"/> rather than a <c>JsonNode</c> tree for one concrete
    /// reason: the timestamp overloads of <c>WriteString</c> write ISO 8601 text <b>without</b> running it
    /// through the encoder, and that is what TTC's files show. The default encoder escapes <c>+</c> as
    /// <c>\u002B</c>, so a <c>+00:00</c> offset routed through a string value would come out as
    /// <c>\u002B00:00</c> and no longer match a TTC file byte for byte.
    ///
    /// A retained field can never override a known one: known fields are written first and a colliding
    /// key is skipped, so a file's stale copy of a field we own cannot resurrect itself.
    /// </summary>
    public static string Write(SessionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        using MemoryStream stream = new();

        using(Utf8JsonWriter writer = new(stream, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.Default,
        }))
        {
            writer.WriteStartObject();

            writer.WriteNumber("schemaVersion", SessionDocument.CurrentSchemaVersion);
            writer.WriteString("sessionId", document.SessionId.ToString("D", CultureInfo.InvariantCulture));
            WriteStringOrNull(writer, "name", document.Name);
            writer.WriteString("startedAt", document.StartedAt);
            WriteTimestampOrNull(writer, "endedAt", document.EndedAt);

            writer.WriteStartArray("entries");

            foreach(EntryDocument entry in document.Entries.OrderBy(entry => entry.Entry.Id))
            {
                WriteEntry(writer, entry);
            }

            writer.WriteEndArray();

            WriteUnknownFields(writer, document.UnknownFields, KnownEnvelopeFields);

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static bool Interpret(JsonNode? root, out SessionDocument? document, out string? failure)
    {
        document = null;
        failure = null;

        if(root is not JsonObject envelope)
        {
            failure = "The file's top level is not a JSON object, so it is not a session file.";
            return false;
        }

        int schemaVersion = ReadInt(envelope, "schemaVersion") ?? 0;

        if(schemaVersion < SessionDocument.OldestReadableSchemaVersion
           || schemaVersion > SessionDocument.CurrentSchemaVersion)
        {
            failure = schemaVersion == 0
                ? "The file has no schemaVersion, so it is not a TimeTracker session file."
                : $"The file uses schemaVersion {schemaVersion}, which this build cannot open "
                  + $"(it reads {SessionDocument.OldestReadableSchemaVersion} to {SessionDocument.CurrentSchemaVersion}).";
            return false;
        }

        if(!TryReadGuid(envelope, "sessionId", out Guid sessionId))
        {
            failure = "The file's sessionId is missing or is not a GUID, so the session cannot be identified.";
            return false;
        }

        document = new SessionDocument(
            schemaVersion,
            sessionId,
            ReadString(envelope, "name"),

            // a missing startedAt is left at its default rather than invented - the same rule entries
            // get for a missing startTime
            TryReadTimestamp(envelope, "startedAt", out DateTimeOffset startedAt) ? startedAt : default,
            TryReadTimestamp(envelope, "endedAt", out DateTimeOffset endedAt) ? endedAt : null,
            ReadEntries(envelope),
            UnknownFields(envelope, KnownEnvelopeFields));

        return true;
    }

    /// <summary>
    /// Reads the entry array, applying TTC's repairs: an absent or non-array <c>entries</c> becomes an
    /// empty list, a <c>null</c> element is dropped, and duplicate ids collapse with the last occurrence
    /// winning so a keyed reader and a positional reader cannot disagree about which entry an id names.
    /// </summary>
    private static IReadOnlyList<EntryDocument> ReadEntries(JsonObject envelope)
    {
        if(envelope["entries"] is not JsonArray array)
        {
            return [];
        }

        Dictionary<int, EntryDocument> byId = [];

        foreach(JsonNode? node in array)
        {
            if(node is JsonObject entry)
            {
                EntryDocument document = ReadEntry(entry);
                byId[document.Entry.Id] = document;
            }
        }

        return [.. byId.Values.OrderBy(entry => entry.Entry.Id)];
    }

    private static EntryDocument ReadEntry(JsonObject entry) =>
        new(
            new TimeEntry(
                Id: ReadInt(entry, "id") ?? 0,
                StartTime: TryReadTimestamp(entry, "startTime", out DateTimeOffset startTime) ? startTime : default,
                EndTime: TryReadTimestamp(entry, "endTime", out DateTimeOffset endTime) ? endTime : null,

                // the one field TTC invents a value for, because "none" is what the app itself stores for
                // an entry with no task. An empty string is NOT null and is preserved as written.
                Task: ReadString(entry, "task") ?? TimeEntry.NoTask,
                Description: ReadString(entry, "description") ?? string.Empty,
                Logged: ReadBool(entry, "logged"),
                IsComplete: ReadBool(entry, "isComplete"),

                // absent in a v1 file, and absent must never mean deleted
                IsDeleted: ReadBool(entry, "isDeleted")),
            UnknownFields(entry, KnownEntryFields));

    private static void WriteEntry(Utf8JsonWriter writer, EntryDocument document)
    {
        TimeEntry entry = document.Entry;

        writer.WriteStartObject();

        writer.WriteNumber("id", entry.Id);
        writer.WriteString("startTime", entry.StartTime);

        // an open entry is written with a null end time, so it can never carry a stale one
        WriteTimestampOrNull(writer, "endTime", entry.IsComplete ? entry.EndTime : null);

        WriteStringOrNull(writer, "task", entry.Task);
        WriteStringOrNull(writer, "description", entry.Description);
        writer.WriteBoolean("logged", entry.Logged);
        writer.WriteBoolean("isComplete", entry.IsComplete);
        writer.WriteBoolean("isDeleted", entry.IsDeleted);

        WriteUnknownFields(writer, document.UnknownFields, KnownEntryFields);

        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes retained fields after the known ones, skipping any key the format already owns. Without the
    /// skip the file would gain a second copy of a field we are responsible for, and which copy wins
    /// would be up to the next reader.
    /// </summary>
    private static void WriteUnknownFields(
        Utf8JsonWriter writer,
        IReadOnlyDictionary<string, JsonElement> unknownFields,
        HashSet<string> knownFields)
    {
        foreach((string key, JsonElement value) in unknownFields)
        {
            if(!knownFields.Contains(key))
            {
                writer.WritePropertyName(key);
                value.WriteTo(writer);
            }
        }
    }

    private static void WriteStringOrNull(Utf8JsonWriter writer, string name, string? value)
    {
        if(value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteTimestampOrNull(Utf8JsonWriter writer, string name, DateTimeOffset? value)
    {
        if(value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value.Value);
        }
    }

    private static IReadOnlyDictionary<string, JsonElement> UnknownFields(JsonObject source, HashSet<string> knownFields)
    {
        Dictionary<string, JsonElement> unknown = new(StringComparer.Ordinal);

        foreach((string key, JsonNode? value) in source)
        {
            if(!knownFields.Contains(key) && value is not null)
            {
                unknown[key] = JsonDocument.Parse(value.ToJsonString()).RootElement.Clone();
            }
        }

        return unknown;
    }

    private static int? ReadInt(JsonObject source, string name) =>
        source[name] is JsonValue value && value.TryGetValue(out int result) ? result : null;

    private static string? ReadString(JsonObject source, string name) =>
        source[name] is JsonValue value && value.TryGetValue(out string? result) ? result : null;

    private static bool ReadBool(JsonObject source, string name) =>
        source[name] is JsonValue value && value.TryGetValue(out bool result) && result;

    private static bool TryReadGuid(JsonObject source, string name, out Guid result)
    {
        result = Guid.Empty;

        return source[name] is JsonValue value
            && value.TryGetValue(out string? text)
            && Guid.TryParse(text, out result);
    }

    /// <summary>
    /// Parses a timestamp, keeping the recorded offset. A value with no offset at all is interpreted in
    /// the reading machine's zone by <see cref="DateTimeOffset.TryParse(string, IFormatProvider, DateTimeStyles, out DateTimeOffset)"/>,
    /// which is the one case where an offset can be invented — TTC always writes one, so this only
    /// affects a hand-edited file.
    /// </summary>
    private static bool TryReadTimestamp(JsonObject source, string name, out DateTimeOffset result)
    {
        result = default;

        return source[name] is JsonValue value
            && value.TryGetValue(out string? text)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out result);
    }
}
