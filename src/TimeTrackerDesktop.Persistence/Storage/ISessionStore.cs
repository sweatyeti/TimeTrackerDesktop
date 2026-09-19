namespace TimeTrackerDesktop.Persistence;

/// <summary>
/// Where a session lives between runs.
///
/// Deliberately tiny. The coordinator owns <i>when</i> to write and the JSON layer owns <i>what</i> a file
/// looks like, so a store only has to move bytes without ever leaving a half-written file behind.
/// </summary>
public interface ISessionStore
{
    /// <summary>Reads a session file, throwing when it cannot be used.</summary>
    SessionDocument Read(string path);

    /// <summary>
    /// Writes a session file atomically: a reader sees either the previous contents or the complete new
    /// ones, never a partial file.
    /// </summary>
    void Write(string path, SessionDocument document);
}
