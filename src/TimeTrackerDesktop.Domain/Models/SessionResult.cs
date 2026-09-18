namespace TimeTrackerDesktop.Domain;

/// <summary>
/// What an operation actually did. Returned with the resulting state so a view model can update its
/// status cues — tray icon, ACTIVE/NOT ACTIVE banner, tooltip — without diffing two states to work
/// out what happened.
/// </summary>
public enum SessionChange
{
    /// <summary>Nothing moved: the operation was a no-op (already idle, or already ended).</summary>
    None,

    /// <summary>Idle to running: a new entry was opened.</summary>
    EntryStarted,

    /// <summary>Running to running: the previous entry was completed and a new one opened.</summary>
    EntryRestarted,

    /// <summary>Running to idle: the open entry was completed. The session stays open.</summary>
    EntryStopped,

    /// <summary>The session was ended. Any open work was completed first.</summary>
    SessionEnded,
}

/// <summary>
/// The immutable outcome of a session operation: the state after it, what changed, and the entry it
/// touched.
///
/// <see cref="AffectedEntry"/> is the entry the operation acted on — the entry that was just opened,
/// completed or restarted — which is what a status cue or a focus decision needs. It is <c>null</c>
/// for a no-op, because nothing was touched.
/// </summary>
public sealed record SessionResult(SessionState State, SessionChange Change, TimeEntry? AffectedEntry = null)
{
    /// <summary>True when the operation changed something. A no-op reports <see cref="SessionChange.None"/>.</summary>
    public bool Changed => Change is not SessionChange.None;

    /// <summary>True while an entry is running, i.e. the widget shows the active state.</summary>
    public bool IsActive => State.IsActive;

    /// <summary>True once the session has been ended.</summary>
    public bool IsEnded => State.EndedAt is not null;

    /// <summary>The running entry, or <c>null</c> when idle. The status cue's task text comes from here.</summary>
    public TimeEntry? ActiveEntry => State.ActiveEntry;
}