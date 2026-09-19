using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace TimeTrackerDesktop.Controls;

/// <summary>Which of the two tracking commands a button represents.</summary>
public enum TrackingButtonKind
{
    /// <summary>Stop-and-start: the green triangle.</summary>
    Play,

    /// <summary>Stop only: the red square.</summary>
    Stop,
}

/// <summary>
/// The widget's primary tracking control (plan Task 4.2).
///
/// Carries an accessible name and a tooltip rather than relying on the glyph, and uses the shared icon-button
/// style so keyboard focus is visible. The two kinds live as two buttons in the markup, each with its own
/// themed background, so the colours come from <c>Styling/Colors.xaml</c> instead of being resolved by hand.
/// </summary>
public sealed partial class PrimaryTrackingButton : UserControl
{
    public PrimaryTrackingButton() => InitializeComponent();

    /// <summary>Raised when the button is invoked.</summary>
    public event RoutedEventHandler? Click;

    /// <summary>Which command this button represents.</summary>
    public TrackingButtonKind Kind { get; private set; } = TrackingButtonKind.Play;

    /// <summary>The button's accessible name, for a screen reader.</summary>
    public string AccessibleName { get; private set; } = string.Empty;

    /// <summary>The hover tooltip.</summary>
    public string Tooltip { get; private set; } = string.Empty;

    /// <summary>Whether the button is available.</summary>
    public bool IsAvailable
    {
        get => Kind is TrackingButtonKind.Play ? PlayButton.IsEnabled : StopButton.IsEnabled;
        set
        {
            if(Kind is TrackingButtonKind.Play)
            {
                PlayButton.IsEnabled = value;
            }
            else
            {
                StopButton.IsEnabled = value;
            }
        }
    }

    /// <summary>
    /// Sets which command this button is, and what it says about itself. Called by the shell rather than
    /// bound, because the kind is structural: it decides which glyph exists at all.
    /// </summary>
    public void Configure(TrackingButtonKind kind, string accessibleName, string tooltip)
    {
        Kind = kind;
        AccessibleName = accessibleName;
        Tooltip = tooltip;

        Button target = kind is TrackingButtonKind.Play ? PlayButton : StopButton;

        PlayButton.Visibility = kind is TrackingButtonKind.Play ? Visibility.Visible : Visibility.Collapsed;
        StopButton.Visibility = kind is TrackingButtonKind.Play ? Visibility.Collapsed : Visibility.Visible;

        AutomationProperties.SetName(target, accessibleName);
        ToolTipService.SetToolTip(target, tooltip);
    }

    private void OnClick(object sender, RoutedEventArgs e) => Click?.Invoke(this, e);
}
