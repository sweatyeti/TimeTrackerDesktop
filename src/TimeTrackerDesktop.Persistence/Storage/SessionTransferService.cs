using System.Text;
using TimeTrackerDesktop.Domain;

namespace TimeTrackerDesktop.Persistence;

/// <summary>What an import did.</summary>
public enum ImportOutcome
{
    /// <summary>The file was copied into storage.</summary>
    Imported,

    /// <summary>The file could not be read as a session this build can open.</summary>
    SourceUnreadable,
}

/// <summary>The result of an import.</summary>
public sealed record ImportResult(ImportOutcome Outcome, string? DestinationPath = null, string? FailureReason = null)
{
    /// <summary>True when the copy was written.</summary>
    public bool Imported => Outcome is ImportOutcome.Imported;

    /// <summary>A short explanation suitable for a status line.</summary>
    public string Reason => Imported
        ? $"Imported to {Path.GetFileName(DestinationPath)}."
        : FailureReason ?? "The file could not be imported.";
}

/// <summary>What an export did.</summary>
public enum ExportOutcome
{
    /// <summary>The session was written to the destination.</summary>
    Exported,

    /// <summary>
    /// The live session could not be saved, so the export was abandoned rather than copying stale data.
    /// </summary>
    NotSaved,
}

/// <summary>The result of an export.</summary>
public sealed record ExportResult(ExportOutcome Outcome, string? DestinationPath = null, string? FailureReason = null)
{
    /// <summary>True when the copy was written.</summary>
    public bool Exported => Outcome is ExportOutcome.Exported;

    /// <summary>A short explanation suitable for a status line.</summary>
    public string Reason => Exported
        ? $"Exported to {Path.GetFileName(DestinationPath)}."
        : FailureReason ?? "The session could not be exported.";
}

/// <summary>
/// Copy-based file interchange (plan Task 2.3). Every operation reads one file and writes one file; nothing
/// here deletes, moves or rewrites the source.
///
/// Two properties are the whole point:
///
/// - <b>Import never touches the source.</b> It is read, copied, and only the copy is renamed — so importing
///   cannot damage the file a user picked, even if they picked it twice.
/// - <b>Export is a copy of the persisted session, and never overwrites.</b> It flushes first so the copy
///   matches what a restart would read back, then writes beside any existing file rather than over it.
/// </summary>
public sealed class SessionTransferService
{
    /// <summary>Appended to the name of an imported copy, so the original and the copy stay distinguishable.</summary>
    public const string ImportedSuffix = " (Imported)";

    /// <summary>UTF-8 without a BOM, matching what the store writes.</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ISessionStore _store;
    private readonly SessionFlushCoordinator _coordinator;

    public SessionTransferService(ISessionStore store, SessionFlushCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(coordinator);

        _store = store;
        _coordinator = coordinator;
    }

    /// <summary>
    /// Imports <b>one</b> selected JSON file into <paramref name="destinationDirectory"/>.
    ///
    /// The copy keeps the session id — it is the same session in a different place — and gains
    /// <see cref="ImportedSuffix"/> on its name. The source file is only ever read.
    /// </summary>
    public ImportResult Import(string sourcePath, string destinationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        string json;

        try
        {
            json = File.ReadAllText(sourcePath, Utf8NoBom);
        }
        catch(Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ImportResult(ImportOutcome.SourceUnreadable, FailureReason: $"The file could not be read: {exception.Message}");
        }

        if(!TtcJsonSerializer.TryRead(json, out SessionDocument? source, out string? failure))
        {
            return new ImportResult(ImportOutcome.SourceUnreadable, FailureReason: failure);
        }

        SessionDocument copy = source! with { Name = ImportedName(source!.Name) };

        string destinationPath = SessionFileNaming.NextAvailablePath(destinationDirectory, copy.Name);

        _store.Write(destinationPath, copy);

        return new ImportResult(ImportOutcome.Imported, destinationPath);
    }

    /// <summary>
    /// Exports the live session to <paramref name="destinationDirectory"/>, next to any file already there.
    ///
    /// The session is flushed first, so the copy is the persisted session rather than whatever memory happens
    /// to hold; if that flush fails the export is abandoned instead of copying stale data. Exporting is not a
    /// way of ending the day: it does not stop a running entry and does not stamp the session as ended.
    /// </summary>
    public ExportResult Export(string destinationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        // force the session onto disk, so what we copy is exactly what a restart would read back
        _coordinator.MarkDirty();

        FlushResult flush = _coordinator.Flush();

        if(flush.Failed)
        {
            return new ExportResult(ExportOutcome.NotSaved, FailureReason: flush.Reason);
        }

        // read the file rather than a live object: that is what makes "source and target are independent"
        // true by construction, and it keeps the export from touching the running entry
        SessionDocument document = _store.Read(_coordinator.Path);

        string destinationPath = SessionFileNaming.NextAvailablePath(destinationDirectory, document.Name);

        _store.Write(destinationPath, document);

        return new ExportResult(ExportOutcome.Exported, destinationPath);
    }

    /// <summary>
    /// The copied session's name. A file whose name did not survive gets the same readable fallback the rest
    /// of the app uses, so the copy is never nameless.
    /// </summary>
    private static string ImportedName(string? name) =>
        (string.IsNullOrWhiteSpace(name) ? SessionListProjection.UnnamedFallback : name) + ImportedSuffix;
}
