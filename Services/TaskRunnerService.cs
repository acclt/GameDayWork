using System.Diagnostics;
using GameOrchestrator.Events;
using GameOrchestrator.Models;

namespace GameOrchestrator.Services;

public sealed record TaskRunResult(RuntimeSession Session, bool Success, bool TimedOut, string? Error);

public sealed class TaskRunnerService(ProcessMonitorService monitor, ProcessCleanupService cleanup, LoggingService log, TaskEventBus events)
{
    public event Action<RuntimeSession>? SessionChanged;
    public async Task<TaskRunResult> RunAsync(AutomationTaskConfig task, CancellationToken queueToken)
    {
        var session = new RuntimeSession { TaskId = task.Id, TaskName = task.Name, Status = TaskRunStatus.Starting };
        task.Status = TaskRunStatus.Starting; events.Publish(new TaskStartingEvent(session)); SessionChanged?.Invoke(session);
        Process? root = null; JobObjectService? job = null; bool timedOut = false; string? error = null;
        try
        {
            if (!File.Exists(task.ProgramPath)) throw new FileNotFoundException("找不到任务程序", task.ProgramPath);
            var startInfo = new ProcessStartInfo(task.ProgramPath, task.Arguments) { UseShellExecute = false, WorkingDirectory = string.IsNullOrWhiteSpace(task.WorkingDirectory) ? Path.GetDirectoryName(task.ProgramPath)! : task.WorkingDirectory };
            root = Process.Start(startInfo) ?? throw new InvalidOperationException("进程启动失败");
            session.RootPid = root.Id;
            if (task.UseJobObject) { try { job = new JobObjectService($"GameOrchestrator-{session.SessionId:N}"); job.TryAssign(root); } catch (Exception ex) { await log.WriteAsync(LogLevel.Warning, $"Job Object 不可用：{ex.Message}"); } }
            task.Status = session.Status = TaskRunStatus.Running; events.Publish(new TaskStartedEvent(session)); SessionChanged?.Invoke(session);
            await log.WriteAsync(LogLevel.Info, $"{task.Name} 已启动，PID {root.Id}");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(task.MaxRunMinutes));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(queueToken, timeout.Token);
            try { await WaitForCompletionAsync(root, task, session, linked.Token); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !queueToken.IsCancellationRequested) { timedOut = true; }
            if (queueToken.IsCancellationRequested) session.ExitReason = "用户停止";
            else if (timedOut) { task.Status = session.Status = TaskRunStatus.TimedOut; session.ExitReason = "超过最大运行时间"; events.Publish(new TaskTimedOutEvent(session)); await log.WriteAsync(LogLevel.Error, $"{task.Name} 已超时"); }
            else { task.Status = session.Status = TaskRunStatus.CompletionDetected; session.ExitReason = "完成条件满足"; events.Publish(new TaskCompletionDetectedEvent(session)); await log.WriteAsync(LogLevel.Info, $"检测到 {task.Name} 已完成"); }
        }
        catch (OperationCanceledException) { session.ExitReason = "用户停止"; }
        catch (Exception ex) { error = ex.Message; session.ExitReason = ex.Message; events.Publish(new TaskFailedEvent(session, ex.Message)); await log.WriteAsync(LogLevel.Error, $"{task.Name} 执行失败：{ex.Message}"); }
        finally
        {
            task.Status = session.Status = TaskRunStatus.Cleaning; events.Publish(new TaskCleanupStartedEvent(session)); SessionChanged?.Invoke(session);
            await log.WriteAsync(LogLevel.Info, $"开始清理 {task.Name} 关联进程");
            bool clean;
            try { clean = await cleanup.CleanupAsync(session, task, job, CancellationToken.None); }
            catch (Exception ex) { clean = false; error ??= ex.Message; }
            task.Status = session.Status = TaskRunStatus.CleanupVerifying; SessionChanged?.Invoke(session);
            var remaining = monitor.Scan(session, task).Count;
            if (clean && remaining == 0) { events.Publish(new TaskCleanupCompletedEvent(session)); await log.WriteAsync(LogLevel.Success, $"{task.Name} 清理完成"); }
            else { error ??= $"仍有 {remaining} 个关联进程未退出"; await log.WriteAsync(LogLevel.Error, $"{task.Name} 清理验证失败：{error}"); }
            job?.Dispose(); root?.Dispose(); session.EndTime = DateTimeOffset.Now;
        }
        var success = error is null && !timedOut && !queueToken.IsCancellationRequested;
        task.Status = session.Status = queueToken.IsCancellationRequested ? TaskRunStatus.Stopped : timedOut ? TaskRunStatus.TimedOut : success ? TaskRunStatus.Completed : TaskRunStatus.Failed;
        if (success) { events.Publish(new TaskCompletedEvent(session)); await log.WriteAsync(LogLevel.Success, $"{task.Name} 已完成，耗时 {(session.EndTime!.Value - session.StartTime).ToString(@"hh\:mm\:ss")}"); }
        SessionChanged?.Invoke(session);
        return new(session, success, timedOut, error);
    }

    private async Task WaitForCompletionAsync(Process root, AutomationTaskConfig task, RuntimeSession session, CancellationToken token)
    {
        if (task.CompletionMode == CompletionDetectionMode.Custom) throw new NotSupportedException("自定义完成检测接口已预留，第一版暂未实现");
        bool targetSeen = false;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            monitor.Scan(session, task); SessionChanged?.Invoke(session);
            if (task.CompletionMode == CompletionDetectionMode.MainProcessExit && root.HasExited) return;
            if (task.CompletionMode == CompletionDetectionMode.SpecifiedProcessExit)
            {
                var target = Path.GetFileNameWithoutExtension(task.CompletionProcessName);
                var matches = session.TrackedProcesses.Where(p => string.Equals(p.ProcessName, target, StringComparison.OrdinalIgnoreCase)).ToList();
                targetSeen |= matches.Count > 0;
                if (targetSeen && matches.Count == 0) return;
            }
            await Task.Delay(700, token);
        }
    }
}
