using GameOrchestrator.Events;
using GameOrchestrator.Infrastructure;
using GameOrchestrator.Models;

namespace GameOrchestrator.Services;

public sealed class TaskQueueService(TaskRunnerService runner, LoggingService log, TaskEventBus events) : ObservableObject
{
    private QueueRunStatus _status = QueueRunStatus.Idle;
    private CancellationTokenSource? _cts;
    public QueueRunStatus Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsRunning => Status is QueueRunStatus.Running or QueueRunStatus.Stopping;
    public async Task RunAsync(IEnumerable<AutomationTaskConfig> source, int intervalSeconds, FailurePolicy policy)
    {
        if (IsRunning) return;
        _cts = new(); Status = QueueRunStatus.Running; events.Publish(new QueueStartedEvent());
        var tasks = source.Where(t => t.Enabled).ToList(); foreach (var t in tasks) t.Status = TaskRunStatus.Waiting;
        await log.WriteAsync(LogLevel.Info, $"开始执行任务队列（{tasks.Count} 项）");
        try
        {
            foreach (var task in tasks)
            {
                if (_cts.IsCancellationRequested) break;
                var retry = 0;
                while (true)
                {
                    var result = await runner.RunAsync(task, _cts.Token);
                    if (_cts.IsCancellationRequested || result.Success) break;
                    if (policy == FailurePolicy.RetryCurrentTask && retry++ < 1) { await log.WriteAsync(LogLevel.Warning, $"重试任务 {task.Name}"); continue; }
                    if (policy == FailurePolicy.StopQueue) { Status = QueueRunStatus.Failed; events.Publish(new QueueFailedEvent(result.Error ?? "任务失败")); return; }
                    if (policy == FailurePolicy.SkipCurrentTask) task.Status = TaskRunStatus.Skipped;
                    break;
                }
                if (!_cts.IsCancellationRequested && task != tasks.Last()) await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, intervalSeconds)), _cts.Token);
            }
            if (_cts.IsCancellationRequested) { foreach (var t in tasks.Where(x => x.Status == TaskRunStatus.Waiting)) t.Status = TaskRunStatus.Stopped; Status = QueueRunStatus.Idle; await log.WriteAsync(LogLevel.Warning, "任务队列已停止"); }
            else { Status = QueueRunStatus.Completed; events.Publish(new QueueCompletedEvent()); await log.WriteAsync(LogLevel.Success, "任务队列全部完成"); }
        }
        catch (OperationCanceledException) { Status = QueueRunStatus.Idle; await log.WriteAsync(LogLevel.Warning, "任务队列已停止"); }
        catch (Exception ex) { Status = QueueRunStatus.Failed; events.Publish(new QueueFailedEvent(ex.Message)); await log.WriteAsync(LogLevel.Error, $"队列失败：{ex.Message}"); }
        finally { _cts.Dispose(); _cts = null; }
    }
    public void Stop() { if (!IsRunning) return; Status = QueueRunStatus.Stopping; _cts?.Cancel(); }
}
