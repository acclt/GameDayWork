using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace GameOrchestrator.Views;

internal sealed class BlackoutOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExTopmost = 0x00000008L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly nint HwndTopmost = new(-1);

    private readonly Forms.Screen _screen;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private nint _handle;

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
        AllowsTransparency = false;
        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) => _closed.TrySetResult();
    }

    public nint Handle => _handle;
    public Task ClosedTask => _closed.Task;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
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
}
