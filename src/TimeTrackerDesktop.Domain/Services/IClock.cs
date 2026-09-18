namespace TimeTrackerDesktop.Domain;

/// <summary>
/// The domain's only source of "now".
///
/// Injected rather than read statically so that (a) every transition is deterministic in tests, and
/// (b) the UI can drive a presentation clock — the ticking elapsed-time display — without the domain
/// touching the wall clock or the domain having to know how often the UI redraws.
/// </summary>
public interface IClock
{
    /// <summary>
    /// The current instant, carrying the machine's local offset. An implementation must not return
    /// <see cref="DateTimeOffset.UtcNow"/>: TTC stamps local time and persists the offset in every
    /// timestamp it writes, so the offset is part of the data, not a display detail.
    /// </summary>
    DateTimeOffset Now { get; }
}