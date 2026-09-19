using System.Text;

namespace TimeTrackerDesktop.Persistence;

/// <summary>
/// The one way this app replaces a file (plan Task 2.2's rule, shared by every writer).
///
/// Write to <c>&lt;name&gt;.tmp</c> in the <b>same directory</b>, flush it all the way to disk, then move it
/// onto the final name. A crash leaves either the previous file or the complete new one — never a half-written
/// file. Both details are load-bearing: <c>flushToDisk</c>, because the move is only atomic if the bytes are
/// already durable, and the same directory, because a temporary file on another volume turns the move into a
/// copy.
///
/// It lives here, rather than inside the session store, so that preferences and any future file get the same
/// guarantee instead of a second implementation that drifts.
/// </summary>
internal static class AtomicFile
{
    /// <summary>UTF-8 without a BOM, because a BOM makes the file unreadable to TTC.</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The temporary path used while writing <paramref name="path"/>, in the same directory.</summary>
    public static string TemporaryPathFor(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return path + ".tmp";
    }

    /// <summary>Replaces <paramref name="path"/> with <paramref name="contents"/>, atomically.</summary>
    public static void Write(string path, string contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);

        string temporary = TemporaryPathFor(path);

        using(FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(Utf8NoBom.GetBytes(contents));
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Reads a file written by <see cref="Write"/>, tolerating a BOM from elsewhere.</summary>
    public static string Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return File.ReadAllText(path, Utf8NoBom);
    }
}
