using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using GameOrchestrator.Infrastructure;

namespace GameOrchestrator.Models;

public sealed class AutomationTaskConfig : ObservableObject
{
    private string _name = "新任务";
    private bool _enabled = true;
    private string _programPath = "";
    private string _arguments = "";
    private string _workingDirectory = "";
    private CompletionDetectionMode _completionMode = CompletionDetectionMode.MainProcessExit;
    private string _completionProcessName = "";
    private string _description = "自动化日常任务，完成后自动清理相关进程并执行下一项。";
    private int _maxRunMinutes = 60;
    private int _cleanupWaitSeconds = 3;
    private int _cleanupRetries = 3;
    private TaskRunStatus _status = TaskRunStatus.Idle;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => _name; set { if (SetProperty(ref _name, value)) OnPropertyChanged(nameof(IconText)); } }
    [JsonIgnore] public string IconText => string.IsNullOrWhiteSpace(Name) ? "?" : Name[..1].ToUpperInvariant();
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    public string ProgramPath { get => _programPath; set { if (SetProperty(ref _programPath, value) && string.IsNullOrWhiteSpace(WorkingDirectory)) WorkingDirectory = Path.GetDirectoryName(value) ?? ""; } }
    public string Arguments { get => _arguments; set => SetProperty(ref _arguments, value); }
    public string WorkingDirectory { get => _workingDirectory; set => SetProperty(ref _workingDirectory, value); }
    public CompletionDetectionMode CompletionMode { get => _completionMode; set => SetProperty(ref _completionMode, value); }
    public string CompletionProcessName { get => _completionProcessName; set => SetProperty(ref _completionProcessName, value); }
    public string Description { get => _description; set => SetProperty(ref _description, value); }
    public int MaxRunMinutes { get => _maxRunMinutes; set => SetProperty(ref _maxRunMinutes, Math.Max(1, value)); }
    public int CleanupWaitSeconds { get => _cleanupWaitSeconds; set => SetProperty(ref _cleanupWaitSeconds, Math.Max(1, value)); }
    public int CleanupRetries { get => _cleanupRetries; set => SetProperty(ref _cleanupRetries, Math.Max(1, value)); }
    public bool TrackChildren { get; set; } = true;
    public bool UseJobObject { get; set; } = true;
    public ObservableCollection<ProcessRule> ProcessRules { get; set; } = [];
    [JsonIgnore] public TaskRunStatus Status { get => _status; set { if (SetProperty(ref _status, value)) { OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(IsActive)); } } }
    [JsonIgnore] public bool IsActive => Status is TaskRunStatus.Starting or TaskRunStatus.Running or TaskRunStatus.CompletionDetected or TaskRunStatus.Cleaning or TaskRunStatus.CleanupVerifying;
    [JsonIgnore] public string StatusText => Status switch
    {
        TaskRunStatus.Idle => "等待执行", TaskRunStatus.Waiting => "等待执行", TaskRunStatus.Starting => "正在启动",
        TaskRunStatus.Running => "正在运行", TaskRunStatus.CompletionDetected => "已检测完成", TaskRunStatus.Cleaning => "正在清理",
        TaskRunStatus.CleanupVerifying => "正在确认清理", TaskRunStatus.Completed => "已完成", TaskRunStatus.Failed => "执行失败",
        TaskRunStatus.TimedOut => "超时", TaskRunStatus.Skipped => "已跳过", TaskRunStatus.Stopped => "已停止", _ => Status.ToString()
    };
}

public sealed class ProcessRule : ObservableObject
{
    private string _processName = "";
    private string _executablePath = "";
    private bool _monitor = true;
    private bool _cleanup = true;
    private bool _allowNameFallback;
    public string ProcessName { get => _processName; set => SetProperty(ref _processName, value); }
    public string ExecutablePath { get => _executablePath; set => SetProperty(ref _executablePath, value); }
    public bool Monitor { get => _monitor; set => SetProperty(ref _monitor, value); }
    public bool Cleanup { get => _cleanup; set => SetProperty(ref _cleanup, value); }
    public bool AllowNameFallback { get => _allowNameFallback; set => SetProperty(ref _allowNameFallback, value); }
}

public sealed class ScheduleConfig : ObservableObject
{
    private bool _enabled;
    private ScheduleRepeat _repeat = ScheduleRepeat.Daily;
    private TimeSpan _time = new(3, 0, 0);
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    public ScheduleRepeat Repeat { get => _repeat; set => SetProperty(ref _repeat, value); }
    public TimeSpan Time { get => _time; set => SetProperty(ref _time, value); }
    public HashSet<DayOfWeek> SelectedDays { get; set; } = [];
}

public sealed class NotificationConfig
{
    public bool Enabled { get; set; }
    public bool NotifyOnStart { get; set; }
    public bool NotifyOnComplete { get; set; } = true;
    public bool NotifyOnFailure { get; set; } = true;
    public bool NotifyOnTimeout { get; set; } = true;
    public bool CaptureOnStart { get; set; }
    public bool CaptureOnComplete { get; set; }
    public bool CaptureOnFailure { get; set; }
    public bool CaptureOnTimeout { get; set; }
}

public sealed class AppConfig
{
    public ObservableCollection<AutomationTaskConfig> Tasks { get; set; } = [];
    public int TaskIntervalSeconds { get; set; } = 5;
    public FailurePolicy FailurePolicy { get; set; } = FailurePolicy.ForceCleanupAndContinue;
    public ScheduleConfig Schedule { get; set; } = new();
    public NotificationConfig Notifications { get; set; } = new();
}
