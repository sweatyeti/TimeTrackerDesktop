using Microsoft.UI.Xaml;

namespace TimeTrackerDesktop;

/// <summary>
/// Application entry point. Phase 0 only stands the shell up; session state, view models and the
/// widget surface arrive in later phases, and <c>SessionService</c> remains the sole authority for
/// mutable session state.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
