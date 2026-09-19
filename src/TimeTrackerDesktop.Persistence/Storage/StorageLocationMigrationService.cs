using System.Text.Json;
using TimeTrackerDesktop.Domain;

namespace TimeTrackerDesktop.Persistence;

/// <summary>
/// What the user chose to do with the sessions already on disk when they changed the storage folder.
///
/// The choice is explicit because the two options mean very different things: one copies files, the other
/// only records a preference. Silently picking either would be the app doing a bulk file operation on the
/// user's behalf, or losing sight of their sessions.
/// </summary>
public enum StorageMigrationChoice
{
    /// <summary>Copy every session to the new folder, verify it, then remove the original.</summary>
    MoveExistingSessions,

    /// <summary>Record the new folder and leave the existing sessions where they are.</summary>
    UseNewLocationWithoutMovingExistingSessions,
}

/// <summary>What a storage-location change actually did.</summary>
public enum StorageMigrationOutcome
{
    /// <summary>Every candidate moved, or there was nothing to move.</summary>
    Applied,

    /// <summary>Some sessions moved and some did not; the stored location was left alone.</summary>
    PartiallyMoved,
}

/// <summary>The result of a storage-location change.</summary>
public sealed record StorageMigrationResult(
    StorageMigrationOutcome Outcome,
    UserPreferences Preferences,
    int MovedCount,
    IReadOnlyList<string> Failures)
{
    /// <summary>
    /// True only when this operation moved sessions. "Use new location" applies a preference and claims
    /// nothing about files, which is the whole reason the two choices are separate.
    /// </summary>
    public bool ClaimsSessionsMoved => Outcome is StorageMigrationOutcome.Applied && MovedCount > 0;

    /// <summary>True when the move can be retried to finish the job.</summary>
    public bool Retryable => Outcome is StorageMigrationOutcome.PartiallyMoved;

    /// <summary>A short explanation suitable for a status line.</summary>
    public string Reason => Outcome switch
    {
        StorageMigrationOutcome.Applied when MovedCount > 0 => $"Moved {MovedCount} session(s) to {Preferences.StorageFolder}.",
        StorageMigrationOutcome.Applied => $"Storage location set to {Preferences.StorageFolder}.",
        StorageMigrationOutcome.PartiallyMoved =>
            $"Moved {MovedCount} session(s); {Failures.Count} could not be moved and are still in the old folder. "
            + "Nothing was deleted. Try again, or keep using the old location.",
        _ => "Unknown outcome.",
    };
}

/// <summary>
/// Relocates sessions when the user changes the storage folder (plan Task 2.4).
///
/// This is the only code in the app that deletes user data, so the order is fixed and non-negotiable: read
/// the source, write the copy, <b>verify the copy</b>, and only then remove the source. A source whose copy
/// could not be written or could not be read back stays exactly where it was.
///
/// A partial failure deliberately does <b>not</b> switch the stored location. Pointing the app at a folder
/// that holds only some of the sessions would look to the user exactly like the rest were deleted.
/// </summary>
public sealed class StorageLocationMigrationService
{
    private readonly ISessionStore _store;

    public StorageLocationMigrationService(ISessionStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <summary>
    /// Applies <paramref name="newFolderPath"/> to <paramref name="preferences"/> according to
    /// <paramref name="choice"/>.
    ///
    /// <paramref name="preferences"/> must carry the <b>current</b> storage folder in
    /// <see cref="UserPreferences.StorageFolder"/> — the location being moved away from. A blank one means
    /// there is nothing to move.
    /// </summary>
    public StorageMigrationResult Apply(
        UserPreferences preferences,
        string newFolderPath,
        StorageMigrationChoice choice)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentException.ThrowIfNullOrWhiteSpace(newFolderPath);

        if(choice is StorageMigrationChoice.UseNewLocationWithoutMovingExistingSessions)
        {
            // nothing is copied, moved, created or deleted - only the preference changes
            return new StorageMigrationResult(
                StorageMigrationOutcome.Applied,
                preferences with { StorageFolder = newFolderPath },
                MovedCount: 0,
                Failures: []);
        }

        string sourceFolder = preferences.StorageFolder ?? string.Empty;

        List<string> failures = [];
        int moved = 0;

        if(!string.IsNullOrWhiteSpace(sourceFolder) && Directory.Exists(sourceFolder))
        {
            Directory.CreateDirectory(newFolderPath);

            foreach(string sourcePath in Directory.EnumerateFiles(sourceFolder, "*.json", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                try
                {
                    MoveOne(sourcePath, newFolderPath);
                    moved++;
                }
                catch(Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
                {
                    // the source is still there, so this is a report rather than a loss
                    failures.Add($"{Path.GetFileName(sourcePath)}: {exception.Message}");
                }
            }
        }

        if(failures.Count > 0)
        {
            // keep the old location: switching now would hide the sessions that did not move
            return new StorageMigrationResult(StorageMigrationOutcome.PartiallyMoved, preferences, moved, failures);
        }

        return new StorageMigrationResult(
            StorageMigrationOutcome.Applied,
            preferences with { StorageFolder = newFolderPath },
            moved,
            failures);
    }

    /// <summary>
    /// Moves one session, removing the source only after its copy has been written and read back.
    /// </summary>
    private void MoveOne(string sourcePath, string destinationFolder)
    {
        // read and validate the source before anything is written anywhere
        SessionDocument source = _store.Read(sourcePath);

        // never overwrite: the destination may already hold a different session with the same name
        string destinationPath = SessionFileNaming.NextAvailablePath(destinationFolder, source.Name);

        _store.Write(destinationPath, source);

        // verify before removing. A copy that cannot be read back is not a copy.
        SessionDocument copy = _store.Read(destinationPath);

        if(!SameContent(source, copy))
        {
            throw new InvalidDataException($"the copy written to {Path.GetFileName(destinationPath)} does not match the original");
        }

        File.Delete(sourcePath);
    }

    /// <summary>
    /// Whether a copy says the same thing as its source. Compares the session's identity and its entries, not
    /// raw bytes: re-serializing may legitimately differ in line endings, and treating that as a failed move
    /// would leave the user's data split across two folders for no reason.
    /// </summary>
    private static bool SameContent(SessionDocument first, SessionDocument second) =>
        first.SessionId == second.SessionId
        && first.Name == second.Name
        && first.StartedAt == second.StartedAt
        && first.EndedAt == second.EndedAt
        && first.ToSessionState().Entries.SequenceEqual(second.ToSessionState().Entries);
}
