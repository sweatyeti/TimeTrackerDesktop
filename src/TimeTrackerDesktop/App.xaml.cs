using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TimeTrackerDesktop.Platform.Windows;

namespace TimeTrackerDesktop;

/// <summary>
/// Application entry point. Phase 3 wires the integration spike together: one instance, a tray icon, and the
/// borderless window. Session state, view models and the real widget surface are later phases, and
/// <c>SessionService</c> remains the sole authority for mutable session state.
/// </summary>
public partial class App : Application
{
    private readonly IWindowInteropService _interop = new WindowInteropService();
    private readonly ISingleInstanceService _singleInstance = new SingleInstanceService();
    private readonly TrayIconService _tray = new();

    private MainWindow? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // A second launch has already asked the first instance to show itself, so this one has nothing left
        // to do but leave. Creating a window first would briefly flash a second widget.
        if(!_singleInstance.IsFirstInstance)
        {
            Exit();
            return;
        }

        _singleInstance.ShowRequested += (_, _) => _window?.ShowAndFocus();

        _window = new MainWindow(_interop);
        _window.Activate();

        _tray.LeftClicked += (_, _) => _window?.ShowAndFocus();
        _tray.RightClicked += (_, _) => ShowTrayMenu();

        try
        {
            _tray.Show(_window, "TimeTrackerDesktop (spike)");
        }
        catch(InvalidOperationException exception)
        {
            // a missing tray icon must not stop the app: report it on the surface instead
            _window.ReportStatus($"Tray icon unavailable: {exception.Message}");
        }

        _window.Closed += (_, _) => _tray.Dispose();
    }

    /// <summary>
    /// The tray's right-click menu. A flyout anchored to the window's content: the spike needs to prove the
    /// callback arrives, not to design the final menu.
    /// </summary>
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
