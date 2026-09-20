using System.Diagnostics;
using GameOrchestrator.Models;

namespace GameOrchestrator.Services;

public sealed class ProcessCleanupService(ProcessMonitorService monitor, LoggingService log)
{
    public async Task<ProcessCleanupResult> CleanupAsync(RuntimeSession session, AutomationTaskConfig task, JobObjectService? job, CancellationToken token)
    {
        var records = new Dictionary<int, ProcessTerminationRecord>();
        await Task.Delay(TimeSpan.FromSeconds(task.CleanupWaitSeconds), token);
        for (var attempt = 1; attempt <= task.CleanupRetries; attempt++)
        {
            var remaining = monitor.Scan(session, task).Where(p => ShouldCleanup(p, task)).ToList();
            if (remaining.Count == 0) return new(true, [.. records.Values], 0);
            await log.WriteAsync(LogLevel.Warning, $"发现 {remaining.Count} 个残留进程，清理尝试 {attempt}/{task.CleanupRetries}");
            if (attempt == 1 && job is not null)
            {
                var terminated = job.Terminate();
                await Task.Delay(TimeSpan.FromMilliseconds(200), token);
                foreach (var tracked in remaining)
                {
                    if (!terminated
                        || tracked.Source == TrackedProcessSource.RuleMatched
                        || IsProcessRunning(tracked.Pid)) continue;
                    records[tracked.Pid] = new(tracked.Pid, tracked.ProcessName, tracked.Source, "JobObject", true, "");
                }
            }
            foreach (var tracked in remaining)
            {
                if (!IsProcessRunning(tracked.Pid)) continue;
                try
                {
                    using var process = Process.GetProcessById(tracked.Pid);
                    if (process.HasExited) continue;
                    await log.WriteAsync(LogLevel.Info, $"正在结束 {tracked.ProcessName}，PID {tracked.Pid}");
                    var method = "KillTree";
                    if (process.CloseMainWindow())
                    {
                        try { await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(task.CleanupWaitSeconds), token); } catch (TimeoutException) { }
                        if (process.HasExited) method = "正常关闭";
                    }
                    if (!process.HasExited) process.Kill(true);
                    if (!process.HasExited)
                    {
                        try { await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(task.CleanupWaitSeconds), token); } catch (TimeoutException) { }
                    }
                    var success = process.HasExited;
                    records[tracked.Pid] = new(tracked.Pid, tracked.ProcessName, tracked.Source, method, success,
                        success ? "" : "等待进程退出超时");
                }
                catch (ArgumentException) { }
                catch (Exception ex)
                {
                    records[tracked.Pid] = new(tracked.Pid, tracked.ProcessName, tracked.Source, "KillTree", false, ex.Message);
                    await log.WriteAsync(LogLevel.Warning, $"清理 PID {tracked.Pid} 失败：{ex.Message}");
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(task.CleanupWaitSeconds), token);
        }
        var remainingCount = monitor.Scan(session, task).Count(p => ShouldCleanup(p, task));
        return new(remainingCount == 0, [.. records.Values], remainingCount);
    }

    private static bool IsProcessRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
    private static bool ShouldCleanup(TrackedProcess process, AutomationTaskConfig task)
    {
        if (process.Source is TrackedProcessSource.Root or TrackedProcessSource.Child or TrackedProcessSource.JobObject) return true;
        return task.ProcessRules.Any(r => r.Cleanup &&
            ((!string.IsNullOrWhiteSpace(r.ExecutablePath) && string.Equals(Path.GetFullPath(r.ExecutablePath), process.ExecutablePath is null ? null : Path.GetFullPath(process.ExecutablePath), StringComparison.OrdinalIgnoreCase)) ||
             (!string.IsNullOrWhiteSpace(r.ExecutableDirectory) && process.ExecutablePath is not null && IsUnderDirectory(process.ExecutablePath, r.ExecutableDirectory)) ||
             (r.AllowNameFallback && string.Equals(Path.GetFileNameWithoutExtension(r.ProcessName), process.ProcessName, StringComparison.OrdinalIgnoreCase))));
    }
    private static bool IsUnderDirectory(string path, string directory)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(directory), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }
}
