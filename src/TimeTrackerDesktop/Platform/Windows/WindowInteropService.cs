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

    /// <summary>
    /// Moves the window to an absolute screen position. This, plus <see cref="GetCursorPosition"/>, is how a
    /// drag is performed on a borderless window.
    /// </summary>
    void MoveTo(Window window, int x, int y);

    /// <summary>Reads the cursor's absolute screen position.</summary>
    void GetCursorPosition(out int x, out int y);

    /// <summary>
    /// Reads the window's real rectangle from the OS. Distinct from <c>AppWindow.Size</c>, which is WinUI's
    /// own model and can disagree with the actual window.
    /// </summary>
    void GetWindowBounds(Window window, out int x, out int y, out int width, out int height);

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

    private const uint SWP_NOSIZE = 0x0001;
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(nint hWnd, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;

        public int Top;

        public int Right;

        public int Bottom;
    }

    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    private const uint SWP_NOZORDER = 0x0004;

    private static readonly nint HWND_TOPMOST = -1;
    private static readonly nint HWND_NOTOPMOST = -2;

    public void ApplyWidgetChrome(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        // the WinUI-3 way to lose the chrome: the presenter owns the border and title bar
        if(window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);

            // resizable is deliberately left alone: the widget's size comes from the plan's presets (Task 4.3)
            // and, in the meantime, refusing resize also blocked the explicit sizing the chooser needs
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

    public void MoveTo(Window window, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(window);

        // fully qualified: this file's namespace is TimeTrackerDesktop.Platform.Windows, so a bare
        // "Windows.Graphics" binds to the local namespace instead of the WinRT one
        window.AppWindow.Move(new global::Windows.Graphics.PointInt32(x, y));
    }

    public void GetCursorPosition(out int x, out int y)
    {
        _ = GetCursorPos(out NativePoint point);

        x = point.X;
        y = point.Y;
    }

    public void GetWindowBounds(Window window, out int x, out int y, out int width, out int height)
    {
        ArgumentNullException.ThrowIfNull(window);

        if(!GetWindowRect(HandleOf(window), out NativeRect rect))
        {
            x = 0;
            y = 0;
            width = 0;
            height = 0;

            return;
        }

        x = rect.Left;
        y = rect.Top;
        width = rect.Right - rect.Left;
        height = rect.Bottom - rect.Top;
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
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint windowHandle, int attribute, ref int value, int size);
}
