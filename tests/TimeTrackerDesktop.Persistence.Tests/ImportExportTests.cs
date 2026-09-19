using System.Globalization;
using TimeTrackerDesktop.Domain;

namespace TimeTrackerDesktop.Persistence.Tests;

/// <summary>
/// Executable form of plan Task 2.3: copy-based file interchange that stays TTC-compatible.
///
/// The rules with teeth:
///
/// - <b>Import never touches the source.</b> It is read, copied, and the copy is what gets a new name and
///   a new file. A user importing a file must not be able to damage the file they picked.
/// - <b>Collisions are <c>name.json</c>, <c>name-1.json</c>, <c>name-2.json</c></b> — deliberately not
///   TTC's <c>-2</c>-first behaviour. The plan settles this as a desktop requirement, and the same rule
///   covers export, so an export can never destroy a file that is already there.
/// - <b>Export is a copy of the persisted session.</b> It flushes first so the copy matches what a restart
///   would read back, and it does not stop an active entry or stamp the session as ended — exporting is not
///   a way of ending the day.
/// </summary>
public sealed class ImportExportTests
{
    private static readonly DateTimeOffset Noon =
        DateTimeOffset.Parse("2026-09-18T12:00:00-05:00", CultureInfo.InvariantCulture);

    private static readonly Guid KnownSessionId = Guid.Parse("0b3f2f7c-6f4f-4f6e-9a1b-2c3d4e5f6a7b");

    private static string NewTempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ttd-transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// A live session with one entry still running and no endedAt.
    ///
    /// The session id is pinned: a real live session keeps its id across reads, and a helper that minted a
    /// new one each call would make "the source is unchanged" untestable.
    /// </summary>
    private static SessionDocument LiveDocument() =>
        SessionDocument.FromSessionState(
            SessionState.New("live session", Noon) with
            {
                SessionId = KnownSessionId,
                Entries =
                [
                    new TimeEntry(1, Noon, null, "weeding", string.Empty, false, IsComplete: false, IsDeleted: false),
                ],
            });

    private static SessionTransferService ServiceFor(
        string storeDirectory,
        Func<SessionDocument> snapshot,
        out SessionFlushCoordinator coordinator)
    {
        AtomicSessionStore store = new(storeDirectory);
        coordinator = new SessionFlushCoordinator(store, Path.Combine(storeDirectory, "live.json"), snapshot, new object());

        return new SessionTransferService(store, coordinator);
    }

    /// <summary>Writes a session file that stands in for one a user picked from somewhere else.</summary>
    private static string WriteSourceFile(string directory, string name, Guid sessionId)
    {
        SessionState state = SessionState.New(name, Noon) with { SessionId = sessionId };
        string path = Path.Combine(directory, SessionFileNaming.Slugify(name) + ".json");

        new AtomicSessionStore(directory).Write(path, SessionDocument.FromSessionState(state));

        return path;
    }

    // ------------------------------------------------------------------ slugging (TTC parity)

    [Theory]
    [InlineData("site visit", "site-visit")]
    [InlineData(@"a:b*c?d<e>f|g/h\i", "abcdefghi")]
    [InlineData(@":*?<>|/\", "session")]
    [InlineData("", "session")]
    [InlineData("   ", "---")]
    public void Session_names_slug_the_way_ttc_slugs_them(string sessionName, string expected)
    {
        // the last two cases are the fixtures' own: a name that is nothing but hostile characters falls back
        // to "session", and spaces become dashes rather than being trimmed away
        Assert.Equal(expected, SessionFileNaming.Slugify(sessionName));
    }

    [Theory]
    [InlineData(0, "site-visit.json")]
    [InlineData(1, "site-visit-1.json")]
    [InlineData(2, "site-visit-2.json")]
    public void Collision_names_start_at_1_not_ttcs_2(int collisionIndex, string expected)
    {
        Assert.Equal(expected, SessionFileNaming.FileNameFor("site visit", collisionIndex));
    }

    // ------------------------------------------------------------------ import

    [Fact]
    public void Import_copies_the_file_into_storage_and_leaves_the_source_bytes_untouched()
    {
        string storeDirectory = NewTempDirectory();
        string sourcePath = WriteSourceFile(NewTempDirectory(), "site visit", KnownSessionId);
        byte[] before = File.ReadAllBytes(sourcePath);

        SessionTransferService service = ServiceFor(storeDirectory, LiveDocument, out _);
        ImportResult result = service.Import(sourcePath, storeDirectory);

        Assert.True(result.Imported);
        Assert.True(File.Exists(result.DestinationPath));
        Assert.NotEqual(sourcePath, result.DestinationPath);

        // byte-for-byte, not merely "still parses": importing must not rewrite the file the user picked
        Assert.Equal(before, File.ReadAllBytes(sourcePath));
    }

    [Fact]
    public void Import_preserves_the_session_id_and_marks_only_the_copied_name()
    {
        string storeDirectory = NewTempDirectory();
        string sourcePath = WriteSourceFile(NewTempDirectory(), "site visit", KnownSessionId);

        SessionTransferService service = ServiceFor(storeDirectory, LiveDocument, out _);
        ImportResult result = service.Import(sourcePath, storeDirectory);

        SessionDocument imported = TtcJsonSerializer.Read(File.ReadAllText(result.DestinationPath!));

        // the id travels with the session, so the copy is the same session in a different place
        Assert.Equal(KnownSessionId, imported.SessionId);
        Assert.Equal("site visit (Imported)", imported.Name);
        Assert.Equal("site visit (Imported)", imported.ToSessionState().Name);

        // ...and the source keeps its own name, which is the whole point of marking only the copy
        Assert.Equal("site visit", TtcJsonSerializer.Read(File.ReadAllText(sourcePath)).Name);
    }

    [Fact]
    public void Repeated_imports_of_one_file_land_on_the_settled_collision_sequence()
    {
        // "exactly one selected file per operation" also means each import is its own copy: importing the
        // same file three times must not silently overwrite the first two
        string storeDirectory = NewTempDirectory();
        string sourcePath = WriteSourceFile(NewTempDirectory(), "site visit", KnownSessionId);

        SessionTransferService service = ServiceFor(storeDirectory, LiveDocument, out _);

        ImportResult first = service.Import(sourcePath, storeDirectory);
        ImportResult second = service.Import(sourcePath, storeDirectory);
        ImportResult third = service.Import(sourcePath, storeDirectory);

        Assert.EndsWith("site-visit-(Imported).json", first.DestinationPath!, StringComparison.Ordinal);
        Assert.EndsWith("site-visit-(Imported)-1.json", second.DestinationPath!, StringComparison.Ordinal);
        Assert.EndsWith("site-visit-(Imported)-2.json", third.DestinationPath!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hostile_session_name_lands_on_a_safe_file_name()
    {
        string storeDirectory = NewTempDirectory();
        string sourcePath = WriteSourceFile(NewTempDirectory(), @"a:b*c?d<e>f|g/h\i", KnownSessionId);

        SessionTransferService service = ServiceFor(storeDirectory, LiveDocument, out _);
        ImportResult result = service.Import(sourcePath, storeDirectory);

        string fileName = Path.GetFileName(result.DestinationPath!);

        Assert.StartsWith("abcdefghi-(Imported)", fileName, StringComparison.Ordinal);
        Assert.Empty(fileName.Intersect(['\\', '/', ':', '*', '?', '"', '<', '>', '|']));

        // the payload keeps the real name, so the file name is never how a session is identified
        Assert.Equal(@"a:b*c?d<e>f|g/h\i (Imported)", TtcJsonSerializer.Read(File.ReadAllText(result.DestinationPath!)).Name);
    }

    [Fact]
    public void Import_refuses_an_unreadable_source_and_writes_nothing()
    {
        string storeDirectory = NewTempDirectory();
        string sourcePath = Path.Combine(NewTempDirectory(), "broken.json");
        File.WriteAllText(sourcePath, "{ not json");

        SessionTransferService service = ServiceFor(storeDirectory, LiveDocument, out _);
        ImportResult result = service.Import(sourcePath, storeDirectory);

        Assert.False(result.Imported);
        Assert.Equal(ImportOutcome.SourceUnreadable, result.Outcome);
        Assert.Null(result.DestinationPath);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
        Assert.Empty(Directory.GetFiles(storeDirectory, "*.json"));
    }

    [Fact]
    public void Import_refuses_a_session_whose_schema_this_build_cannot_open()
    {
        string storeDirectory = NewTempDirectory();
        string sourcePath = Path.Combine(NewTempDirectory(), "future.json");
        File.WriteAllText(sourcePath, File.ReadAllText(Fixture("session-v2-completed.json")).Replace("\"schemaVersion\": 2", "\"schemaVersion\": 9", StringComparison.Ordinal));

        SessionTransferService service = ServiceFor(storeDirectory, LiveDocument, out _);
        ImportResult result = service.Import(sourcePath, storeDirectory);

        Assert.Equal(ImportOutcome.SourceUnreadable, result.Outcome);
        Assert.Contains("9", result.Reason, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(storeDirectory, "*.json"));
    }

    [Fact]
    public void Import_accepts_a_real_ttc_fixture()
    {
        string storeDirectory = NewTempDirectory();
        string sourcePath = Path.Combine(NewTempDirectory(), "from-ttc.json");
        File.Copy(Fixture("session-v1-completed.json"), sourcePath);

        SessionTransferService service = ServiceFor(storeDirectory, LiveDocument, out _);
        ImportResult result = service.Import(sourcePath, storeDirectory);

        Assert.True(result.Imported);

        // a v1 file imports and is persisted as v2, with isDeleted explicit
        SessionDocument imported = TtcJsonSerializer.Read(File.ReadAllText(result.DestinationPath!));
        Assert.Equal(SessionDocument.CurrentSchemaVersion, imported.SchemaVersion);
        Assert.All(imported.ToSessionState().Entries, entry => Assert.False(entry.IsDeleted));
    }

    // ------------------------------------------------------------------ export

    [Fact]
    public void Export_flushes_first_so_the_copy_matches_what_a_restart_would_read_back()
    {
        string storeDirectory = NewTempDirectory();
        SessionTransferService service = ServiceFor(storeDirectory, LiveDocument, out SessionFlushCoordinator coordinator);

        ExportResult result = service.Export(NewTempDirectory());

        Assert.True(result.Exported);
        Assert.True(File.Exists(coordinator.Path), "exporting must write the live session, not just read memory");
        Assert.False(coordinator.IsDirty);
        Assert.Equal(File.ReadAllText(coordinator.Path), File.ReadAllText(result.DestinationPath!));
    }

    [Fact]
    public void Export_does_not_stop_the_active_entry_or_end_the_session()
    {
        string storeDirectory = NewTempDirectory();
        SessionTransferService service = ServiceFor(storeDirectory, LiveDocument, out _);

        ExportResult result = service.Export(NewTempDirectory());
        SessionDocument exported = TtcJsonSerializer.Read(File.ReadAllText(result.DestinationPath!));

        TimeEntry open = Assert.Single(exported.ToSessionState().Entries);

        Assert.False(open.IsComplete, "exporting must not complete a running entry");
        Assert.Null(open.EndTime);
        Assert.Null(exported.EndedAt);          // and must not stamp the session as ended
    }

    [Fact]
    public void Export_never_overwrites_an_existing_destination_file()
    {
        string destination = NewTempDirectory();
        string storeDirectory = NewTempDirectory();
        SessionTransferService service = ServiceFor(storeDirectory, LiveDocument, out _);

        ExportResult first = service.Export(destination);
        string firstBytes = File.ReadAllText(first.DestinationPath!);

        ExportResult second = service.Export(destination);

        Assert.NotEqual(first.DestinationPath, second.DestinationPath);
        Assert.EndsWith("-1.json", second.DestinationPath!, StringComparison.Ordinal);

        // the earlier export is still exactly what it was
        Assert.Equal(firstBytes, File.ReadAllText(first.DestinationPath!));
    }

    [Fact]
    public void Export_leaves_the_source_session_readable_and_unchanged_in_content()
    {
        string storeDirectory = NewTempDirectory();
        SessionTransferService service = ServiceFor(storeDirectory, LiveDocument, out SessionFlushCoordinator coordinator);

        // the first export is what writes the live session, so there is a source file to compare against
        service.Export(NewTempDirectory());

        SessionDocument before = TtcJsonSerializer.Read(File.ReadAllText(coordinator.Path));
        ExportResult result = service.Export(NewTempDirectory());

        SessionDocument after = TtcJsonSerializer.Read(File.ReadAllText(coordinator.Path));

        Assert.Equal(before.SessionId, after.SessionId);
        Assert.Equal(before.ToSessionState().Entries, after.ToSessionState().Entries);

        // source and target are separate files with the same content
        Assert.NotEqual(coordinator.Path, result.DestinationPath);
        Assert.Equal(File.ReadAllText(coordinator.Path), File.ReadAllText(result.DestinationPath!));
    }

    [Fact]
    public void Export_reports_a_failed_flush_instead_of_exporting_stale_data()
    {
        FailingSessionStore store = new();
        SessionFlushCoordinator coordinator = new(store, Path.Combine(NewTempDirectory(), "live.json"), LiveDocument, new object());
        SessionTransferService service = new(store, coordinator);

        ExportResult result = service.Export(NewTempDirectory());

        Assert.False(result.Exported);
        Assert.Equal(ExportOutcome.NotSaved, result.Outcome);
        Assert.Null(result.DestinationPath);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    // ------------------------------------------------------------------ helpers

    private static string Fixture(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", fileName);

    /// <summary>A store that fails every write, standing in for a full disk.</summary>
    private sealed class FailingSessionStore : ISessionStore
    {
        public SessionDocument Read(string path) => throw new IOException("the disk is full");

        public void Write(string path, SessionDocument document) => throw new IOException("the disk is full");
    }
}
