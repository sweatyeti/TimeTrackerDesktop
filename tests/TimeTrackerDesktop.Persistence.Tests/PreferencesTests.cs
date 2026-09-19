using System.Globalization;
using System.Text.Json;
using TimeTrackerDesktop.Domain;

namespace TimeTrackerDesktop.Persistence.Tests;

/// <summary>
/// Executable form of plan Task 2.4: persist only what is implemented, and relocate data without risking it.
///
/// The migration rules are the ones with teeth, because they are the only place in this app that deletes
/// user data:
///
/// - <b>A source file is removed only after its copy has been verified readable and equal in content.</b>
///   Anything else risks turning "move my sessions" into "lose my sessions".
/// - <b>Choosing the new location does not claim anything moved.</b> The two choices exist precisely so a
///   user can point the app somewhere else without agreeing to a bulk file operation.
/// - <b>A partial failure leaves untouched sources and reports a retryable result</b>, and does not switch
///   the stored location — pointing the app at a folder that only has half the sessions would look exactly
///   like data loss.
/// </summary>
public sealed class PreferencesTests
{
    private static readonly DateTimeOffset Noon =
        DateTimeOffset.Parse("2026-09-18T12:00:00-05:00", CultureInfo.InvariantCulture);

    private static string NewTempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ttd-prefs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string PathIn(string directory) => Path.Combine(directory, "preferences.json");

    private static string WriteSession(string directory, string name, Guid sessionId)
    {
        SessionState state = SessionState.New(name, Noon) with { SessionId = sessionId };
        string path = Path.Combine(directory, SessionFileNaming.Slugify(name) + ".json");

        new AtomicSessionStore(directory).Write(path, SessionDocument.FromSessionState(state));

        return path;
    }

    // ------------------------------------------------------------------ preferences

    [Fact]
    public void Defaults_are_used_when_no_preferences_file_exists()
    {
        PreferencesStore store = new(PathIn(NewTempDirectory()));

        UserPreferences preferences = store.Read();

        Assert.Equal(UserPreferences.Default, preferences);
        Assert.False(File.Exists(store.FilePath), "reading must not create a file");
    }

    [Fact]
    public void Preferences_round_trip_through_the_store()
    {
        string directory = NewTempDirectory();
        PreferencesStore store = new(PathIn(directory));

        UserPreferences written = UserPreferences.Default with
        {
            Theme = AppTheme.Dark,
            CustomAccentColor = "#FF3B7DD8",
            TimerDisplay = TimerDisplayMode.StartedTime,
            AlwaysOnTop = false,
            WidgetSize = WidgetSizePreset.Expanded,
            WidgetPlacement = new WidgetPlacement(120.5, 340.25, @"\\.\DISPLAY2"),
            StoppedClose = StoppedCloseBehavior.Exit,
            StorageFolder = directory,
        };

        store.Write(written);

        Assert.Equal(written, store.Read());
    }

    [Fact]
    public void A_corrupt_preferences_file_falls_back_to_defaults_rather_than_crashing()
    {
        // preferences are a convenience: a damaged one must never stop the app from starting
        string path = PathIn(NewTempDirectory());
        File.WriteAllText(path, "{ not json");

        Assert.Equal(UserPreferences.Default, new PreferencesStore(path).Read());
    }

    [Fact]
    public void An_unknown_preference_field_is_ignored_rather_than_fatal()
    {
        // a newer build may write fields this one does not know; that is not a reason to reset everything
        string path = PathIn(NewTempDirectory());
        File.WriteAllText(path, """{ "theme": "Dark", "futurePreference": { "nested": true } }""");

        UserPreferences preferences = new PreferencesStore(path).Read();

        Assert.Equal(AppTheme.Dark, preferences.Theme);
        Assert.Equal(UserPreferences.Default.TimerDisplay, preferences.TimerDisplay);
    }

    [Fact]
    public void The_preferences_file_is_written_atomically_and_leaves_no_temporary_behind()
    {
        string directory = NewTempDirectory();
        PreferencesStore store = new(PathIn(directory));

        store.Write(UserPreferences.Default);

        Assert.True(File.Exists(store.FilePath));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void The_default_preferences_path_is_under_the_per_user_application_data_folder()
    {
        string path = PreferencesStore.DefaultFilePath;
        string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.StartsWith(localApplicationData, path, StringComparison.Ordinal);
        Assert.EndsWith(".json", path, StringComparison.Ordinal);
    }

    [Fact]
    public void No_launch_at_login_or_global_shortcut_setting_is_stored_in_v1()
    {
        // the plan excludes both from v1. A canary, so adding one has to be a deliberate act rather than a
        // field that quietly appears.
        string[] banned = ["LaunchAtLogin", "StartWithWindows", "RunAtStartup", "GlobalShortcut", "Hotkey"];

        string[] properties = [.. typeof(UserPreferences).GetProperties().Select(property => property.Name)];

        Assert.Empty(properties.Intersect(banned, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_persisted_field_set_is_exactly_the_documented_one()
    {
        // drift canary: the plan names these eight fields, and nothing else belongs in the file
        string[] expected =
        [
            "theme",
            "customAccentColor",
            "timerDisplay",
            "alwaysOnTop",
            "widgetSize",
            "widgetPlacement",
            "stoppedClose",
            "storageFolder",
        ];

        string json = JsonSerializer.Serialize(
            UserPreferences.Default,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        foreach(string key in expected)
        {
            Assert.Contains($"\"{key}\"", json, StringComparison.Ordinal);
        }

        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(expected.Length, document.RootElement.EnumerateObject().Count());
    }

    // ------------------------------------------------------------------ migration: use new location

    [Fact]
    public void Use_new_location_changes_only_the_preference_and_claims_nothing_moved()
    {
        string oldFolder = NewTempDirectory();
        string newFolder = NewTempDirectory();
        string session = WriteSession(oldFolder, "site visit", Guid.NewGuid());

        StorageLocationMigrationService service = new(new AtomicSessionStore(oldFolder));

        StorageMigrationResult result = service.Apply(
            UserPreferences.Default with { StorageFolder = oldFolder },
            newFolder,
            StorageMigrationChoice.UseNewLocationWithoutMovingExistingSessions);

        Assert.Equal(StorageMigrationOutcome.Applied, result.Outcome);
        Assert.Equal(newFolder, result.Preferences.StorageFolder);
        Assert.Equal(0, result.MovedCount);
        Assert.False(result.ClaimsSessionsMoved);

        // the sessions stay exactly where they were, and the new folder is not even created
        Assert.True(File.Exists(session));
        Assert.Empty(Directory.GetFiles(newFolder, "*.json"));
    }

    // ------------------------------------------------------------------ migration: move existing sessions

    [Fact]
    public void Move_copies_every_session_then_removes_the_verified_source()
    {
        string oldFolder = NewTempDirectory();
        string newFolder = NewTempDirectory();
        Guid firstId = Guid.NewGuid();
        Guid secondId = Guid.NewGuid();

        WriteSession(oldFolder, "site visit", firstId);
        WriteSession(oldFolder, "code review", secondId);

        StorageLocationMigrationService service = new(new AtomicSessionStore(oldFolder));

        StorageMigrationResult result = service.Apply(
            UserPreferences.Default with { StorageFolder = oldFolder },
            newFolder,
            StorageMigrationChoice.MoveExistingSessions);

        Assert.Equal(StorageMigrationOutcome.Applied, result.Outcome);
        Assert.Equal(2, result.MovedCount);
        Assert.True(result.ClaimsSessionsMoved);
        Assert.Equal(newFolder, result.Preferences.StorageFolder);

        // sources are gone, copies are there and readable, and the session identity survived the move
        Assert.Empty(Directory.GetFiles(oldFolder, "*.json"));

        Guid[] movedIds =
        [
            .. Directory.GetFiles(newFolder, "*.json")
                .Select(path => TtcJsonSerializer.Read(File.ReadAllText(path)).SessionId),
        ];

        Assert.Contains(firstId, movedIds);
        Assert.Contains(secondId, movedIds);
    }

    [Fact]
    public void Move_keeps_the_collision_sequence_so_an_existing_destination_is_never_overwritten()
    {
        string oldFolder = NewTempDirectory();
        string newFolder = NewTempDirectory();

        // the destination already holds a different session that happens to want the same file name
        string existing = WriteSession(newFolder, "site visit", Guid.NewGuid());
        string existingBytes = File.ReadAllText(existing);

        WriteSession(oldFolder, "site visit", Guid.NewGuid());

        StorageLocationMigrationService service = new(new AtomicSessionStore(oldFolder));

        StorageMigrationResult result = service.Apply(
            UserPreferences.Default with { StorageFolder = oldFolder },
            newFolder,
            StorageMigrationChoice.MoveExistingSessions);

        Assert.Equal(StorageMigrationOutcome.Applied, result.Outcome);
        Assert.Equal(existingBytes, File.ReadAllText(existing));
        Assert.Equal(2, Directory.GetFiles(newFolder, "*.json").Length);
        Assert.Contains(Directory.GetFiles(newFolder, "*.json"), path => path.EndsWith("-1.json", StringComparison.Ordinal));
    }

    [Fact]
    public void Move_never_removes_a_source_whose_copy_failed_to_write()
    {
        // the rule that matters most: a failed copy must leave the original exactly where it was
        string oldFolder = NewTempDirectory();
        string newFolder = NewTempDirectory();
        string session = WriteSession(oldFolder, "site visit", Guid.NewGuid());

        StorageLocationMigrationService service = new(new FailingSessionStore(failures: 1));

        StorageMigrationResult result = service.Apply(
            UserPreferences.Default with { StorageFolder = oldFolder },
            newFolder,
            StorageMigrationChoice.MoveExistingSessions);

        Assert.Equal(StorageMigrationOutcome.PartiallyMoved, result.Outcome);
        Assert.Equal(0, result.MovedCount);
        Assert.Single(result.Failures);
        Assert.True(File.Exists(session), "a source must never be removed unless its copy was verified");
        Assert.Equal(oldFolder, result.Preferences.StorageFolder);
        Assert.True(result.Retryable);
    }

    [Fact]
    public void A_partial_failure_keeps_the_old_location_and_reports_what_stayed()
    {
        string oldFolder = NewTempDirectory();
        string newFolder = NewTempDirectory();

        WriteSession(oldFolder, "site visit", Guid.NewGuid());
        WriteSession(oldFolder, "code review", Guid.NewGuid());

        // the first copy succeeds, the second fails
        StorageLocationMigrationService service = new(new FailingSessionStore(failures: 1, failAfter: 1));

        StorageMigrationResult result = service.Apply(
            UserPreferences.Default with { StorageFolder = oldFolder },
            newFolder,
            StorageMigrationChoice.MoveExistingSessions);

        Assert.Equal(StorageMigrationOutcome.PartiallyMoved, result.Outcome);
        Assert.Equal(1, result.MovedCount);
        Assert.Single(result.Failures);

        // the app still points at the folder that holds the session which did not move
        Assert.Equal(oldFolder, result.Preferences.StorageFolder);
        Assert.Single(Directory.GetFiles(oldFolder, "*.json"));
    }

    [Fact]
    public void Move_ignores_temporary_files_rather_than_treating_them_as_sessions()
    {
        string oldFolder = NewTempDirectory();
        string newFolder = NewTempDirectory();

        WriteSession(oldFolder, "site visit", Guid.NewGuid());
        File.WriteAllText(Path.Combine(oldFolder, "half-written.json.tmp"), "not a session");

        StorageLocationMigrationService service = new(new AtomicSessionStore(oldFolder));

        StorageMigrationResult result = service.Apply(
            UserPreferences.Default with { StorageFolder = oldFolder },
            newFolder,
            StorageMigrationChoice.MoveExistingSessions);

        Assert.Equal(1, result.MovedCount);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void Move_with_nothing_to_move_still_applies_the_new_location()
    {
        string oldFolder = NewTempDirectory();
        string newFolder = NewTempDirectory();

        StorageLocationMigrationService service = new(new AtomicSessionStore(oldFolder));

        StorageMigrationResult result = service.Apply(
            UserPreferences.Default with { StorageFolder = oldFolder },
            newFolder,
            StorageMigrationChoice.MoveExistingSessions);

        Assert.Equal(StorageMigrationOutcome.Applied, result.Outcome);
        Assert.Equal(0, result.MovedCount);
        Assert.Equal(newFolder, result.Preferences.StorageFolder);
    }

    [Fact]
    public void A_session_that_cannot_be_read_is_reported_and_left_in_place()
    {
        string oldFolder = NewTempDirectory();
        string newFolder = NewTempDirectory();

        WriteSession(oldFolder, "site visit", Guid.NewGuid());
        File.WriteAllText(Path.Combine(oldFolder, "broken.json"), "{ not json");

        StorageLocationMigrationService service = new(new AtomicSessionStore(oldFolder));

        StorageMigrationResult result = service.Apply(
            UserPreferences.Default with { StorageFolder = oldFolder },
            newFolder,
            StorageMigrationChoice.MoveExistingSessions);

        Assert.Equal(StorageMigrationOutcome.PartiallyMoved, result.Outcome);
        Assert.Equal(1, result.MovedCount);
        Assert.Single(result.Failures);
        Assert.True(File.Exists(Path.Combine(oldFolder, "broken.json")), "an unreadable file is not deleted");
    }

    // ------------------------------------------------------------------ test doubles

    /// <summary>A store that fails writes: the first <paramref name="failAfter"/> succeed, then everything fails.</summary>
    private sealed class FailingSessionStore(int failures, int failAfter = 0) : ISessionStore
    {
        private readonly AtomicSessionStore _real = new(Path.GetTempPath());
        private int _writes;

        public SessionDocument Read(string path) => _real.Read(path);

        public void Write(string path, SessionDocument document)
        {
            if(_writes++ >= failAfter && failures > 0)
            {
                throw new IOException("the disk is full");
            }

            _real.Write(path, document);
        }
    }
}
