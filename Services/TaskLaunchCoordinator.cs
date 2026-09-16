using GameOrchestrator.Models;

namespace GameOrchestrator.Services;

public sealed class TaskLaunchCoordinator : IAsyncDisposable
{
    private readonly ScreenManager _screenManager;
    private readonly TaskQueueService _queue;
    private readonly LoggingService _log;
    private readonly Func<bool> _autoBlackoutAfterTask;
    private readonly SemaphoreSlim _serialGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private CancellationTokenSource? _activeRun;
    private int _pendingRuns;

    public bool IsBusy => Volatile.Read(ref _pendingRuns) > 0;
    public event Action<bool>? BusyChanged;

    public TaskLaunchCoordinator(
        ScreenManager screenManager,
        TaskQueueService queue,
        LoggingService log,
        Func<bool> autoBlackoutAfterTask)
    {
        _screenManager = screenManager;
        _queue = queue;
        _log = log;
        _autoBlackoutAfterTask = autoBlackoutAfterTask;
    }

    public Task RunSingleAsync(AutomationTaskConfig task, FailurePolicy policy, CancellationToken token = default) =>
        RunAsync([task], DateTime.Now, 0, policy, token);

    public Task RunAsync(
        IReadOnlyList<AutomationTaskConfig> tasks,
        DateTime launchAt,
        int intervalSeconds,
        FailurePolicy policy,
        CancellationToken token = default) =>
        RunCoreAsync(tasks, launchAt, intervalSeconds, policy, token);

    private async Task RunCoreAsync(
        IReadOnlyList<AutomationTaskConfig> tasks,
        DateTime launchAt,
        int intervalSeconds,
        FailurePolicy policy,
        CancellationToken token)
    {
        SetPending(1);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
        try
        {
            await _serialGate.WaitAsync(linked.Token);
            try
            {
                _activeRun = linked;
                await _screenManager.PrepareForTaskAsync(linked.Token);
                await _screenManager.WaitUntilReadyAsync(linked.Token);

                var wait = launchAt - DateTime.Now;
                if (wait > TimeSpan.Zero)
                {
                    await _log.WriteAsync(LogLevel.Info, $"屏幕已恢复，等待计划启动时间 {launchAt:HH:mm:ss}");
                    await Task.Delay(wait, linked.Token);
                }
                else if (wait < TimeSpan.FromSeconds(-1))
                {
                    await _log.WriteAsync(LogLevel.Warning, $"计划时间 {launchAt:HH:mm:ss} 已过，立即串行启动任务链");
                }

                await _screenManager.BeginTaskAsync(linked.Token);
                using var stopRegistration = linked.Token.Register(_queue.Stop);
                await _queue.RunAsync(tasks, intervalSeconds, policy);
            }
            finally
            {
                _activeRun = null;
                try
                {
                    var enterBlackout = !_shutdown.IsCancellationRequested && _screenManager.Enabled && _autoBlackoutAfterTask();
                    await _screenManager.CompleteTaskChainAsync(enterBlackout, CancellationToken.None);
                }
                finally
                {
                    _serialGate.Release();
                }
            }
        }
        finally
        {
            SetPending(-1);
        }
    }

    public void Stop()
    {
        _activeRun?.Cancel();
        _queue.Stop();
    }

    private void SetPending(int delta)
    {
        var previous = Interlocked.Add(ref _pendingRuns, delta) - delta;
        var current = previous + delta;
        if ((previous == 0) != (current == 0)) BusyChanged?.Invoke(current > 0);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        Stop();
        await _serialGate.WaitAsync();
        _serialGate.Release();
        _shutdown.Dispose();
        _serialGate.Dispose();
    }
}
