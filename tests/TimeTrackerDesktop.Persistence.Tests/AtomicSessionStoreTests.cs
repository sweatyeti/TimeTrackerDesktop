using System.Globalization;
using TimeTrackerDesktop.Domain;

namespace TimeTrackerDesktop.Persistence.Tests;

/// <summary>
/// Executable form of plan Task 2.2: per-user storage with atomic writes and a periodic flush.
///
/// Two properties are worth more than the rest, and both are asserted directly:
///
/// 1. <b>A reader never sees a partial file.</b> The write goes to a same-directory temporary file, is
///    flushed to disk, and is then moved onto the final name — so a crash leaves either the old file or
///    the complete new one.
/// 2. <b>A failed write is never reported as success.</b> The dirty flag stays set, the failure is kept
///    as durable state a UI can show, and the next flush retries.
///
/// The synchronization test matters for a subtler reason: snapshotting under the caller's boundary but
/// writing <b>outside</b> it is the difference between a flush that briefly pauses tracking and one that
/// blocks every mutation for the duration of a disk write.
/// </summary>
public sealed class AtomicSessionStoreTests
{
    private static readonly DateTimeOffset Noon =
        DateTimeOffset.Parse("2026-09-18T12:00:00-05:00", CultureInfo.InvariantCulture);

    private static string NewTempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ttd-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static SessionDocument Document(string name = "fixture") =>
        SessionDocument.FromSessionState(SessionState.New(name, Noon));

    private static string PathIn(string directory) => Path.Combine(directory, "session.json");

    // ------------------------------------------------------------------ atomic writes

    [Fact]
    public void An_initial_write_creates_the_file_and_leaves_no_temporary_behind()
    {
        string directory = NewTempDirectory();
        string path = PathIn(directory);

        new AtomicSessionStore(directory).Write(path, Document());

        Assert.True(File.Exists(path));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        Assert.Equal(SessionDocument.CurrentSchemaVersion, TtcJsonSerializer.Read(File.ReadAllText(path)).SchemaVersion);
    }

    [Fact]
    public void The_file_is_utf8_without_a_bom()
    {
        // a BOM would make the file unreadable to TTC
        string path = PathIn(NewTempDirectory());
        new AtomicSessionStore(Path.GetDirectoryName(path)!).Write(path, Document());

        byte[] bytes = File.ReadAllBytes(path);

        Assert.True(bytes.Length > 3);
        Assert.False(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "the file must not start with a UTF-8 BOM");
    }

    [Fact]
    public void The_temporary_file_is_written_beside_the_final_one()
    {
        // a temporary file on another volume cannot be moved into place atomically
        string path = Path.Combine(Path.GetTempPath(), "sessions", "session.json");

        Assert.Equal(path + ".tmp", AtomicSessionStore.TemporaryPathFor(path));
        Assert.Equal(Path.GetDirectoryName(path), Path.GetDirectoryName(AtomicSessionStore.TemporaryPathFor(path)));
    }

    [Fact]
    public void Stale_temporaries_are_cleaned_without_touching_final_data()
    {
        string directory = NewTempDirectory();
        string final = PathIn(directory);

        File.WriteAllText(final, "{}");
        File.WriteAllText(AtomicSessionStore.TemporaryPathFor(final), "half-written");
        File.WriteAllText(Path.Combine(directory, "other.json.tmp"), "half-written");

        int removed = new AtomicSessionStore(directory).CleanStaleTemporaryFiles();

        Assert.Equal(2, removed);
        Assert.True(File.Exists(final), "cleanup must never delete final data");
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void The_store_creates_its_directory_on_demand()
    {
        string parent = NewTempDirectory();
        string directory = Path.Combine(parent, "entries");

        new AtomicSessionStore(directory).Write(PathIn(directory), Document());

        Assert.True(File.Exists(PathIn(directory)));
    }

    // ------------------------------------------------------------------ default location

    [Fact]
    public void Default_storage_is_a_per_user_application_data_folder()
    {
        string directory = AtomicSessionStore.DefaultDirectory;
        string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.False(string.IsNullOrWhiteSpace(directory));
        Assert.StartsWith(localApplicationData, directory, StringComparison.Ordinal);

        // never the working directory or the executable's directory: an unpackaged app can be launched from
        // anywhere, and its sessions must not follow the shell around
        Assert.DoesNotContain(Directory.GetCurrentDirectory(), directory, StringComparison.Ordinal);
        Assert.DoesNotContain(AppContext.BaseDirectory, directory, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the coordinator

    [Fact]
    public void An_explicit_flush_writes_when_dirty_and_does_nothing_when_clean()
    {
        ProbeSessionStore store = new(() => { });
        SessionFlushCoordinator coordinator = new(store, PathIn(NewTempDirectory()), () => Document(), new object());

        Assert.Equal(FlushOutcome.NotDirty, coordinator.Flush().Outcome);
        Assert.Equal(0, store.WriteCount);

        coordinator.MarkDirty();

        Assert.Equal(FlushOutcome.Written, coordinator.Flush().Outcome);
        Assert.Equal(1, store.WriteCount);
        Assert.False(coordinator.IsDirty);
        Assert.Null(coordinator.LastFailure);
    }

    [Fact]
    public void The_snapshot_is_taken_behind_the_boundary_and_the_write_happens_outside_it()
    {
        // the requirement in one test: read under the caller's lock, write without it. Holding the lock
        // across a disk write would block every mutation the UI makes.
        object boundary = new();
        bool boundaryHeldDuringSnapshot = false;
        bool boundaryHeldDuringWrite = false;

        ProbeSessionStore store = new(() => boundaryHeldDuringWrite = Monitor.IsEntered(boundary));

        SessionFlushCoordinator coordinator = new(
            store,
            PathIn(NewTempDirectory()),
            snapshot: () =>
            {
                boundaryHeldDuringSnapshot = Monitor.IsEntered(boundary);
                return Document();
            },
            syncRoot: boundary);

        coordinator.MarkDirty();

        Assert.True(coordinator.Flush().Succeeded);
        Assert.True(boundaryHeldDuringSnapshot, "the snapshot must be taken under the boundary");
        Assert.False(boundaryHeldDuringWrite, "the write must happen outside the boundary");
    }

    [Fact]
    public void A_failed_write_is_reported_keeps_the_dirty_flag_and_retries_successfully()
    {
        FailingSessionStore store = new(failures: 1);
        SessionFlushCoordinator coordinator = new(store, PathIn(NewTempDirectory()), () => Document(), new object());

        coordinator.MarkDirty();
        FlushResult failed = coordinator.Flush();

        Assert.True(failed.Failed);
        Assert.True(coordinator.IsDirty, "a failed flush must not clear the dirty flag");
        Assert.NotNull(coordinator.LastFailure);

        FlushResult retried = coordinator.Flush();

        Assert.True(retried.Succeeded);
        Assert.False(coordinator.IsDirty);
        Assert.Null(coordinator.LastFailure);
        Assert.Equal(2, store.Attempts);
    }

    [Fact]
    public void A_failed_write_leaves_the_previous_final_file_readable()
    {
        string directory = NewTempDirectory();
        string path = PathIn(directory);
        AtomicSessionStore real = new(directory);

        real.Write(path, Document("first"));
        string before = File.ReadAllText(path);

        SessionFlushCoordinator coordinator = new(new FailingSessionStore(failures: 1), path, () => Document("second"), new object());

        coordinator.MarkDirty();

        Assert.True(coordinator.Flush().Failed);
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void A_failed_atomic_write_leaves_the_previous_final_file_intact()
    {
        // the same guarantee one layer down, with the real store: make the move fail by pointing the
        // temporary path at a directory, which cannot be replaced by a file
        string directory = NewTempDirectory();
        string path = PathIn(directory);
        AtomicSessionStore store = new(directory);

        store.Write(path, Document("first"));
        string before = File.ReadAllText(path);

        Directory.CreateDirectory(AtomicSessionStore.TemporaryPathFor(path));

        try
        {
            store.Write(path, Document("second"));
        }
        catch(Exception)
        {
            // which exception is platform-specific (a directory is refused differently on Windows and
            // Linux); the guarantee under test is that the final file is untouched
        }

        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public async Task The_periodic_loop_flushes_a_dirty_session()
    {
        ProbeSessionStore store = new(() => { });
        SessionFlushCoordinator coordinator = new(
            store,
            PathIn(NewTempDirectory()),
            () => Document(),
            new object(),
            TimeSpan.FromMilliseconds(20));

        using CancellationTokenSource cancellation = new();

        coordinator.MarkDirty();
        Task loop = coordinator.RunAsync(cancellation.Token);

        for(int attempt = 0; attempt < 200 && store.WriteCount == 0; attempt++)
        {
            await Task.Delay(10);
        }

        cancellation.Cancel();
        await loop;

        Assert.True(store.WriteCount >= 1, "the periodic loop never flushed");
        Assert.False(coordinator.IsDirty);
    }

    [Fact]
    public async Task Cancelling_the_periodic_loop_performs_a_final_flush()
    {
        ProbeSessionStore store = new(() => { });
        SessionFlushCoordinator coordinator = new(
            store,
            PathIn(NewTempDirectory()),
            () => Document(),
            new object(),
            TimeSpan.FromHours(1));     // long enough that only shutdown can cause a write

        using CancellationTokenSource cancellation = new();

        coordinator.MarkDirty();
        Task loop = coordinator.RunAsync(cancellation.Token);

        cancellation.Cancel();
        await loop;

        Assert.Equal(1, store.WriteCount);
        Assert.False(coordinator.IsDirty);
    }

    [Fact]
    public async Task A_clean_session_is_not_rewritten_at_shutdown()
    {
        ProbeSessionStore store = new(() => { });
        SessionFlushCoordinator coordinator = new(
            store,
            PathIn(NewTempDirectory()),
            () => Document(),
            new object(),
            TimeSpan.FromHours(1));

        using CancellationTokenSource cancellation = new();

        Task loop = coordinator.RunAsync(cancellation.Token);
        cancellation.Cancel();
        await loop;

        Assert.Equal(0, store.WriteCount);
    }

    // ------------------------------------------------------------------ test doubles

    /// <summary>A store that records writes and reports whether the boundary was held during one.</summary>
    private sealed class ProbeSessionStore(Action onWrite) : ISessionStore
    {
        public int WriteCount { get; private set; }

        public SessionDocument? LastWritten { get; private set; }

        public SessionDocument Read(string path) => throw new NotSupportedException();

        public void Write(string path, SessionDocument document)
        {
            WriteCount++;
            LastWritten = document;
            onWrite();
        }
    }

    /// <summary>A store that fails a fixed number of writes before succeeding.</summary>
    private sealed class FailingSessionStore(int failures) : ISessionStore
    {
        private int _remaining = failures;

        public int Attempts { get; private set; }

        public SessionDocument Read(string path) => throw new NotSupportedException();

        public void Write(string path, SessionDocument document)
        {
            Attempts++;

            if(_remaining-- > 0)
            {
                throw new IOException("the disk is full");
            }
        }
    }
}
