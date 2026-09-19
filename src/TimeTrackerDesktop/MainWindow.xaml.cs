using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TimeTrackerDesktop.Platform.Windows;

namespace TimeTrackerDesktop;

/// <summary>
/// The integration-spike window (plan Task 3.1). Borderless, rounded, draggable from its own surface, with a
/// text box and a button that must keep normal interaction.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly IWindowInteropService _interop;

    public MainWindow(IWindowInteropService interop)
    {
        ArgumentNullException.ThrowIfNull(interop);

        _interop = interop;

        InitializeComponent();

        _interop.ApplyWidgetChrome(this);
        _interop.SetDarkMode(this, dark: true);

        DragSurface.PointerPressed += OnDragSurfacePointerPressed;
        TopmostToggle.Click += OnTopmostToggled;

        StatusText.Text = "Chrome applied. Drag anywhere except the controls.";
    }

    /// <summary>
    /// Shows and focuses the window. Used by the tray click and by the single-instance path when a second
    /// launch asks this instance to come forward.
    /// </summary>
    public void ShowAndFocus()
    {
        Activate();
    }

    /// <summary>Reports what the spike is doing, for the on-screen evidence trail.</summary>
    public void ReportStatus(string message) => StatusText.Text = message;

    private void OnDragSurfacePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // the spike's second check: a press on a text box or a button must never become a window drag
        if(!_interop.ShouldBeginDrag(e.OriginalSource))
        {
            return;
        }

        _interop.BeginDrag(this);
    }

    private void OnTopmostToggled(object sender, RoutedEventArgs e)
    {
        _interop.SetTopmost(this, TopmostToggle.IsChecked == true);

        // read it back rather than trusting the request: the spike's third check is that get and set agree
        ReportStatus($"Topmost requested: {TopmostToggle.IsChecked == true}; window reports: {_interop.IsTopmost(this)}");
    }
}
