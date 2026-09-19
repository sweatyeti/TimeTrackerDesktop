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

    private bool _dragging;
    private int _dragStartCursorX;
    private int _dragStartCursorY;
    private int _dragStartWindowX;
    private int _dragStartWindowY;

    public MainWindow(IWindowInteropService interop)
    {
        ArgumentNullException.ThrowIfNull(interop);

        _interop = interop;

        InitializeComponent();

        _interop.ApplyWidgetChrome(this);
        _interop.SetDarkMode(this, dark: true);

        // always-on-top is the default preference, so the widget must start that way: a HUD that opens
        // behind other windows is not doing its job. Phase 4.3 replaces this with the stored preference.
        _interop.SetTopmost(this, topmost: true);
        TopmostToggle.IsChecked = true;

        DragSurface.PointerPressed += OnDragSurfacePointerPressed;
        DragSurface.PointerMoved += OnDragSurfacePointerMoved;
        DragSurface.PointerReleased += OnDragSurfacePointerReleased;
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

        // A borderless window has no caption, so WM_NCLBUTTONDOWN/HTCAPTION does nothing - measured, not
        // assumed: the window did not move. The drag is therefore done by hand, from the cursor's screen
        // position and the window's position at the moment of the press.
        _dragging = true;

        _interop.GetCursorPosition(out _dragStartCursorX, out _dragStartCursorY);

        _dragStartWindowX = AppWindow.Position.X;
        _dragStartWindowY = AppWindow.Position.Y;

        DragSurface.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnDragSurfacePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if(!_dragging)
        {
            return;
        }

        _interop.GetCursorPosition(out int cursorX, out int cursorY);

        _interop.MoveTo(
            this,
            _dragStartWindowX + (cursorX - _dragStartCursorX),
            _dragStartWindowY + (cursorY - _dragStartCursorY));

        ReportStatus($"Dragging: window at ({AppWindow.Position.X}, {AppWindow.Position.Y})");
        e.Handled = true;
    }

    private void OnDragSurfacePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if(!_dragging)
        {
            return;
        }

        _dragging = false;
        DragSurface.ReleasePointerCapture(e.Pointer);

        ReportStatus($"Window moved to ({AppWindow.Position.X}, {AppWindow.Position.Y})");
        e.Handled = true;
    }

    private void OnTopmostToggled(object sender, RoutedEventArgs e)
    {
        _interop.SetTopmost(this, TopmostToggle.IsChecked == true);

        // read it back rather than trusting the request: the spike's third check is that get and set agree
        ReportStatus($"Topmost requested: {TopmostToggle.IsChecked == true}; window reports: {_interop.IsTopmost(this)}");
    }
}
