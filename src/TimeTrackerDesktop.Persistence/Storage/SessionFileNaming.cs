using System.Text;

namespace TimeTrackerDesktop.Persistence;

/// <summary>
/// How a session's file is named (plan Task 2.3).
///
/// The <b>slug rule is TTC's, character for character</b>, because the fixtures pin it: a name of
/// <c>a:b*c?d&lt;e&gt;f|g/h\i</c> becomes <c>abcdefghi.json</c>, and a name made entirely of hostile
/// characters becomes <c>session.json</c>. Spaces become dashes and the result is whitespace-trimmed, but a
/// leading dash is <b>not</b> removed — TTC does not remove one either, and a slug that differs from TTC's is
/// a file TTC will not find.
///
/// The <b>collision sequence is deliberately not TTC's</b>. TTC starts at <c>-2</c>; the plan settles
/// <c>name.json</c>, <c>name-1.json</c>, <c>name-2.json</c> for the desktop, and the same rule covers both
/// import and export so that a copy can never overwrite a file that is already there.
/// </summary>
public static class SessionFileNaming
{
    /// <summary>What a name with nothing usable left in it becomes.</summary>
    public const string FallbackSlug = "session";

    /// <summary>
    /// Characters that cannot appear in a file name. The platform's own set is unioned with the Windows set
    /// and the control characters, so a slug produced on Linux is the same slug Windows would produce —
    /// otherwise one session would land in differently-named files on different machines.
    /// </summary>
    private static readonly HashSet<char> HostileCharacters = BuildHostileCharacters();

    /// <summary>Slugs a session name the way TTC does.</summary>
    public static string Slugify(string? sessionName)
    {
        if(string.IsNullOrEmpty(sessionName))
        {
            return FallbackSlug;
        }

        StringBuilder slug = new(sessionName.Length);

        foreach(char character in sessionName)
        {
            if(character == ' ')
            {
                slug.Append('-');
            }
            else if(!HostileCharacters.Contains(character))
            {
                slug.Append(character);
            }
        }

        string result = slug.ToString().Trim();

        return string.IsNullOrEmpty(result) ? FallbackSlug : result;
    }

    /// <summary>
    /// The file name for a session at a collision index: <c>name.json</c> at 0, then <c>name-1.json</c>,
    /// <c>name-2.json</c>, …
    /// </summary>
    public static string FileNameFor(string? sessionName, int collisionIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(collisionIndex);

        string slug = Slugify(sessionName);

        return collisionIndex == 0 ? slug + ".json" : $"{slug}-{collisionIndex}.json";
    }

    /// <summary>The first free path for a session in <paramref name="directoryPath"/>.</summary>
    public static string NextAvailablePath(string directoryPath, string? sessionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        for(int collisionIndex = 0; ; collisionIndex++)
        {
            string path = Path.Combine(directoryPath, FileNameFor(sessionName, collisionIndex));

            if(!File.Exists(path))
            {
                return path;
            }
        }
    }

    private static HashSet<char> BuildHostileCharacters()
    {
        HashSet<char> characters = new(Path.GetInvalidFileNameChars());

        // the Windows-invalid set, kept portable even when running on Linux
        foreach(char character in new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' })
        {
            characters.Add(character);
        }

        for(int code = 0; code < 32; code++)
        {
            characters.Add((char)code);
        }

        return characters;
    }
}
