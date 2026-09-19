using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using TimeTrackerDesktop.Domain;
using TimeTrackerDesktop.Platform.Windows;
using TimeTrackerDesktop.ViewModels;
using TimeTrackerDesktop.Views;

namespace TimeTrackerDesktop.Windows;

/// <summary>
/// The widget window (plan Task 4.1). It opens on the session chooser, and the widget surface replaces the
/// chooser once a session is chosen — Task 4.2 builds that surface.
///
/// The window chrome and the drag come from the Phase 3 spike, whose five checks passed on Windows 11, so
/// none of that behaviour is re-guessed here.
/// </summary>
public sealed partial class MainWidgetWindow : Window
{
    private readonly IWindowInteropService _interop;

    private bool _dragging;
    private DispatcherTimer? _tickTimer;
    private int _dragStartCursorX;
    private int _dragStartCursorY;
    private int _dragStartWindowX;
    private int _dragStartWindowY;

    public MainWidgetWindow(IWindowInteropService interop)
    {
        ArgumentNullException.ThrowIfNull(interop);

        _interop = interop;

        InitializeComponent();

        _interop.ApplyWidgetChrome(this);
        _interop.SetDarkMode(this, dark: true);

        // always-on-top is the default preference; a HUD that opens behind other windows is not doing its job
        _interop.SetTopmost(this, topmost: true);

        // handledEventsToo: the hosted page handles presses itself, and a drag that only worked on uncovered
        // background would leave the window immovable wherever content sits on top of it
        DragSurface.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(OnDragSurfacePointerPressed),
            handledEventsToo: true);
        DragSurface.AddHandler(
            UIElement.PointerMovedEvent,
            new PointerEventHandler(OnDragSurfacePointerMoved),
            handledEventsToo: true);
        DragSurface.AddHandler(
            UIElement.PointerReleasedEvent,
            new PointerEventHandler(OnDragSurfacePointerReleased),
            handledEventsToo: true);
    }

    /// <summary>Raised once the user has chosen a session in the chooser.</summary>
    public event EventHandler<SessionService>? SessionChosen;

    /// <summary>
    /// The chooser needs room for its rows and buttons; the widget is a compact card. Both are set explicitly
    /// because a borderless window with no requested size gets a default that clipped the chooser's buttons
    /// off the bottom — measured, not assumed: the button text was absent from a screenshot of the running app.
    /// </summary>
    private static readonly global::Windows.Graphics.SizeInt32 ChooserSize = new(460, 620);

    private static readonly global::Windows.Graphics.SizeInt32 WidgetSize = new(420, 260);

    /// <summary>Shows the session chooser. The widget surface replaces it once a session is chosen.</summary>
    public void ShowChooser(SessionChooserViewModel viewModel)
    {
        SessionChooserPage page = new(viewModel);

        page.SessionChosen += (_, session) => SessionChosen?.Invoke(this, session);

        Host.Content = page;
        _interop.SetSize(this, ChooserSize.Width, ChooserSize.Height);
    }

    /// <summary>
    /// Shows the widget for a chosen session (plan Task 4.2).
    ///
    /// The timer only refreshes the duration text: the elapsed time is always derived from the entry's
    /// persisted start, so a missed tick, a suspended process or a hidden window cannot make it wrong.
    /// </summary>
    public void ShowWidget(SessionService session, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(clock);

        WidgetShell shell = new(new WidgetViewModel(session, clock));

        Host.Content = shell;
        _interop.SetSize(this, WidgetSize.Width, WidgetSize.Height);

        if(_tickTimer is null)
        {
            _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _tickTimer.Tick += (_, _) => (Host.Content as WidgetShell)?.ViewModel.Tick();
            _tickTimer.Start();
        }
    }

    /// <summary>Shows and focuses the window, for the tray and single-instance paths.</summary>
    public void ShowAndFocus() => Activate();

    /// <summary>Reports what the app is doing, on the surface.</summary>
    public void ReportStatus(string message) => StatusText.Text = message;

    private void OnDragSurfacePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // a press on anything interactive must keep its normal behaviour rather than moving the window
        if(!_interop.ShouldBeginDrag(e.OriginalSource))
        {
            return;
        }

        // A borderless window has no caption, so WM_NCLBUTTONDOWN/HTCAPTION does nothing here: the drag is
        // done by hand, from the cursor's screen position and the window's position at the moment of the press
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
        e.Handled = true;
    }
}
