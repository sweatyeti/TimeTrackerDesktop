using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace TimeTrackerDesktop.Platform.Windows;

/// <summary>
/// The tray icon the widget needs (plan Task 3.1, step 3).
///
/// An abstraction because the plan's gate is about behaviour, and because a tray icon is the one piece of a
/// desktop app that has no first-class WinUI 3 support: whatever backs this is a platform decision, not a
/// UI decision.
/// </summary>
public interface ITrayIconService : IDisposable
{
    /// <summary>Creates the icon. Called once, with the window that owns it.</summary>
    void Show(Window window, string tooltip);

    /// <summary>Updates the hover text without recreating the icon.</summary>
    void UpdateTooltip(string tooltip);

    /// <summary>Raised on a left click: the caller shows and focuses the widget.</summary>
    event EventHandler? LeftClicked;

    /// <summary>Raised on a right click: the caller opens the context menu.</summary>
    event EventHandler? RightClicked;
}

/// <summary>
/// A tray icon implemented with <c>Shell_NotifyIcon</c> directly (plan Task 3.1).
///
/// No package dependency: the spike's point is to establish what the platform itself requires, and a
/// third-party icon library would hide exactly the constraints this task exists to find. The chosen APIs and
/// their caveats are recorded in <c>docs/DECISIONS.md</c>.
///
/// The window procedure is subclassed so the shell's callback message reaches us; that hook is removed on
/// dispose, and the icon is removed before the hook, so a disposed icon can never call into a dead window.
/// </summary>
public sealed class TrayIconService : ITrayIconService
{
    private const int WM_TRAYICON = 0x0400 + 1;   // WM_APP + 1
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_DESTROY = 0x0002;

    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;

    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;

    private const int GWLP_WNDPROC = -4;
    private const int IDI_APPLICATION = 32512;

    private readonly uint _iconId = 1;
    private readonly WindowProcedure _hook;   // kept alive: a collected delegate would crash the hook

    private nint _windowHandle;
    private nint _previousProcedure;
    private bool _disposed;

    public TrayIconService()
    {
        _hook = WindowProcedureHook;
    }

    public event EventHandler? LeftClicked;

    public event EventHandler? RightClicked;

    public void Show(Window window, string tooltip)
    {
        ArgumentNullException.ThrowIfNull(window);
        ObjectDisposedException.ThrowIf(_disposed, this);

        _windowHandle = WindowNative.GetWindowHandle(window);
        _previousProcedure = SetWindowLongPtr(_windowHandle, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_hook));

        NotifyIconData data = CreateData(tooltip);

        if(!Shell_NotifyIcon(NIM_ADD, ref data))
        {
            // put the window procedure back: a hook with no icon to justify it is just a leak
            SetWindowLongPtr(_windowHandle, GWLP_WNDPROC, _previousProcedure);
            _previousProcedure = 0;

            throw new InvalidOperationException("The tray icon could not be created.");
        }
    }

    public void UpdateTooltip(string tooltip)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if(_windowHandle == 0)
        {
            return;
        }

        NotifyIconData data = CreateData(tooltip);

        _ = Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    public void Dispose()
    {
        if(_disposed)
        {
            return;
        }

        _disposed = true;

        if(_windowHandle != 0)
        {
            // icon first, then the hook: the shell must not be able to call a procedure we are removing
            NotifyIconData data = CreateData(string.Empty);
            _ = Shell_NotifyIcon(NIM_DELETE, ref data);

            if(_previousProcedure != 0)
            {
                SetWindowLongPtr(_windowHandle, GWLP_WNDPROC, _previousProcedure);
                _previousProcedure = 0;
            }
        }
    }

    private nint WindowProcedureHook(nint windowHandle, uint message, nint wParam, nint lParam)
    {
        if(message == WM_TRAYICON)
        {
            int mouseMessage = (int)(lParam & 0xFFFF);

            if(mouseMessage == WM_LBUTTONUP)
            {
                LeftClicked?.Invoke(this, EventArgs.Empty);
                return 0;
            }

            if(mouseMessage == WM_RBUTTONUP)
            {
                RightClicked?.Invoke(this, EventArgs.Empty);
                return 0;
            }
        }

        return CallWindowProc(_previousProcedure, windowHandle, message, wParam, lParam);
    }

    private NotifyIconData CreateData(string tooltip) => new()
    {
        Size = Marshal.SizeOf<NotifyIconData>(),
        WindowHandle = _windowHandle,
        Id = _iconId,
        Flags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
        CallbackMessage = WM_TRAYICON,

        // a stock icon for the spike: the shipped icon arrives with packaging, and a missing icon would
        // make the icon invisible rather than obviously absent
        Icon = LoadIcon(0, IDI_APPLICATION),
        Tip = tooltip,
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size;
        public nint WindowHandle;
        public uint Id;
        public int Flags;
        public int CallbackMessage;
        public nint Icon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;
    }

    private delegate nint WindowProcedure(nint windowHandle, uint message, nint wParam, nint lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint windowHandle, int index, nint newValue);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProc(nint previousProcedure, nint windowHandle, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint LoadIcon(nint instance, int iconName);
}
