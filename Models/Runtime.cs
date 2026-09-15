namespace GameOrchestrator.Models;

public sealed class RuntimeSession
{
    public Guid SessionId { get; init; } = Guid.NewGuid();
    public Guid TaskId { get; init; }
    public string TaskName { get; init; } = "";
    public DateTimeOffset StartTime { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? EndTime { get; set; }
    public int RootPid { get; set; }
    public string? RootExecutablePath { get; set; }
    public DateTimeOffset? RootProcessStartTime { get; set; }
    public HashSet<int> BaselineProcessIds { get; } = [];
    public List<TrackedProcess> TrackedProcesses { get; } = [];
    public TaskRunStatus Status { get; set; }
    public string ExitReason { get; set; } = "";
}

public sealed record TrackedProcess(int Pid, string ProcessName, string? ExecutablePath,
    DateTimeOffset? StartTime, int? ParentPid, TrackedProcessSource Source);

public sealed record LogEntry(DateTimeOffset Time, LogLevel Level, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss");
    public string LevelText => Level.ToString().ToUpperInvariant();
}
