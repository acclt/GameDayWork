using System.Diagnostics;
using GameOrchestrator.Models;

namespace GameOrchestrator.Services;

public sealed class ProcessCleanupService(ProcessMonitorService monitor, LoggingService log)
{
    public async Task<bool> CleanupAsync(RuntimeSession session, AutomationTaskConfig task, JobObjectService? job, CancellationToken token)
    {
        await Task.Delay(TimeSpan.FromSeconds(task.CleanupWaitSeconds), token);
        for (var attempt = 1; attempt <= task.CleanupRetries; attempt++)
        {
            var remaining = monitor.Scan(session, task).Where(p => ShouldCleanup(p, task)).ToList();
            if (remaining.Count == 0) return true;
            await log.WriteAsync(LogLevel.Warning, $"发现 {remaining.Count} 个残留进程，清理尝试 {attempt}/{task.CleanupRetries}");
            if (attempt == 1) job?.Terminate();
            foreach (var tracked in remaining)
            {
                try
                {
                    using var process = Process.GetProcessById(tracked.Pid);
                    if (process.HasExited) continue;
                    await log.WriteAsync(LogLevel.Info, $"正在结束 {tracked.ProcessName}，PID {tracked.Pid}");
                    if (process.CloseMainWindow())
                    {
                        try { await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(task.CleanupWaitSeconds), token); } catch (TimeoutException) { }
                    }
                    if (!process.HasExited) process.Kill(true);
                }
                catch (ArgumentException) { }
                catch (Exception ex) { await log.WriteAsync(LogLevel.Warning, $"清理 PID {tracked.Pid} 失败：{ex.Message}"); }
            }
            await Task.Delay(TimeSpan.FromSeconds(task.CleanupWaitSeconds), token);
        }
        return !monitor.Scan(session, task).Any(p => ShouldCleanup(p, task));
    }
    private static bool ShouldCleanup(TrackedProcess process, AutomationTaskConfig task)
    {
        if (process.Source is TrackedProcessSource.Root or TrackedProcessSource.Child or TrackedProcessSource.JobObject) return true;
        return task.ProcessRules.Any(r => r.Cleanup && ((!string.IsNullOrWhiteSpace(r.ExecutablePath) && string.Equals(r.ExecutablePath, process.ExecutablePath, StringComparison.OrdinalIgnoreCase)) || (r.AllowNameFallback && string.Equals(Path.GetFileNameWithoutExtension(r.ProcessName), process.ProcessName, StringComparison.OrdinalIgnoreCase))));
    }
}
