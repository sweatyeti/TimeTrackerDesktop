namespace TimeTrackerDesktop.Persistence;

/// <summary>
/// A file-backed store that cannot leave a half-written session behind (plan Task 2.2).
///
/// Every write goes to <c>&lt;name&gt;.json.tmp</c> in the <b>same directory</b>, is flushed all the way to
/// disk, and is then moved onto the final name. A crash therefore leaves either the previous file or the
/// complete new one. The one outcome that is never acceptable is a session file that parses to half a
/// session, because that is the failure a user cannot see and cannot undo.
/// </summary>
public sealed class AtomicSessionStore : ISessionStore
{
    private readonly string _directoryPath;

    public AtomicSessionStore(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        _directoryPath = directoryPath;
    }

    /// <summary>
    /// Where sessions live by default: the per-user local application-data folder.
    ///
    /// Not the working directory, and not the executable's directory. TTC writes <c>entries/</c> relative
    /// to the process working directory, so the same session turns up in a different place depending on
    /// where the app was launched from — an unpackaged WinUI app can be started from anywhere, and its
    /// sessions must not follow the shell around.
    /// </summary>
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TimeTrackerDesktop",
        "entries");

    /// <summary>The directory this store writes into.</summary>
    public string DirectoryPath => _directoryPath;

    /// <summary>
    /// The temporary path used while writing <paramref name="path"/>. Same directory by construction: a
    /// temporary file on another volume cannot be moved into place atomically, and the move would silently
    /// become a copy.
    /// </summary>
    public static string TemporaryPathFor(string path) => AtomicFile.TemporaryPathFor(path);

    public SessionDocument Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return TtcJsonSerializer.Read(AtomicFile.Read(path));
    }

    public void Write(string path, SessionDocument document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(document);

        System.IO.Directory.CreateDirectory(_directoryPath);

        // serialize BEFORE touching the disk, then let AtomicFile do the temp-write-flush-move: one
        // implementation of the atomicity rule, shared with preferences
        AtomicFile.Write(path, TtcJsonSerializer.Write(document));
    }

    /// <summary>
    /// Deletes leftover <c>*.json.tmp</c> files, best-effort, and reports how many went.
    ///
    /// Temporary files only. Cleanup exists so a crash cannot accumulate litter — never so it can remove
    /// something a user might still want.
    /// </summary>
    public int CleanStaleTemporaryFiles()
    {
        if(!System.IO.Directory.Exists(_directoryPath))
        {
            return 0;
        }

        int removed = 0;

        foreach(string file in System.IO.Directory.EnumerateFiles(_directoryPath, "*.json.tmp", SearchOption.TopDirectoryOnly))
        {
            try
            {
                File.Delete(file);
                removed++;
            }
            catch(IOException)
            {
                // another instance may be mid-write; leaving it is the correct outcome
            }
            catch(UnauthorizedAccessException)
            {
                // ditto: a locked or read-only file is not worth failing startup over
            }
        }

        return removed;
    }
}
