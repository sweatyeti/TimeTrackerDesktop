using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace TimeTrackerDesktop.Platform.Windows;

/// <summary>
/// The window behaviours the widget needs from the platform (plan Task 3.1).
///
/// An interface rather than a static helper because the spike's gate is about behaviour a test host cannot
/// exercise: an interface keeps the decision logic testable while the actual Win32 calls stay in one place.
/// </summary>
public interface IWindowInteropService
{
    /// <summary>Removes the system border and title bar and rounds the corners, for the widget shell.</summary>
    void ApplyWidgetChrome(Window window);

    /// <summary>Reads whether the window is currently topmost.</summary>
    bool IsTopmost(Window window);

    /// <summary>Sets or clears topmost.</summary>
    void SetTopmost(Window window, bool topmost);

    /// <summary>
    /// Whether a pointer press on <paramref name="source"/> should start a window drag. False for anything
    /// interactive, so a button click or a text selection never turns into a drag.
    /// </summary>
    bool ShouldBeginDrag(object? source);

    /// <summary>Starts a window drag, as if the user had grabbed the title bar.</summary>
    void BeginDrag(Window window);

    /// <summary>Applies the Windows 11 corner preference.</summary>
    void SetRoundedCorners(Window window, bool rounded);

    /// <summary>Applies the immersive dark-mode window attribute.</summary>
    void SetDarkMode(Window window, bool dark);
}

/// <summary>
/// Which UI elements a drag may start from (plan Task 3.1, step 2).
///
/// Pure and type-based on purpose: this is the one part of the drag behaviour that can be tested without a
/// UI thread, and it is the part most likely to regress. A widget that drags when you click a button, or
/// refuses to drag from its own background, is broken in a way no compiler catches.
/// </summary>
public static class DragSurfacePolicy
{
    /// <summary>
    /// Control type names that must keep normal interaction. Matched by walking the base chain, so a
    /// subclass — or a templated part inside one of these — is still recognised as interactive.
    /// </summary>
    private static readonly HashSet<string> InteractiveTypeNames = new(StringComparer.Ordinal)
    {
        "Button",
        "ButtonBase",
        "ToggleButton",
        "CheckBox",
        "RadioButton",
        "ComboBox",
        "Slider",
        "TextBox",
        "RichEditBox",
        "PasswordBox",
        "ScrollViewer",
        "ScrollBar",
        "ListView",
        "ListViewItem",
        "GridView",
        "GridViewItem",
        "MenuFlyoutItem",
        "HyperlinkButton",
        "RepeatButton",
        "AppBarButton",
        "NumberBox",
        "DatePicker",
        "TimePicker",
        "CalendarView",
        "WebView",
        "WebView2",
    };

    /// <summary>
    /// True when a press on an element of this type may start a drag. A null or unknown type is treated as
    /// draggable: the widget's own surface is plain layout, and refusing to drag there would make the window
    /// immovable.
    /// </summary>
    public static bool CanStartDrag(Type? sourceType)
    {
        for(Type? type = sourceType; type is not null; type = type.BaseType)
        {
            if(InteractiveTypeNames.Contains(type.Name))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// The Win32 implementation of <see cref="IWindowInteropService"/>.
///
/// The APIs chosen here, and why, are recorded in <c>docs/DECISIONS.md</c> — the spike's whole purpose is to
/// prove these work on Windows 11 before any visual work is built on top of them.
/// </summary>
public sealed class WindowInteropService : IWindowInteropService
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOPMOST = 0x00000008;

    // documented DwmSetWindowAttribute ids; the corner preference is Windows 11 only
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private const int DWMWCP_DONOTROUND = 1;

    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 2;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    private static readonly nint HWND_TOPMOST = -1;
    private static readonly nint HWND_NOTOPMOST = -2;

    public void ApplyWidgetChrome(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        // the WinUI-3 way to lose the chrome: the presenter owns the border and title bar
        if(window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        window.ExtendsContentIntoTitleBar = true;

        SetRoundedCorners(window, rounded: true);
    }

    public bool IsTopmost(Window window)
    {
        nint handle = HandleOf(window);

        return (GetWindowLong(handle, GWL_EXSTYLE) & WS_EX_TOPMOST) == WS_EX_TOPMOST;
    }

    public void SetTopmost(Window window, bool topmost)
    {
        nint handle = HandleOf(window);

        // SetWindowPos rather than the presenter: it keeps the flag readable through GetWindowLong, so
        // IsTopmost and SetTopmost cannot disagree about the same window
        SetWindowPos(
            handle,
            topmost ? HWND_TOPMOST : HWND_NOTOPMOST,
            0,
            0,
            0,
            0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public bool ShouldBeginDrag(object? source)
    {
        // walk UP the tree from the original source. A click on a button lands on the TextBlock inside it,
        // so classifying only the source would let every button press start a window drag - the exact
        // failure the spike's second check exists to catch.
        for(DependencyObject? element = source as DependencyObject;
            element is FrameworkElement frameworkElement;
            element = VisualTreeHelper.GetParent(frameworkElement))
        {
            if(!DragSurfacePolicy.CanStartDrag(frameworkElement.GetType()))
            {
                return false;
            }
        }

        return true;
    }

    public void BeginDrag(Window window)
    {
        nint handle = HandleOf(window);

        // the classic non-client drag: release the capture, then tell the window the caption was grabbed
        ReleaseCapture();
        SendMessage(handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
    }

    public void SetRoundedCorners(Window window, bool rounded)
    {
        int preference = rounded ? DWMWCP_ROUND : DWMWCP_DONOTROUND;

        // returns E_INVALIDARG on Windows 10, which has no corner preference; ignoring the result is
        // deliberate, because the window still works, it is simply square
        _ = DwmSetWindowAttribute(HandleOf(window), DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }

    public void SetDarkMode(Window window, bool dark)
    {
        int enabled = dark ? 1 : 0;

        _ = DwmSetWindowAttribute(HandleOf(window), DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int));
    }

    private static nint HandleOf(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        return WindowNative.GetWindowHandle(window);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(nint windowHandle, int index);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint windowHandle,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessage(nint windowHandle, int message, nint wParam, nint lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint windowHandle, int attribute, ref int value, int size);
}
