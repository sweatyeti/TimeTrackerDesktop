using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using TimeTrackerDesktop.Domain;
using TimeTrackerDesktop.Platform.Windows;
using TimeTrackerDesktop.ViewModels;
using TimeTrackerDesktop.Views;

namespace TimeTrackerDesktop.Windows;

/// <summary>
/// The widget window (plan Tasks 4.1-4.3). It opens on the session chooser, and the widget surface replaces
/// the chooser once a session is chosen.
///
/// The window chrome and the drag come from the Phase 3 spike, whose five checks passed on Windows 11, so
/// none of that behaviour is re-guessed here. Size and always-on-top come from <see cref="UserPreferences"/>.
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
    /// The chooser needs room for its rows and buttons, and a borderless window with no requested size gets a
    /// default that clipped them. The widget's size is no longer a constant here: it comes from the user's
    /// Compact/Comfortable/Expanded preference, because a preset that does not change the window is decoration.
    /// </summary>
    private static readonly global::Windows.Graphics.SizeInt32 ChooserSize = new(460, 620);

    /// <summary>Shows the session chooser. The widget surface replaces it once a session is chosen.</summary>
    public void ShowChooser(SessionChooserViewModel viewModel)
    {
        SessionChooserPage page = new(viewModel);

        page.SessionChosen += (_, session) => SessionChosen?.Invoke(this, session);

        // AppWindow.Resize, not SetWindowPos: a raw SetWindowPos changes the HWND but WinUI keeps its own
        // notion of the size, so AppWindow reported 420x260 while the window was still 620 tall and the
        // content stayed measured against the real one - which clipped the widget's button row. Measured.
        AppWindow.Resize(ChooserSize);
        Host.Content = page;

        WriteGeometryDiagnostic("immediately after ShowChooser");

        // and again once the layout has settled: if WinUI applies its own size on a later pass, the two lines
        // will disagree, which is the whole question
        DispatcherTimer probe = new() { Interval = TimeSpan.FromSeconds(3) };
        probe.Tick += (_, _) =>
        {
            probe.Stop();
            WriteGeometryDiagnostic("3s after ShowChooser");
        };
        probe.Start();
    }

    /// <summary>
    /// Writes the window's geometry to a file so it can be inspected from outside the session.
    ///
    /// Necessary because PowerShell over SSH runs in session 0 and reports <c>MainWindowHandle = 0</c> for a
    /// window in session 1, so every geometry check from outside is blind. A diagnostic must come from in here.
    /// </summary>
    public void WriteGeometryDiagnostic(string stage)
    {
        try
        {
            _interop.GetWindowBounds(this, out int hwndX, out int hwndY, out int hwndWidth, out int hwndHeight);

            string line =
                $"{DateTimeOffset.Now:HH:mm:ss.fff} | {stage} | "
                + $"position=({AppWindow.Position.X},{AppWindow.Position.Y}) "
                + $"size=({AppWindow.Size.Width}x{AppWindow.Size.Height}) "
                + $"hwnd=({hwndX},{hwndY} {hwndWidth}x{hwndHeight}) "
                + $"host=({Host.ActualWidth}x{Host.ActualHeight}) "
                + $"surface=({DragSurface.ActualWidth}x{DragSurface.ActualHeight}) "
                + (Host.Content as Views.WidgetShell)?.DescribeButtons()
                + $" res:key={Application.Current.Resources.ContainsKey("TrackingPlayBrush")}"
                + $" lookup={(Application.Current.Resources.TryGetValue("TrackingPlayBrush", out object? brush) ? brush?.GetType().Name ?? "null" : "MISSING")}";

            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "ttd-window.txt"),
                line + Environment.NewLine);
        }
        catch(IOException)
        {
            // a diagnostic must never be the reason the app fails
        }
    }

    /// <summary>
    /// Shows the widget for a chosen session (plan Tasks 4.2 and 4.3).
    ///
    /// The timer only refreshes the display: the elapsed time is always derived from the entry's persisted
    /// start, so a missed tick, a suspended process or a hidden window cannot make it wrong — and no tick ever
    /// writes to the session, so the recorded start time cannot move.
    ///
    /// Size and always-on-top come from the user's preferences rather than constants. That is what makes the
    /// presets and the toggle mean anything, and it is why the height is taken from the view model, which
    /// enforces the explicit content minimum a preset may not go below.
    /// </summary>
    public void ShowWidget(SessionService session, IClock clock, UserPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(preferences);

        WidgetViewModel viewModel = new(session, clock, preferences);
        WidgetShell shell = new(viewModel);

        global::Windows.Graphics.SizeInt32 size = new(viewModel.WidgetWidth, viewModel.WidgetHeight);

        _interop.SetTopmost(this, viewModel.AlwaysOnTop);

        // AppWindow.Resize, not SetWindowPos: a raw SetWindowPos changes the HWND while WinUI keeps its own
        // notion of the size, so the two disagree. Measured.
        AppWindow.Resize(size);

        // The measure taken immediately after a resize reports the PREVIOUS size and does not settle until the
        // following layout pass (measured: host went 444x575, then 420x232 three seconds later). Sizing the root
        // explicitly keeps the card filling the window in the meantime; removing it has not been proven safe.
        DragSurface.Width = size.Width;
        DragSurface.Height = size.Height;

        Host.Content = shell;
        WriteGeometryDiagnostic("after ShowWidget");

        // and again once the layout has settled: the line above fires before the pass that follows the resize,
        // so it reports the previous size. This is the one that says whether the card actually fits.
        DispatcherTimer settle = new() { Interval = TimeSpan.FromSeconds(3) };
        settle.Tick += (_, _) =>
        {
            settle.Stop();
            WriteGeometryDiagnostic("3s after ShowWidget");
        };
        settle.Start();

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
