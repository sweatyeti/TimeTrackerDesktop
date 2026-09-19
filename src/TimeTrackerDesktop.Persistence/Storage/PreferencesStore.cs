using System.Text.Json;
using System.Text.Json.Serialization;
using TimeTrackerDesktop.Domain;

namespace TimeTrackerDesktop.Persistence;

/// <summary>
/// Stores the user's preferences as a small JSON file (plan Task 2.4).
///
/// Deliberately forgiving on read: a preferences file is a convenience, so a missing, empty or damaged one
/// falls back to the defaults rather than stopping the app from starting. Fields this build does not know are
/// ignored rather than treated as corruption, so a newer build's settings file does not reset an older
/// build's user.
///
/// Enums are written as names, not numbers: a settings file a user can read is a settings file they can fix,
/// and reordering an enum cannot silently change what a stored value means.
/// </summary>
public sealed class PreferencesStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _filePath;

    public PreferencesStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        _filePath = filePath;
    }

    /// <summary>
    /// Where preferences live by default: beside the session files, under the per-user local application-data
    /// folder. Not the working directory and not the executable's directory, for the same reason sessions are
    /// not: an unpackaged app can be launched from anywhere.
    /// </summary>
    public static string DefaultFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TimeTrackerDesktop",
        "preferences.json");

    /// <summary>The file this store reads and writes.</summary>
    public string FilePath => _filePath;

    /// <summary>Reads the preferences, falling back to <see cref="UserPreferences.Default"/>.</summary>
    public UserPreferences Read()
    {
        if(!File.Exists(_filePath))
        {
            return UserPreferences.Default;
        }

        try
        {
            return JsonSerializer.Deserialize<UserPreferences>(AtomicFile.Read(_filePath), Options)
                ?? UserPreferences.Default;
        }
        catch(JsonException)
        {
            // a damaged preferences file is not worth refusing to start over
            return UserPreferences.Default;
        }
        catch(IOException)
        {
            // ditto for a file that is momentarily unreadable
            return UserPreferences.Default;
        }
    }

    /// <summary>Writes the preferences atomically.</summary>
    public void Write(UserPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

        AtomicFile.Write(_filePath, JsonSerializer.Serialize(preferences, Options));
    }
}
