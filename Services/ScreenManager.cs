using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using GameOrchestrator.Models;
using GameOrchestrator.Views;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace GameOrchestrator.Services;

public sealed class ScreenManager : IAsyncDisposable
{
    private static readonly TimeSpan VisualStabilityDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan ZOrderGuardInterval = TimeSpan.FromSeconds(2);
    private const uint EventSystemForeground = 0x0003;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectReorder = 0x8004;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint WineventOutofcontext = 0x0000;
    private const uint WineventSkipownprocess = 0x0002;
    private const uint GwHwndprev = 3;
    private const uint GaRoot = 2;
    private readonly LoggingService _log;
    private readonly BrightnessManager _brightness;
    private readonly AudioOutputMonitor _audioOutput;
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private readonly List<BlackoutOverlayWindow> _overlays = [];
    private readonly System.Windows.Threading.DispatcherTimer _inputTimer;
    private readonly System.Windows.Threading.DispatcherTimer _zOrderGuardTimer;
    private readonly WinEventDelegate _winEventCallback;
    private readonly List<nint> _winEventHooks = [];
    private bool _disposed;
    private bool _inputTickRunning;
    private bool _monitoringStarted;
    private bool _automaticBlackout;
    private uint _blackoutInputTick;
    private uint _idleMonitoringSinceTick;
    private DateTimeOffset _readyAfter = DateTimeOffset.MinValue;
    private int _cursorHideAdjustments;
    private int _zOrderRepairQueued;
    private DateTimeOffset _lastZOrderRepairLog = DateTimeOffset.MinValue;

    public ScreenManagerState State { get; private set; } = ScreenManagerState.IdleMonitoring;
    public bool Enabled { get; set; } = true;
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(30);
    public event Action<ScreenManagerState>? StateChanged;

    public ScreenManager(LoggingService log, BrightnessManager brightness)
    {
        _log = log;
        _brightness = brightness;
        _audioOutput = new AudioOutputMonitor(log);
        _inputTimer = new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(500),
            System.Windows.Threading.DispatcherPriority.Background,
            async (_, _) => await CheckInputAsync(),
            Application.Current.Dispatcher);
        _zOrderGuardTimer = new System.Windows.Threading.DispatcherTimer(
            ZOrderGuardInterval,
            System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => EnsureOverlayZOrder(),
            Application.Current.Dispatcher);
        _winEventCallback = WinEventCallback;
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
    }

    public Task RecoverDisplayStateAsync(CancellationToken token = default) => _brightness.RecoverPendingAsync(token);

    public void StartMonitoring()
    {
        ThrowIfDisposed();
        if (_monitoringStarted) return;
        _monitoringStarted = true;
        _idleMonitoringSinceTick = GetLastInputTick();
        _audioOutput.Start();
        _inputTimer.Start();
    }

    public Task<bool> EnterBlackoutAsync(CancellationToken token = default) => EnterBlackoutAsync(false, token);

    private async Task<bool> EnterBlackoutAsync(bool automatic, CancellationToken token)
    {
        await _transitionGate.WaitAsync(token);
        try
        {
            ThrowIfDisposed();
            if (State == ScreenManagerState.Blackout)
            {
                if (!automatic) _automaticBlackout = false;
                return true;
            }
            if (!Enabled)
            {
                await _log.WriteAsync(LogLevel.Warning, "Screen Manager 已禁用，未进入假息屏");
                return false;
            }
            if (State is ScreenManagerState.PreparingTask or ScreenManagerState.RunningTask)
            {
                await _log.WriteAsync(LogLevel.Warning, "任务准备或运行期间禁止进入假息屏");
                return false;
            }

            await CenterCursorBeforeBlackoutAsync();
            await _brightness.DimForBlackoutAsync(token);
            await ShowOverlaysAsync();
            _blackoutInputTick = GetLastInputTick();
            _automaticBlackout = automatic;
            await SetStateAsync(ScreenManagerState.Blackout, "已进入假息屏");
            return true;
        }
        catch
        {
            _automaticBlackout = false;
            await CloseOverlaysCoreAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public async Task ExitBlackoutAsync(CancellationToken token = default)
    {
        await _transitionGate.WaitAsync(token);
        try
        {
            ThrowIfDisposed();
            await CloseOverlaysCoreAsync(token);
            if (State == ScreenManagerState.Blackout)
                await SetStateAsync(ScreenManagerState.IdleMonitoring, "已退出假息屏");
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public async Task PrepareForTaskAsync(CancellationToken token = default)
    {
        await _transitionGate.WaitAsync(token);
        try
        {
            ThrowIfDisposed();
            if (State == ScreenManagerState.RunningTask) throw new InvalidOperationException("已有任务正在运行");
            await SetStateAsync(ScreenManagerState.PreparingTask, "正在准备任务，退出假息屏");
            await CloseOverlaysCoreAsync(token);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public async Task WaitUntilReadyAsync(CancellationToken token = default)
    {
        await _transitionGate.WaitAsync(token);
        try
        {
            ThrowIfDisposed();
            await CloseOverlaysCoreAsync(token);
            var remaining = _readyAfter - DateTimeOffset.Now;
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public async Task BeginTaskAsync(CancellationToken token = default)
    {
        await _transitionGate.WaitAsync(token);
        try
        {
            ThrowIfDisposed();
            if (_overlays.Count > 0 || State != ScreenManagerState.PreparingTask)
                throw new InvalidOperationException("屏幕尚未完成任务启动准备");
            await SetStateAsync(ScreenManagerState.RunningTask, "任务链开始执行");
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public async Task CompleteTaskChainAsync(CancellationToken token = default)
    {
        await _transitionGate.WaitAsync(token);
        try
        {
            ThrowIfDisposed();
            await SetStateAsync(ScreenManagerState.IdleMonitoring, "任务链结束，恢复空闲监控并重新计算空闲时间");
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    private Task ShowOverlaysAsync()
    {
        if (_overlays.Count > 0) return Task.CompletedTask;
        foreach (var screen in Forms.Screen.AllScreens)
        {
            var overlay = new BlackoutOverlayWindow(screen);
            overlay.Show();
            overlay.PlaceOverMonitor();
            _overlays.Add(overlay);
        }
        HideCursorCore();
        StartZOrderGuard();
        return Task.CompletedTask;
    }

    private async Task CenterCursorBeforeBlackoutAsync()
    {
        var screen = Forms.Screen.PrimaryScreen
            ?? throw new InvalidOperationException("未找到主显示器，无法将鼠标移动到安全位置");
        var bounds = screen.Bounds;
        var x = bounds.Left + bounds.Width / 2;
        var y = bounds.Top + bounds.Height / 2;

        if (!SetCursorPos(x, y))
            throw new InvalidOperationException($"无法将鼠标移动到主屏中心，Win32 错误码：{Marshal.GetLastWin32Error()}");

        await _log.WriteAsync(LogLevel.Info, $"进入假息屏前已将鼠标移动到主屏中心：({x}, {y})");
    }

    private void StartZOrderGuard()
    {
        StopZOrderGuard();
        AddWinEventHook(EventSystemForeground, EventSystemForeground);
        AddWinEventHook(EventObjectShow, EventObjectReorder);
        AddWinEventHook(EventObjectLocationChange, EventObjectLocationChange);
        _zOrderGuardTimer.Start();
        EnsureOverlayZOrder(force: true);
    }

    private void AddWinEventHook(uint eventMin, uint eventMax)
    {
        var hook = SetWinEventHook(
            eventMin,
            eventMax,
            nint.Zero,
            _winEventCallback,
            0,
            0,
            WineventOutofcontext | WineventSkipownprocess);
        if (hook != nint.Zero) _winEventHooks.Add(hook);
        else _ = _log.WriteAsync(LogLevel.Warning, $"任务栏层级事件监听注册失败：0x{eventMin:X}–0x{eventMax:X}");
    }

    private void StopZOrderGuard()
    {
        _zOrderGuardTimer.Stop();
        foreach (var hook in _winEventHooks) UnhookWinEvent(hook);
        _winEventHooks.Clear();
        Interlocked.Exchange(ref _zOrderRepairQueued, 0);
    }

    private void WinEventCallback(
        nint hook,
        uint eventType,
        nint hwnd,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        if (_disposed || _overlays.Count == 0) return;
        if (eventType != EventSystemForeground && !IsTaskbarWindow(hwnd)) return;
        QueueOverlayZOrderCheck();
    }

    private void QueueOverlayZOrderCheck()
    {
        if (Interlocked.Exchange(ref _zOrderRepairQueued, 1) != 0) return;
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            try { EnsureOverlayZOrder(); }
            finally { Interlocked.Exchange(ref _zOrderRepairQueued, 0); }
        });
    }

    private void EnsureOverlayZOrder(bool force = false)
    {
        if (_disposed || _overlays.Count == 0) return;
        if (!force && !_overlays.Any(HasTaskbarAbove)) return;

        var repaired = false;
        foreach (var overlay in _overlays) repaired |= overlay.EnsureTopmost();
        if (!repaired || force || DateTimeOffset.Now - _lastZOrderRepairLog < TimeSpan.FromSeconds(5)) return;

        _lastZOrderRepairLog = DateTimeOffset.Now;
        _ = _log.WriteAsync(LogLevel.Info, "检测到任务栏位于假息屏遮罩上方，已恢复遮罩层级");
    }

    private static bool HasTaskbarAbove(BlackoutOverlayWindow overlay)
    {
        var current = GetWindow(overlay.Handle, GwHwndprev);
        for (var index = 0; current != nint.Zero && index < 512; index++)
        {
            if (IsTaskbarWindow(current)) return true;
            current = GetWindow(current, GwHwndprev);
        }
        return false;
    }

    private static bool IsTaskbarWindow(nint hwnd)
    {
        if (hwnd == nint.Zero) return false;
        var root = GetAncestor(hwnd, GaRoot);
        if (root == nint.Zero) root = hwnd;

        var className = new StringBuilder(64);
        var length = GetClassName(root, className, className.Capacity);
        if (length <= 0) return false;
        var value = className.ToString();
        return value.Equals("Shell_TrayWnd", StringComparison.Ordinal)
            || value.Equals("Shell_SecondaryTrayWnd", StringComparison.Ordinal);
    }

    private async Task CloseOverlaysCoreAsync(
        CancellationToken token,
        bool waitForVisualStability = true,
        bool restoreDisplayState = true)
    {
        StopZOrderGuard();
        if (restoreDisplayState) await _brightness.RestoreAsync(CancellationToken.None);

        if (_overlays.Count == 0)
        {
            if (restoreDisplayState) RestoreCursorCore();
            if (!waitForVisualStability) return;
            var existingDelay = _readyAfter - DateTimeOffset.Now;
            if (existingDelay > TimeSpan.Zero) await Task.Delay(existingDelay, token);
            return;
        }

        var closing = _overlays.ToArray();
        _overlays.Clear();
        try
        {
            foreach (var overlay in closing) overlay.Close();
            await Task.WhenAll(closing.Select(window => window.ClosedTask)).WaitAsync(token);

            foreach (var overlay in closing)
            {
                while (overlay.Handle != nint.Zero && IsWindow(overlay.Handle))
                    await Task.Delay(10, token);
            }
        }
        finally
        {
            if (restoreDisplayState) RestoreCursorCore();
        }

        if (!waitForVisualStability) return;
        try { DwmFlush(); } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
        _readyAfter = DateTimeOffset.Now + VisualStabilityDelay;
        await Task.Delay(VisualStabilityDelay, token);
    }

    private async Task SetStateAsync(ScreenManagerState state, string logMessage)
    {
        if (State == state) return;
        var previous = State;
        State = state;
        if (state != ScreenManagerState.Blackout) _automaticBlackout = false;
        if (state == ScreenManagerState.IdleMonitoring) _idleMonitoringSinceTick = GetTickCount();
        StateChanged?.Invoke(state);
        await _log.WriteAsync(LogLevel.Info, $"屏幕状态：{previous} → {state}；{logMessage}");
    }

    private async Task CheckInputAsync()
    {
        if (_disposed || !_monitoringStarted || _inputTickRunning) return;
        _inputTickRunning = true;
        try
        {
            if (!Enabled)
            {
                if (State == ScreenManagerState.Blackout) await ExitBlackoutAsync();
                return;
            }

            var lastInputTick = GetLastInputTick();
            if (State == ScreenManagerState.Blackout)
            {
                if (lastInputTick != _blackoutInputTick)
                {
                    await _log.WriteAsync(LogLevel.Info, "检测到鼠标或键盘输入，恢复显示");
                    await ExitBlackoutAsync();
                }
                else if (_automaticBlackout && _audioOutput.HasRecentOutput)
                {
                    await _log.WriteAsync(LogLevel.Info, "检测到音频输出，恢复显示");
                    await ExitBlackoutAsync();
                }
                return;
            }

            if (State != ScreenManagerState.IdleMonitoring) return;
            if (_audioOutput.HasRecentOutput)
            {
                _idleMonitoringSinceTick = GetTickCount();
                return;
            }
            var timeoutMilliseconds = (uint)Math.Clamp(IdleTimeout.TotalMilliseconds, 1_000d, uint.MaxValue);
            if (ElapsedSince(lastInputTick) >= timeoutMilliseconds && ElapsedSince(_idleMonitoringSinceTick) >= timeoutMilliseconds)
                await EnterBlackoutAsync(true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await _log.WriteAsync(LogLevel.Error, $"空闲输入检测失败：{ex.Message}");
        }
        finally
        {
            _inputTickRunning = false;
        }
    }

    private static uint GetLastInputTick()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? info.Time : GetTickCount();
    }

    private static uint ElapsedSince(uint tick) => unchecked(GetTickCount() - tick);

    private void DisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_disposed || State != ScreenManagerState.Blackout) return;
        _ = Application.Current.Dispatcher.InvokeAsync(RebuildOverlaysAfterDisplayChangeAsync).Task.Unwrap();
    }

    private async Task RebuildOverlaysAfterDisplayChangeAsync()
    {
        await _transitionGate.WaitAsync();
        try
        {
            if (_disposed || State != ScreenManagerState.Blackout) return;
            await CloseOverlaysCoreAsync(CancellationToken.None, waitForVisualStability: false, restoreDisplayState: false);
            await _brightness.DimForBlackoutAsync(CancellationToken.None);
            await ShowOverlaysAsync();
            await _log.WriteAsync(LogLevel.Info, $"显示器配置变化，已重建 {_overlays.Count} 个假息屏窗口");
        }
        catch (Exception ex)
        {
            await CloseOverlaysCoreAsync(CancellationToken.None, waitForVisualStability: false);
            if (!_disposed && State == ScreenManagerState.Blackout)
                await SetStateAsync(ScreenManagerState.IdleMonitoring, "显示器配置变化后重建假息屏失败，已恢复显示");
            await _log.WriteAsync(LogLevel.Error, $"重建假息屏窗口失败：{ex.Message}");
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _inputTimer.Stop();
        StopZOrderGuard();
        SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
        await _transitionGate.WaitAsync();
        try { await CloseOverlaysCoreAsync(CancellationToken.None, waitForVisualStability: false); }
        finally { _transitionGate.Release(); _transitionGate.Dispose(); }
        await _audioOutput.DisposeAsync();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo lastInputInfo);

    [DllImport("kernel32.dll")]
    private static extern uint GetTickCount();

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmFlush();

    private delegate void WinEventDelegate(
        nint hook,
        uint eventType,
        nint hwnd,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint eventHookModule,
        WinEventDelegate callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hwnd, uint command);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, StringBuilder className, int maxCount);

    private void HideCursorCore()
    {
        if (_cursorHideAdjustments > 0) return;

        var adjustments = 0;
        int displayCount;
        do
        {
            displayCount = ShowCursor(false);
            adjustments++;
        }
        while (displayCount >= 0 && adjustments < 64);

        _cursorHideAdjustments = adjustments;
        SetCursor(nint.Zero);
    }

    private void RestoreCursorCore()
    {
        if (_cursorHideAdjustments == 0) return;
        for (var index = 0; index < _cursorHideAdjustments; index++) ShowCursor(true);
        _cursorHideAdjustments = 0;

        var arrow = LoadCursor(nint.Zero, new nint(32512));
        if (arrow != nint.Zero) SetCursor(arrow);
    }

    [DllImport("user32.dll")]
    private static extern int ShowCursor([MarshalAs(UnmanagedType.Bool)] bool show);

    [DllImport("user32.dll")]
    private static extern nint SetCursor(nint cursor);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", EntryPoint = "LoadCursorW")]
    private static extern nint LoadCursor(nint instance, nint cursorName);
}
