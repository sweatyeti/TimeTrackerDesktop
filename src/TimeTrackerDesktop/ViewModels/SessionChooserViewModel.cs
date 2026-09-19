using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using TimeTrackerDesktop.Domain;
using TimeTrackerDesktop.Persistence;

namespace TimeTrackerDesktop.ViewModels;

/// <summary>
/// One row of the session chooser: the display projection plus the session it came from.
///
/// <see cref="SessionListItem"/> deliberately carries no id or path, so the pairing happens here rather than
/// by looking the session up again — a row and the session it resumes cannot drift apart.
/// </summary>
public sealed record SessionChoice(SessionListItem Row, SessionState State)
{
    public string Name => Row.Name;

    public DateTimeOffset StartedAt => Row.StartedAt;

    /// <summary>TTC's own format for a chooser row, kept so the two apps read the same.</summary>
    public string StartedDisplay => Row.StartedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// The row's state in words. A running session says <c>Active</c>, which is the case a user most needs
    /// to recognise — and it is text, not just a colour, so it survives a colour-blind reader.
    /// </summary>
    public string StateLabel => Row.IsActive ? "Active" : Row.IsUnfinished ? "Unfinished" : "Finished";

    public TimeSpan Tracked => Row.Tracked;

    /// <summary>Tracked time, rounded up to the minute the way the summary rounds.</summary>
    public string TrackedDisplay => FormatTracked(Row.Tracked);

    /// <summary>The single line a compact row can show.</summary>
    public string Description => $"{StartedDisplay} · {StateLabel} · {TrackedDisplay}";

    /// <summary>Tracked time, rounded up to the minute the way the summary rounds.</summary>
    public static string FormatTracked(TimeSpan tracked) => TimeDisplay.Format(tracked);
}

/// <summary>
/// How a task reads on screen (plan Task 4.1: a new session starts with a "blank visible task field").
///
/// TTC stores <c>none</c> for "no task", which is a storage word rather than something to show a user. The
/// mapping lives here so the chooser and the widget agree, and so the rule is testable without a UI.
/// </summary>
public static class TaskDisplay
{
    /// <summary>TTC's stored marker for "no task".</summary>
    public const string NoTaskMarker = "none";

    /// <summary>The text to show for a task: empty when there is no task, otherwise the task itself.</summary>
    public static string Visible(string? task) =>
        string.IsNullOrWhiteSpace(task) || string.Equals(task.Trim(), NoTaskMarker, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : task.Trim();
}

/// <summary>
/// The session chooser (plan Task 4.1). Lists what is on disk, newest first, and turns a choice into a live
/// <see cref="SessionService"/>.
///
/// The plan's requirement is that a session is chosen <b>before</b> the widget opens, so this is the app's
/// entry state rather than a dialog: <see cref="StartNewSession"/> is the primary action and needs no
/// arguments, because there is no naming prompt — the name is generated (decision 2, invariant culture).
///
/// It reads the storage folder itself and skips a session it cannot read rather than refusing to open the
/// chooser, because one damaged file must not cost the user access to every other session.
/// </summary>
public sealed class SessionChooserViewModel : INotifyPropertyChanged
{
    private readonly ISessionStore _store;
    private readonly string _storageFolder;
    private readonly IClock _clock;

    private SessionChoice? _selectedChoice;
    private string _message = string.Empty;

    public SessionChooserViewModel(ISessionStore store, string storageFolder, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(storageFolder);
        ArgumentNullException.ThrowIfNull(clock);

        _store = store;
        _storageFolder = storageFolder;
        _clock = clock;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The rows, newest first.</summary>
    public ObservableCollection<SessionChoice> Choices { get; } = [];

    /// <summary>The highlighted row, if any.</summary>
    public SessionChoice? SelectedChoice
    {
        get => _selectedChoice;
        set
        {
            if(_selectedChoice == value)
            {
                return;
            }

            _selectedChoice = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanResume));
        }
    }

    /// <summary>True when a row is selected, so Resume is available.</summary>
    public bool CanResume => _selectedChoice is not null;

    /// <summary>True when there is anything to resume at all.</summary>
    public bool HasSessions => Choices.Count > 0;

    /// <summary>
    /// A refusal or a note, empty when there is nothing to say. A corrupt session is reported here instead of
    /// being repaired or hidden: the plan's rule is that a session with more than one unfinished entry is
    /// refused, and a silent no-op would look like a broken button.
    /// </summary>
    public string Message
    {
        get => _message;
        private set
        {
            if(_message == value)
            {
                return;
            }

            _message = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Re-reads the storage folder. Cheap enough to call on every show.</summary>
    public void Refresh()
    {
        List<SessionState> sessions = [];
        List<string> unreadable = [];

        if(Directory.Exists(_storageFolder))
        {
            foreach(string path in Directory.EnumerateFiles(_storageFolder, "*.json", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                try
                {
                    sessions.Add(_store.Read(path).ToSessionState());
                }
                catch(Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                {
                    unreadable.Add(Path.GetFileName(path));
                }
            }
        }

        Choices.Clear();

        foreach(SessionState session in SessionListProjection.Ordered(sessions))
        {
            Choices.Add(new SessionChoice(SessionListProjection.From(session), session));
        }

        SelectedChoice = null;
        OnPropertyChanged(nameof(HasSessions));

        Message = unreadable.Count > 0
            ? $"{unreadable.Count} session file(s) could not be read and are not listed: {string.Join(", ", unreadable)}"
            : string.Empty;
    }

    /// <summary>
    /// Starts a new session. No arguments, and no name is asked for: the session starts timing immediately
    /// with an open entry whose task is unset, which the widget shows as a blank task field.
    /// </summary>
    public SessionService StartNewSession() => SessionService.StartNewSession(_clock);

    /// <summary>
    /// Resumes the selected session, or reports why it cannot be resumed and returns <c>null</c>.
    /// </summary>
    public SessionService? ResumeSelected()
    {
        if(_selectedChoice is null)
        {
            Message = "Select a session first.";
            return null;
        }

        SessionResumeResult result = SessionResumeService.Resume(_selectedChoice.State, _clock);

        if(!result.Resumed || result.Session is null)
        {
            Message = result.Reason;
            return null;
        }

        Message = string.Empty;

        return result.Session;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
