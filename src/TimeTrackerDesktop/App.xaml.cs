using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TimeTrackerDesktop.Domain;
using TimeTrackerDesktop.Persistence;
using TimeTrackerDesktop.Platform.Windows;
using TimeTrackerDesktop.ViewModels;
using TimeTrackerDesktop.Windows;

namespace TimeTrackerDesktop;

/// <summary>
/// The application composition root (plan Task 4.1).
///
/// This is the one place that decides where sessions live, which store reads them, and which clock the whole
/// app shares. Everything downstream takes those as arguments, which is what keeps the domain and the view
/// models testable without a UI thread.
///
/// Startup order matters: the chooser is shown <b>before</b> any widget surface exists, because the plan
/// requires a session choice before the widget opens.
/// </summary>
public partial class App : Application
{
    private readonly IWindowInteropService _interop = new WindowInteropService();
    private readonly ISingleInstanceService _singleInstance = new SingleInstanceService();
    private readonly TrayIconService _tray = new();
    private readonly IClock _clock = new SystemClock();

    private MainWidgetWindow? _window;
    private SessionService? _session;

    public App() => InitializeComponent();

    /// <summary>The live session, once the user has chosen one.</summary>
    public SessionService? Session => _session;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // a second launch has already asked the first instance to show itself, so this one just leaves
        if(!_singleInstance.IsFirstInstance)
        {
            Exit();
            return;
        }

        _singleInstance.ShowRequested += (_, _) => _window?.ShowAndFocus();

        _window = new MainWidgetWindow(_interop);
        _window.SessionChosen += OnSessionChosen;
        _window.Activate();

        _tray.LeftClicked += (_, _) =>
        {
            _window?.ShowAndFocus();
            _window?.ReportStatus("Tray: left click received");
        };

        _tray.RightClicked += (_, _) =>
        {
            _window?.ReportStatus("Tray: right click received");
            ShowTrayMenu();
        };

        try
        {
            _tray.Show(_window, "TimeTrackerDesktop");
        }
        catch(InvalidOperationException exception)
        {
            _window.ReportStatus($"Tray icon unavailable: {exception.Message}");
        }

        _window.Closed += (_, _) => _tray.Dispose();

        _window.ShowChooser(CreateChooser());
    }

    /// <summary>
    /// Builds the chooser from the app's own decisions: the storage folder, the store that reads it, and the
    /// shared clock. The folder comes from preferences, resolved against the store's default — a stored
    /// preference that is unset means "wherever the app puts sessions", not a frozen absolute path.
    /// </summary>
    private SessionChooserViewModel CreateChooser()
    {
        UserPreferences preferences = new PreferencesStore(PreferencesStore.DefaultFilePath).Read();
        string storageFolder = preferences.ResolveStorageFolder(AtomicSessionStore.DefaultDirectory);

        return new SessionChooserViewModel(new AtomicSessionStore(storageFolder), storageFolder, _clock);
    }

    private void OnSessionChosen(object? sender, SessionService session)
    {
        _session = session;

        // the chooser has done its job: the widget takes over the surface
        _window?.ShowWidget(session, _clock);
        _window?.ReportStatus($"Session '{session.State.Name}' open.");
    }

    /// <summary>The tray's right-click menu.</summary>
    private void ShowTrayMenu()
    {
        if(_window?.Content is not FrameworkElement anchor)
        {
            return;
        }

        MenuFlyout menu = new();

        MenuFlyoutItem show = new() { Text = "Show widget" };
        show.Click += (_, _) => _window?.ShowAndFocus();

        MenuFlyoutItem exit = new() { Text = "Exit" };
        exit.Click += (_, _) => Exit();

        menu.Items.Add(show);
        menu.Items.Add(exit);

        menu.ShowAt(anchor);
    }
}
