namespace GameOrchestrator.Models;

public sealed class RuntimeSession
{
    private readonly object _processGate = new();
    private readonly List<TrackedProcess> _trackedProcesses = [];
    private readonly List<ProcessTerminationRecord> _terminatedProcesses = [];
    public Guid SessionId { get; init; } = Guid.NewGuid();
    public Guid TaskId { get; init; }
    public string TaskName { get; init; } = "";
    public DateTimeOffset StartTime { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? EndTime { get; set; }
    public int RootPid { get; set; }
    public string? RootExecutablePath { get; set; }
    public DateTimeOffset? RootProcessStartTime { get; set; }
    public HashSet<int> BaselineProcessIds { get; } = [];
    public TaskRunStatus Status { get; set; }
    public string ExitReason { get; set; } = "";

    public int TrackedProcessCount { get { lock (_processGate) return _trackedProcesses.Count; } }
    public IReadOnlyList<TrackedProcess> SnapshotTrackedProcesses() { lock (_processGate) return [.. _trackedProcesses]; }
    public IReadOnlyList<ProcessTerminationRecord> SnapshotTerminatedProcesses() { lock (_processGate) return [.. _terminatedProcesses]; }
    internal void ReplaceTrackedProcesses(IEnumerable<TrackedProcess> processes)
    {
        lock (_processGate)
        {
            _trackedProcesses.Clear();
            _trackedProcesses.AddRange(processes);
        }
    }
    internal void SetTerminatedProcesses(IEnumerable<ProcessTerminationRecord> processes)
    {
        lock (_processGate)
        {
            _terminatedProcesses.Clear();
            _terminatedProcesses.AddRange(processes);
        }
    }
}

public sealed record TrackedProcess(int Pid, string ProcessName, string? ExecutablePath,
    DateTimeOffset? StartTime, int? ParentPid, TrackedProcessSource Source);

public sealed record ProcessTerminationRecord(
    int Pid,
    string ProcessName,
    TrackedProcessSource Source,
    string Method,
    bool Success,
    string Error);

public sealed record ProcessCleanupResult(
    bool Success,
    IReadOnlyList<ProcessTerminationRecord> Processes,
    int RemainingCount);

public sealed record ScreenshotPayload(
    byte[] Data,
    string MimeType,
    string Format,
    int Width,
    int Height);

public sealed record LogEntry(DateTimeOffset Time, LogLevel Level, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss");
    public string LevelText => Level.ToString().ToUpperInvariant();
}
