using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TimeTrackerDesktop.Domain;
using TimeTrackerDesktop.ViewModels;

namespace TimeTrackerDesktop.Views;

/// <summary>
/// The session chooser's surface (plan Task 4.1). Thin on purpose: every rule it depends on — ordering,
/// labelling, what starting does — lives in <see cref="SessionChooserViewModel"/>, which is testable without a
/// UI thread.
/// </summary>
public sealed partial class SessionChooserPage : Page
{
    public SessionChooserPage(SessionChooserViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        // assigned before InitializeComponent: x:Bind reads the property while the markup is being built
        ViewModel = viewModel;

        ViewModel.Refresh();

        InitializeComponent();
    }

    /// <summary>Raised once the user has chosen a session to open.</summary>
    public event EventHandler<SessionService>? SessionChosen;

    public SessionChooserViewModel ViewModel { get; }

    private void OnStartNewSession(object sender, RoutedEventArgs e) =>
        SessionChosen?.Invoke(this, ViewModel.StartNewSession());

    private void OnResume(object sender, RoutedEventArgs e)
    {
        SessionService? session = ViewModel.ResumeSelected();

        // a refusal is already reported through ViewModel.Message; this only fires on success
        if(session is not null)
        {
            SessionChosen?.Invoke(this, session);
        }
    }
}
