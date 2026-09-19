namespace TimeTrackerDesktop.Domain;

/// <summary>Which theme the app renders in. <c>System</c> follows Windows.</summary>
public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>What the widget's timer shows.</summary>
public enum TimerDisplayMode
{
    /// <summary>The elapsed duration, ticking.</summary>
    Elapsed,

    /// <summary>The entry's start time, e.g. "Started 9:41 AM".</summary>
    StartedTime,
}

/// <summary>The widget's size preset.</summary>
public enum WidgetSizePreset
{
    Compact,
    Comfortable,
    Expanded,
}

/// <summary>What closing the widget does while nothing is being tracked.</summary>
public enum StoppedCloseBehavior
{
    /// <summary>Close hides to the tray; the app keeps running.</summary>
    HideToTray,

    /// <summary>Close exits the app.</summary>
    Exit,
}

/// <summary>Where the widget last sat, and on which monitor.</summary>
public sealed record WidgetPlacement(double X, double Y, string? MonitorId);

/// <summary>
/// Everything the user can set that outlives a run (plan Task 2.4).
///
/// Only preferences that something actually implements belong here — the plan's rule is to persist what is
/// implemented, not what might be. Launch-at-login and global shortcuts are explicitly out of scope for v1,
/// and a test fails if a property resembling either appears.
///
/// <see cref="StorageFolder"/> is nullable on purpose: <c>null</c> means "wherever the app puts sessions by
/// default", which only the persistence layer can resolve. Storing a resolved absolute path here on first
/// run would freeze today's default and stop it following a future change.
/// </summary>
public sealed record UserPreferences(
    AppTheme Theme,
    string? CustomAccentColor,
    TimerDisplayMode TimerDisplay,
    bool AlwaysOnTop,
    WidgetSizePreset WidgetSize,
    WidgetPlacement? WidgetPlacement,
    StoppedCloseBehavior StoppedClose,
    string? StorageFolder)
{
    /// <summary>
    /// The first-run defaults. Chosen to be unsurprising rather than maximal: follow the system theme, show
    /// elapsed time (the thing a time tracker is for), keep the widget on top, and hide to the tray on close
    /// so closing it never loses a running entry by accident.
    /// </summary>
    public static UserPreferences Default { get; } = new(
        Theme: AppTheme.System,
        CustomAccentColor: null,
        TimerDisplay: TimerDisplayMode.Elapsed,
        AlwaysOnTop: true,
        WidgetSize: WidgetSizePreset.Comfortable,
        WidgetPlacement: null,
        StoppedClose: StoppedCloseBehavior.HideToTray,
        StorageFolder: null);

    /// <summary>
    /// The folder to use, resolving an unset preference to <paramref name="defaultFolder"/>. The fallback is
    /// supplied by the caller because the default location belongs to the storage layer, not the domain.
    /// </summary>
    public string ResolveStorageFolder(string defaultFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultFolder);

        return string.IsNullOrWhiteSpace(StorageFolder) ? defaultFolder : StorageFolder;
    }
}
