namespace TimeTrackerDesktop.Persistence;

/// <summary>What a flush attempt did.</summary>
public enum FlushOutcome
{
    /// <summary>The session was written.</summary>
    Written,

    /// <summary>There was nothing to write.</summary>
    NotDirty,

    /// <summary>The write failed. The session is still dirty and the error is kept.</summary>
    Failed,
}

/// <summary>The result of one flush attempt.</summary>
public sealed record FlushResult(FlushOutcome Outcome, Exception? Error = null)
{
    /// <summary>True when the session on disk matches the session in memory — or when there was nothing to do.</summary>
    public bool Succeeded => Outcome is FlushOutcome.Written or FlushOutcome.NotDirty;

    /// <summary>True when the write failed and the change is still only in memory.</summary>
    public bool Failed => Outcome is FlushOutcome.Failed;

    /// <summary>A short explanation suitable for a status bar.</summary>
    public string Reason => Outcome switch
    {
        FlushOutcome.Written => "Session saved.",
        FlushOutcome.NotDirty => "Nothing to save.",
        FlushOutcome.Failed => $"The session could not be saved: {Error?.Message ?? "unknown error"}",
        _ => "Unknown outcome.",
    };
}

/// <summary>
/// Keeps a session file roughly in step with the live session (plan Task 2.2): a mutation marks the session
/// dirty, a periodic loop writes it about every five seconds, and shutdown forces one last write.
///
/// Two rules it exists to enforce:
///
/// - <b>The snapshot is taken under the caller's boundary; the write happens outside it.</b> Holding a lock
///   across a disk write would stall every mutation the UI makes, so <c>syncRoot</c> is entered only long
///   enough to read the current state.
/// - <b>A failure is never reported as success.</b> The dirty flag survives a failed write, the error is
///   kept in <see cref="LastFailure"/> for a UI to show, and the next flush retries. The file is allowed to
///   lag the screen; it is never allowed to claim it has data it does not have.
/// </summary>
public sealed class SessionFlushCoordinator
{
    /// <summary>How often a dirty session is written. Roughly TTC's own five-second loop.</summary>
    public static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromSeconds(5);

    private readonly ISessionStore _store;
    private readonly Func<SessionDocument> _snapshot;
    private readonly object _syncRoot;
    private readonly Lock _stateLock = new();

    private bool _dirty;
    private FlushResult? _lastFailure;

    /// <param name="store">Where the session is written.</param>
    /// <param name="path">The file to write.</param>
    /// <param name="snapshot">Reads the current session. Called under <paramref name="syncRoot"/>.</param>
    /// <param name="syncRoot">
    /// The caller's synchronization boundary — the same object its mutations are made under. The snapshot
    /// is taken inside it so a flush cannot capture a half-applied change, and the write happens outside it
    /// so a flush cannot block the UI.
    /// </param>
    /// <param name="flushInterval">Defaults to <see cref="DefaultFlushInterval"/>; tests shorten it.</param>
    public SessionFlushCoordinator(
        ISessionStore store,
        string path,
        Func<SessionDocument> snapshot,
        object syncRoot,
        TimeSpan? flushInterval = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(syncRoot);

        _store = store;
        _snapshot = snapshot;
        _syncRoot = syncRoot;

        Path = path;
        FlushInterval = flushInterval ?? DefaultFlushInterval;
    }

    /// <summary>The file this coordinator writes.</summary>
    public string Path { get; }

    /// <summary>How long the periodic loop waits between writes.</summary>
    public TimeSpan FlushInterval { get; }

    /// <summary>True while the in-memory session holds changes the file does not.</summary>
    public bool IsDirty
    {
        get
        {
            lock(_stateLock)
            {
                return _dirty;
            }
        }
    }

    /// <summary>
    /// The last failed flush, or <c>null</c> when the file is current. Kept until a write succeeds, so a UI
    /// can show a durable "not saved" state rather than a message that scrolls away.
    /// </summary>
    public FlushResult? LastFailure
    {
        get
        {
            lock(_stateLock)
            {
                return _lastFailure;
            }
        }
    }

    /// <summary>Marks the session dirty, so the next flush — or the periodic loop — writes it.</summary>
    public void MarkDirty()
    {
        lock(_stateLock)
        {
            _dirty = true;
        }
    }

    /// <summary>Writes now if the session is dirty. Safe to call at any time, from any thread.</summary>
    public FlushResult Flush()
    {
        lock(_stateLock)
        {
            if(!_dirty)
            {
                return new FlushResult(FlushOutcome.NotDirty);
            }
        }

        SessionDocument document;

        // the caller's boundary is held only for the read
        lock(_syncRoot)
        {
            document = _snapshot();
        }

        try
        {
            _store.Write(Path, document);
        }
        catch(Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            FlushResult failure = new(FlushOutcome.Failed, exception);

            lock(_stateLock)
            {
                // still dirty: nothing was written, and clearing the flag would lose the change silently
                _dirty = true;
                _lastFailure = failure;
            }

            return failure;
        }

        lock(_stateLock)
        {
            _dirty = false;
            _lastFailure = null;
        }

        return new FlushResult(FlushOutcome.Written);
    }

    /// <summary>
    /// Runs the periodic flush until <paramref name="cancellationToken"/> is cancelled, then performs one
    /// final flush. The final flush is what makes a graceful shutdown durable: without it, the last few
    /// seconds of tracked work would live only in memory.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while(true)
            {
                await Task.Delay(FlushInterval, cancellationToken).ConfigureAwait(false);
                Flush();
            }
        }
        catch(OperationCanceledException)
        {
            // expected: the caller is shutting down
        }

        Flush();
    }
}
