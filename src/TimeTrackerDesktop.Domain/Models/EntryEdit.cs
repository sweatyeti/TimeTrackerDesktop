namespace TimeTrackerDesktop.Domain;

/// <summary>
/// A request to change an entry's editable fields. A <c>null</c> field is left alone, so a caller says
/// what it wants changed rather than restating the whole entry — which is what lets a description-only
/// edit leave the task untouched.
///
/// This mirrors TTC's <c>ApplyEntryUpdate(entryId, logged, task, description)</c>, which applies every
/// field it is given in one atomic block. One difference is deliberate: in TTC a null task means
/// "write an empty string", because its prompts always supply a value, whereas here null means "leave
/// unchanged" and an empty string means "clear it" (which for a task maps to <c>none</c>).
/// </summary>
public sealed record EntryEdit(string? Task = null, string? Description = null, bool? Logged = null)
{
    /// <summary>True when the request asks for nothing, so there is nothing to write.</summary>
    public bool IsEmpty => Task is null && Description is null && Logged is null;
}

/// <summary>What an edit request actually did.</summary>
public enum EntryEditOutcome
{
    /// <summary>The entry was changed.</summary>
    Applied,

    /// <summary>The request is valid but the entry already holds those values. Not an error.</summary>
    Unchanged,

    /// <summary>No entry with that id exists in this session.</summary>
    EntryNotFound,

    /// <summary>Nothing is running, so there is no active entry to edit.</summary>
    NoActiveEntry,

    /// <summary>The entry is soft-deleted. Deleted entries are read-only until restored.</summary>
    EntryDeleted,

    /// <summary>Logged state exists only for completed entries with a real task.</summary>
    LoggedNotApplicable,

    /// <summary>The request named no fields, so there was nothing to apply.</summary>
    NothingToDo,
}

/// <summary>
/// The outcome of an edit: the resulting state, what happened, and the entry it produced.
///
/// The plan asks invalid requests to return "a deliberate domain validation result rather than
/// silently changing state", so every refusal is named here instead of being swallowed. A caller that
/// has to explain to the user why nothing happened gets the reason without re-deriving the rules.
/// </summary>
public sealed record EntryEditResult(SessionState State, EntryEditOutcome Outcome, TimeEntry? Entry = null)
{
    /// <summary>True only when the entry was actually changed.</summary>
    public bool Applied => Outcome is EntryEditOutcome.Applied;

    /// <summary>True when the request was refused, rather than applied or already satisfied.</summary>
    public bool Rejected => Outcome is EntryEditOutcome.EntryNotFound
        or EntryEditOutcome.NoActiveEntry
        or EntryEditOutcome.EntryDeleted
        or EntryEditOutcome.LoggedNotApplicable
        or EntryEditOutcome.NothingToDo;

    /// <summary>A short explanation suitable for a status line, a tooltip or a log.</summary>
    public string Reason => Outcome switch
    {
        EntryEditOutcome.Applied => "Entry updated.",
        EntryEditOutcome.Unchanged => "Entry already had those values.",
        EntryEditOutcome.EntryNotFound => "No entry with that id in this session.",
        EntryEditOutcome.NoActiveEntry => "No entry is running.",
        EntryEditOutcome.EntryDeleted => "Deleted entries are read-only until restored.",
        EntryEditOutcome.LoggedNotApplicable => "Only completed entries with a real task have a logged state.",
        EntryEditOutcome.NothingToDo => "The request named no fields to change.",
        _ => "Unknown outcome.",
    };
}
