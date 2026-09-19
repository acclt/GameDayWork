using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace GameOrchestrator.Views;

internal sealed class BlackoutOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WmSetCursor = 0x0020;
    private const long WsExTopmost = 0x00000008L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly nint HwndTopmost = new(-1);

    private readonly Forms.Screen _screen;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private nint _handle;
    private HwndSource? _source;

    public BlackoutOverlayWindow(Forms.Screen screen)
    {
        _screen = screen;
        Title = "GameDayWork Blackout";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Topmost = true;
        Background = Brushes.Black;
        Cursor = Cursors.None;
        ForceCursor = true;
        AllowsTransparency = false;
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
    }

    public nint Handle => _handle;
    public Task ClosedTask => _closed.Task;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WindowProc);
        SetCursor(nint.Zero);
        var style = GetWindowLongPtr(_handle, GwlExStyle).ToInt64();
        style |= WsExTopmost | WsExNoActivate | WsExToolWindow;
        SetWindowLongPtr(_handle, GwlExStyle, new nint(style));
        PlaceOverMonitor();
    }

    public void PlaceOverMonitor()
    {
        if (_handle == nint.Zero) return;
        var bounds = _screen.Bounds;
        SetWindowPos(_handle, HwndTopmost, bounds.X, bounds.Y, bounds.Width, bounds.Height, SwpNoActivate | SwpShowWindow);
    }

    public bool EnsureTopmost()
    {
        if (_handle == nint.Zero) return false;
        return SetWindowPos(
            _handle,
            HwndTopmost,
            0,
            0,
            0,
            0,
            SwpNoSize | SwpNoMove | SwpNoActivate | SwpShowWindow);
    }

    private nint WindowProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != WmSetCursor) return nint.Zero;

        SetCursor(nint.Zero);
        handled = true;
        return new nint(1);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _source?.RemoveHook(WindowProc);
        _source = null;
        _closed.TrySetResult();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr64(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(nint hWnd, int nIndex);

    private static nint GetWindowLongPtr(nint hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new nint(GetWindowLong32(hWnd, nIndex));

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(nint hWnd, int nIndex, nint newLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(nint hWnd, int nIndex, int newLong);

    private static nint SetWindowLongPtr(nint hWnd, int nIndex, nint newLong) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, newLong) : new nint(SetWindowLong32(hWnd, nIndex, newLong.ToInt32()));

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern nint SetCursor(nint cursor);

}
