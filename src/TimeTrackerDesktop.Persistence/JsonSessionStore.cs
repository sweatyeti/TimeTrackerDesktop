using TimeTrackerDesktop.Domain;

namespace TimeTrackerDesktop.Persistence;

/// <summary>
/// Placeholder store from the solution bootstrap, kept so Task 2.2 has somewhere to build.
///
/// It delegates its JSON to <see cref="TtcJsonSerializer"/> rather than carrying its own copy: the
/// bootstrap's version used <c>JsonSerializerDefaults.Web</c>, which writes an escaped offset instead of
/// TTC's literal <c>+</c>, drops every field it does not know, and validates nothing. Two serializers in
/// one project is two answers to the same question, and only one of them can be right.
///
/// Per-user location, atomic writes and the five-second flush belong to Task 2.2, not here.
/// </summary>
public sealed class JsonSessionStore
{
    /// <summary>Reads a session file into domain state, throwing when the file cannot be used.</summary>
    public static SessionState Read(string json) => TtcJsonSerializer.Read(json).ToSessionState();

    /// <summary>Writes domain state as a TTC-compatible v2 session file.</summary>
    public static string Write(SessionState state) => TtcJsonSerializer.Write(SessionDocument.FromSessionState(state));
}
