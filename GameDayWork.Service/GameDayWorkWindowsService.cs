using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using GameOrchestrator.Services;

internal sealed class GameDayWorkWindowsService : ServiceBase
{
    private readonly ConcurrentDictionary<int, SessionSupervisor> _sessions = new();
    private Timer? _pollTimer;
    private readonly LockScreenPowerPolicy _powerPolicy = new(new WindowsLockScreenPowerApi());
    private ServiceControlPipe? _controlPipe;

    public GameDayWorkWindowsService()
    {
        ServiceName = GameDayWorkServiceManager.ServiceName;
        CanHandleSessionChangeEvent = true;
        CanHandlePowerEvent = true;
        AutoLog = false;
    }

    protected override void OnStart(string[] args)
    {
        ApplyPowerPolicy();
        _controlPipe = new ServiceControlPipe(MarkIntentionalExit);
        _controlPipe.Start();
        foreach (var sessionId in InteractiveSessionLauncher.GetActiveSessionIds()) EnsureDesktop(sessionId, afterLogin: true, afterUnlock: false);
        _pollTimer = new Timer(_ => PollSessions(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        ServiceFileLog.Write("服务已启动。");
    }

    protected override void OnStop()
    {
        _pollTimer?.Dispose();
        _controlPipe?.Dispose();
        foreach (var supervisor in _sessions.Values) supervisor.Dispose();
        _sessions.Clear();
        ServiceFileLog.Write("服务已停止；桌面端保持独立运行。");
    }

    protected override void OnSessionChange(SessionChangeDescription changeDescription)
    {
        base.OnSessionChange(changeDescription);
        var sessionId = changeDescription.SessionId;
        switch (changeDescription.Reason)
        {
            case SessionChangeReason.SessionLogon:
                _sessions.TryRemove(sessionId, out var old);
                old?.Dispose();
                EnsureDesktop(sessionId, afterLogin: true, afterUnlock: false);
                break;
            case SessionChangeReason.SessionUnlock:
                EnsureDesktop(sessionId, afterLogin: false, afterUnlock: true);
                _ = SendDesktopCommandAsync(sessionId, "blackout-unlock");
                break;
            case SessionChangeReason.SessionLogoff:
                if (_sessions.TryRemove(sessionId, out var supervisor)) supervisor.Dispose();
                break;
        }
    }

    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        if (powerStatus is PowerBroadcastStatus.PowerStatusChange or PowerBroadcastStatus.ResumeAutomatic or PowerBroadcastStatus.ResumeSuspend)
            ApplyPowerPolicy();
        return true;
    }

    private void PollSessions()
    {
        try
        {
            ApplyPowerPolicy();
            foreach (var sessionId in InteractiveSessionLauncher.GetActiveSessionIds()) EnsureDesktop(sessionId, afterLogin: false, afterUnlock: false);
        }
        catch (Exception ex) { ServiceFileLog.Write($"轮询会话失败：{ex.Message}"); }
    }

    private void EnsureDesktop(int sessionId, bool afterLogin, bool afterUnlock)
    {
        var supervisor = _sessions.GetOrAdd(sessionId, id => new SessionSupervisor(id));
        supervisor.EnsureRunning(afterLogin, afterUnlock);
    }

    private void MarkIntentionalExit(int sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var supervisor)) supervisor.MarkIntentionalExit();
    }

    private void ApplyPowerPolicy()
    {
        try
        {
            var config = MachineServiceConfigStore.Load();
            if (!config.LockScreenTimeoutEnabled) return;
            if (_powerPolicy.ApplyActiveScheme(config)) MachineServiceConfigStore.Save(config);
        }
        catch (Exception ex) { ServiceFileLog.Write($"应用登录/锁屏息屏策略失败：{ex.Message}"); }
    }

    private static async Task SendDesktopCommandAsync(int sessionId, string command)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", $"GameDayWork.Desktop.{sessionId}", PipeDirection.Out, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(1500);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            await writer.WriteLineAsync(command);
        }
        catch (Exception ex) { ServiceFileLog.Write($"向会话 {sessionId} 发送 {command} 失败：{ex.Message}"); }
    }
}

internal sealed class SessionSupervisor(int sessionId) : IDisposable
{
    public const int IntentionalExitCode = 77;
    private readonly object _gate = new();
    private readonly RestartPolicy _restartPolicy = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Process? _process;
    private bool _intentionalExit;
    private bool _launching;

    public void EnsureRunning(bool afterLogin, bool afterUnlock)
    {
        lock (_gate)
        {
            if (_intentionalExit || _launching || _process is { HasExited: false }) return;
            _launching = true;
        }
        _ = LaunchAndWatchAsync(afterLogin, afterUnlock);
    }

    private async Task LaunchAndWatchAsync(bool afterLogin, bool afterUnlock)
    {
        var handedOff = false;
        try
        {
            var config = MachineServiceConfigStore.Load();
            if (!File.Exists(config.DesktopExecutablePath))
            {
                ServiceFileLog.Write($"会话 {sessionId} 的桌面端路径不存在：{config.DesktopExecutablePath}");
                return;
            }
            var arguments = $"--service-managed --session-id={sessionId}" + (afterLogin ? " --after-login" : "") + (afterUnlock ? " --after-unlock" : "");
            var process = FindExistingDesktop() ?? InteractiveSessionLauncher.Start(sessionId, config.DesktopExecutablePath, arguments);
            lock (_gate) _process = process;
            ServiceFileLog.Write($"已在会话 {sessionId} 启动桌面端 PID {process.Id}。");
            await process.WaitForExitAsync(_shutdown.Token);
            var exitCode = process.ExitCode;
            process.Dispose();
            lock (_gate) _process = null;
            bool intentional;
            lock (_gate) intentional = _intentionalExit;
            if (intentional || exitCode == IntentionalExitCode)
            {
                lock (_gate) _intentionalExit = true;
                ServiceFileLog.Write($"会话 {sessionId} 用户明确退出，不再拉起。");
                return;
            }
            var delay = _restartPolicy.RegisterUnexpectedExit(DateTimeOffset.Now);
            if (delay is null)
            {
                ServiceFileLog.Write($"会话 {sessionId} 在 10 分钟内异常退出过多，停止恢复以避免崩溃循环。");
                return;
            }
            ServiceFileLog.Write($"会话 {sessionId} 桌面端异常退出（{exitCode}），{delay.Value.TotalSeconds:0} 秒后恢复。");
            await Task.Delay(delay.Value, _shutdown.Token);
            lock (_gate) { _launching = false; handedOff = true; }
            EnsureRunning(afterLogin: false, afterUnlock: false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ServiceFileLog.Write($"会话 {sessionId} 桌面端保活失败：{ex.Message}"); }
        finally { if (!handedOff) lock (_gate) _launching = false; }
    }

    public void MarkIntentionalExit()
    {
        lock (_gate) _intentionalExit = true;
        ServiceFileLog.Write($"会话 {sessionId} 收到用户明确退出信号，不再拉起。");
    }

    private Process? FindExistingDesktop()
    {
        foreach (var candidate in Process.GetProcessesByName("GameDayWork"))
        {
            try
            {
                if (candidate.SessionId == sessionId && !candidate.HasExited) return candidate;
            }
            catch { }
            candidate.Dispose();
        }
        return null;
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _process?.Dispose();
        _shutdown.Dispose();
    }
}

internal sealed class ServiceControlPipe(Action<int> intentionalExit) : IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _task;

    public void Start() => _task ??= RunAsync();

    private async Task RunAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                var security = new PipeSecurity();
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                    PipeAccessRights.ReadWrite, AccessControlType.Allow));
                await using var pipe = NamedPipeServerStreamAcl.Create("GameDayWork.Service.Control", PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
                await pipe.WaitForConnectionAsync(_shutdown.Token);
                using var reader = new StreamReader(pipe);
                var command = await reader.ReadLineAsync(_shutdown.Token);
                if (command?.Split(' ', StringSplitOptions.RemoveEmptyEntries) is ["intentional-exit", var session]
                    && int.TryParse(session, out var sessionId)
                    && GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientPid)
                    && IsProcessInSession(clientPid, sessionId)) intentionalExit(sessionId);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                ServiceFileLog.Write($"服务控制管道失败：{ex.Message}");
                try { await Task.Delay(1000, _shutdown.Token); } catch (OperationCanceledException) { break; }
            }
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        try { _task?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _shutdown.Dispose();
    }

    private static bool IsProcessInSession(uint processId, int sessionId)
    {
        try { using var process = Process.GetProcessById((int)processId); return process.SessionId == sessionId; }
        catch { return false; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);
}

internal static class ServiceFileLog
{
    private static readonly object Gate = new();
    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(MachineServiceConfigStore.DirectoryPath);
                File.AppendAllText(Path.Combine(MachineServiceConfigStore.DirectoryPath, "service.log"), $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
