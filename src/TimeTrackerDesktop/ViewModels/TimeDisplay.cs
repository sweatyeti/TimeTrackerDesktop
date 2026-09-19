using System.Globalization;

namespace TimeTrackerDesktop.ViewModels;

/// <summary>
/// How durations and instants read on screen.
///
/// One place for the formats, because the chooser and the widget must agree: a session that reads
/// <c>1h 05m</c> in the chooser and something else in the widget is the kind of inconsistency that makes a
/// user distrust the numbers.
/// </summary>
public static class TimeDisplay
{
    /// <summary>The session/entry start format TTC uses in its chooser, kept so the two apps read the same.</summary>
    public const string DateFormat = "yyyy-MM-dd HH:mm";

    /// <summary>A time of day, for the widget's "Started 9:41 AM" line.</summary>
    public const string ClockFormat = "h:mm tt";

    /// <summary>A duration as hours and minutes, rounded up to the minute the way the summary rounds.</summary>
    public static string Format(TimeSpan duration)
    {
        int minutes = (int)Math.Ceiling(duration.TotalMinutes);
        int hours = minutes / 60;

        return hours > 0 ? $"{hours}h {minutes % 60:00}m" : $"{minutes}m";
    }

    /// <summary>A duration as <c>h:mm:ss</c>, for the widget's ticking display.</summary>
    public static string FormatTicking(TimeSpan duration)
    {
        TimeSpan clamped = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;

        return $"{(int)clamped.TotalHours}:{clamped.Minutes:00}:{clamped.Seconds:00}";
    }

    /// <summary>An instant as a date and time.</summary>
    public static string Date(DateTimeOffset instant) => instant.ToString(DateFormat, CultureInfo.InvariantCulture);

    /// <summary>An instant as a time of day.</summary>
    public static string Clock(DateTimeOffset instant) => instant.ToString(ClockFormat, CultureInfo.InvariantCulture);
}
