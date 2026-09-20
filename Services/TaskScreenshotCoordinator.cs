using System.Collections.Concurrent;
using GameOrchestrator.Events;
using GameOrchestrator.Models;

namespace GameOrchestrator.Services;

public sealed class TaskScreenshotCoordinator : IAsyncDisposable
{
    private sealed record PendingCapture(CancellationTokenSource Cancellation, Task Task);
    private readonly IScreenshotService _screenshots;
    private readonly WeComNotificationService _notifications;
    private readonly Func<NotificationConfig> _configProvider;
    private readonly LoggingService _log;
    private readonly Func<CancellationToken, Task> _prepareForCapture;
    private readonly ConcurrentDictionary<Guid, PendingCapture> _pending = new();
    private readonly CancellationTokenSource _shutdown = new();

    public TaskScreenshotCoordinator(
        TaskEventBus events,
        IScreenshotService screenshots,
        WeComNotificationService notifications,
        Func<NotificationConfig> configProvider,
        LoggingService log,
        Func<CancellationToken, Task>? prepareForCapture = null)
    {
        _screenshots = screenshots;
        _notifications = notifications;
        _configProvider = configProvider;
        _log = log;
        _prepareForCapture = prepareForCapture ?? (_ => Task.CompletedTask);
        events.Subscribe<TaskStartedEvent>(message => ScheduleRunningCapture(message.Session));
        events.Subscribe<TaskCompletionDetectedEvent>(message => CancelRunningCapture(message.Session.SessionId));
        events.Subscribe<TaskCleanupStartedEvent>(message => CancelRunningCapture(message.Session.SessionId));
        events.Subscribe<TaskCompletedEvent>(message => CancelRunningCapture(message.Session.SessionId));
        events.Subscribe<TaskFailedEvent>(message => CancelRunningCapture(message.Session.SessionId));
        events.Subscribe<TaskTimedOutEvent>(message => CancelRunningCapture(message.Session.SessionId));
        events.Subscribe<TaskStoppedEvent>(message => CancelRunningCapture(message.Session.SessionId));
    }

    private void ScheduleRunningCapture(RuntimeSession session)
    {
        var config = _configProvider();
        if (!CanCapture(config)) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        var task = CaptureAfterDelayAsync(session, Math.Clamp(config.RunningScreenshotDelaySeconds, 1, 3600), cancellation.Token);
        var pending = new PendingCapture(cancellation, task);
        if (!_pending.TryAdd(session.SessionId, pending))
        {
            cancellation.Cancel();
            cancellation.Dispose();
            return;
        }
        _ = ObserveAsync(session.SessionId, pending);
    }

    private async Task CaptureAfterDelayAsync(RuntimeSession session, int delaySeconds, CancellationToken token)
    {
        await Task.Delay(TimeSpan.FromSeconds(delaySeconds), token);
        token.ThrowIfCancellationRequested();
        if (!CanCapture(_configProvider())) return;
        if (session.Status != TaskRunStatus.Running) return;

        var processSnapshot = session.SnapshotTrackedProcesses();
        try
        {
            await _prepareForCapture(token);
            var screenshot = await _screenshots.CaptureForTaskAsync(session, token);
            token.ThrowIfCancellationRequested();
            _notifications.QueueRunningScreenshot(session, processSnapshot, screenshot,
                screenshot is null ? "截图服务未返回图像" : null);
            if (screenshot is not null)
                await _log.WriteAsync(LogLevel.Info, $"已生成任务运行截图：{session.TaskName}，{screenshot.Width}×{screenshot.Height}，{screenshot.Data.Length / 1024d:F1} KB");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _notifications.QueueRunningScreenshot(session, processSnapshot, null, ex.Message);
            await _log.WriteAsync(LogLevel.Error, $"任务运行截图失败（{session.TaskName}）：{ex.Message}");
        }
    }

    private async Task ObserveAsync(Guid sessionId, PendingCapture pending)
    {
        try { await pending.Task; }
        catch (OperationCanceledException) when (pending.Cancellation.IsCancellationRequested) { }
        catch (Exception ex) { await _log.WriteAsync(LogLevel.Error, $"任务运行截图调度失败：{ex.Message}"); }
        finally
        {
            _pending.TryRemove(new KeyValuePair<Guid, PendingCapture>(sessionId, pending));
            pending.Cancellation.Dispose();
        }
    }

    private void CancelRunningCapture(Guid sessionId)
    {
        if (!_pending.TryGetValue(sessionId, out var pending)) return;
        try { pending.Cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public async Task CaptureEndAsync(RuntimeSession session, CancellationToken token)
    {
        var config = _configProvider();
        if (!CanCapture(config)) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token, timeout.Token);
        try
        {
            await _prepareForCapture(linked.Token);
            var screenshot = await _screenshots.CaptureForTaskAsync(session, linked.Token);
            _notifications.QueueEndScreenshot(session, screenshot, screenshot is null ? "截图服务未返回图像" : null);
            if (screenshot is not null)
                await _log.WriteAsync(LogLevel.Info, $"已生成任务结束截图：{session.TaskName}，{screenshot.Width}×{screenshot.Height}，{screenshot.Data.Length / 1024d:F1} KB");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            _notifications.QueueEndScreenshot(session, null, "截图超时");
            await _log.WriteAsync(LogLevel.Error, $"任务结束截图超时（{session.TaskName}）");
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _notifications.QueueEndScreenshot(session, null, ex.Message);
            await _log.WriteAsync(LogLevel.Error, $"任务结束截图失败（{session.TaskName}）：{ex.Message}");
        }
    }

    private static bool CanCapture(NotificationConfig config) => config.Enabled
        && config.CaptureTaskScreenshots
        && !string.IsNullOrWhiteSpace(config.WeComWebhookUrl);

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        var pending = _pending.Values.ToArray();
        foreach (var capture in pending)
        {
            try { capture.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        try { await Task.WhenAll(pending.Select(capture => capture.Task)); }
        catch (OperationCanceledException) { }
        _shutdown.Dispose();
    }
}
