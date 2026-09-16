using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using GameOrchestrator.Events;
using GameOrchestrator.Infrastructure;
using GameOrchestrator.Models;
using GameOrchestrator.Services;

namespace GameOrchestrator.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly ConfigService _configService = new();
    private readonly LoggingService _log = new();
    private readonly SchedulerService _scheduler = new();
    private readonly TaskValidationService _validator = new();
    private readonly KnownToolProfileService _toolProfiles = new();
    private readonly TaskQueueService _queue;
    private AppConfig _config = new();
    private AutomationTaskConfig? _selectedTask;
    private RuntimeSession? _currentSession;
    private string _statusText = "准备就绪";
    private string _elapsed = "00:00:00";
    private readonly DispatcherTimer _uiTimer;

    public ObservableCollection<AutomationTaskConfig> Tasks => _config.Tasks;
    public ObservableCollection<LogEntry> Logs { get; } = [];
    public AutomationTaskConfig? SelectedTask { get => _selectedTask; set { if (SetProperty(ref _selectedTask, value)) RaiseCommandStates(); } }
    public RuntimeSession? CurrentSession { get => _currentSession; private set { if (SetProperty(ref _currentSession, value)) RaiseRuntimeProperties(); } }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string Elapsed { get => _elapsed; private set => SetProperty(ref _elapsed, value); }
    public string NextRunText => _scheduler.NextRun is { } next ? next.ToString("yyyy/MM/dd HH:mm") : "未启用";
    public int TaskIntervalSeconds { get => _config.TaskIntervalSeconds; set { _config.TaskIntervalSeconds = Math.Max(0, value); OnPropertyChanged(); } }
    public FailurePolicy FailurePolicy { get => _config.FailurePolicy; set { _config.FailurePolicy = value; OnPropertyChanged(); } }
    public QueueExecutionMode ExecutionMode { get => _config.ExecutionMode; set { _config.ExecutionMode = value; OnPropertyChanged(); } }
    public bool ShowCompletionNotification
    {
        get => _config.Notifications.NotifyOnComplete;
        set
        {
            _config.Notifications.NotifyOnComplete = value;
            _config.Notifications.Enabled = value
                || _config.Notifications.NotifyOnStart
                || _config.Notifications.NotifyOnFailure
                || _config.Notifications.NotifyOnTimeout;
            OnPropertyChanged();
        }
    }
    public bool GenerateExecutionLog
    {
        get => _config.GenerateExecutionLog;
        set
        {
            _config.GenerateExecutionLog = value;
            _log.FileLoggingEnabled = value;
            OnPropertyChanged();
        }
    }
    public ScheduleConfig Schedule => _config.Schedule;
    public Array FailurePolicies => Enum.GetValues(typeof(FailurePolicy));
    public Array CompletionModes => Enum.GetValues(typeof(CompletionDetectionMode));
    public bool HasActiveTask => CurrentSession?.Status is TaskRunStatus.Starting or TaskRunStatus.Running or TaskRunStatus.CompletionDetected or TaskRunStatus.Cleaning or TaskRunStatus.CleanupVerifying;
    public string CurrentTaskName => HasActiveTask ? CurrentSession!.TaskName : "—";
    public string CurrentStage => HasActiveTask ? CurrentSession!.Status.ToString().ToUpperInvariant() : "IDLE";
    public string RootPid => HasActiveTask && CurrentSession!.RootPid > 0 ? CurrentSession.RootPid.ToString() : "—";
    public int RelatedCount => HasActiveTask ? CurrentSession!.TrackedProcesses.Count : 0;
    public string CurrentStatusHeadline => HasActiveTask ? CurrentSession!.TaskName : "暂无任务运行";
    public string CurrentStatusHint => CurrentSession?.Status switch
    {
        TaskRunStatus.Starting => "正在启动任务",
        TaskRunStatus.Running => "任务正在运行",
        TaskRunStatus.CompletionDetected => "已检测完成，准备清理",
        TaskRunStatus.Cleaning => "正在清理关联进程",
        TaskRunStatus.CleanupVerifying => "正在确认清理结果",
        _ => "点击「开始执行」或等待定时任务"
    };
    public string RecentEvent => Logs.LastOrDefault()?.Message ?? "点击“开始执行”或等待定时任务";

    public ICommand StartCommand { get; }
    public ICommand RunOnceCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand DeleteTaskCommand { get; }
    public ICommand DuplicateTaskCommand { get; }
    public ICommand AddRuleCommand { get; }
    public ICommand DeleteRuleCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand ResetTaskCommand { get; }
    public ICommand ImportToolsCommand { get; }
    public event Action<string>? ValidationFailed;
    public event Action<string>? NoticeRequested;

    public MainViewModel()
    {
        var bus = new TaskEventBus();
        var monitor = new ProcessMonitorService();
        var cleanup = new ProcessCleanupService(monitor, _log);
        var runner = new TaskRunnerService(monitor, cleanup, _log, bus);
        _queue = new TaskQueueService(runner, _log, bus);
        _scheduler.Error += ex => _ = _log.WriteAsync(LogLevel.Error, $"定时任务执行异常：{ex.Message}");
        runner.SessionChanged += session => Application.Current.Dispatcher.Invoke(() => CurrentSession = session);
        _log.EntryWritten += entry => Application.Current.Dispatcher.Invoke(() => { Logs.Add(entry); if (Logs.Count > 2000) Logs.RemoveAt(0); OnPropertyChanged(nameof(RecentEvent)); });
        _queue.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TaskQueueService.Status)) Application.Current.Dispatcher.Invoke(UpdateQueueStatus); };
        StartCommand = new AsyncRelayCommand(RunByModeAsync, () => !_queue.IsRunning);
        RunOnceCommand = new AsyncRelayCommand(RunFullQueueAsync, () => !_queue.IsRunning);
        StopCommand = new RelayCommand(() => _queue.Stop(), () => _queue.IsRunning);
        DeleteTaskCommand = new RelayCommand(DeleteTask, () => SelectedTask is not null && !_queue.IsRunning);
        DuplicateTaskCommand = new RelayCommand(DuplicateTask, () => SelectedTask is not null && !_queue.IsRunning);
        AddRuleCommand = new RelayCommand(() => SelectedTask?.ProcessRules.Add(new ProcessRule { ProcessName = "Process.exe" }));
        DeleteRuleCommand = new RelayCommand(() => { if (SelectedTask?.ProcessRules.Count > 0) SelectedTask.ProcessRules.RemoveAt(SelectedTask.ProcessRules.Count - 1); });
        MoveUpCommand = new RelayCommand(() => MoveSelected(-1));
        MoveDownCommand = new RelayCommand(() => MoveSelected(1));
        OpenLogsCommand = new RelayCommand(OpenLogs);
        ResetTaskCommand = new AsyncRelayCommand(ResetSelectedTaskAsync, () => SelectedTask is not null && !_queue.IsRunning);
        ImportToolsCommand = new AsyncRelayCommand(ApplyKnownToolsAsync, () => !_queue.IsRunning);
        _uiTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => TickUi(), Application.Current.Dispatcher);
    }

    public async Task InitializeAsync()
    {
        _config = await _configService.LoadAsync();
        _config.ExecutionMode = QueueExecutionMode.Sequential;
        _log.FileLoggingEnabled = _config.GenerateExecutionLog;
        Tasks.CollectionChanged += (_, _) => RefreshTaskIndexes();
        RefreshTaskIndexes();
        OnPropertyChanged(nameof(Tasks)); OnPropertyChanged(nameof(TaskIntervalSeconds)); OnPropertyChanged(nameof(FailurePolicy)); OnPropertyChanged(nameof(Schedule)); OnPropertyChanged(nameof(ShowCompletionNotification)); OnPropertyChanged(nameof(GenerateExecutionLog));
        SelectedTask = Tasks.FirstOrDefault();
        _scheduler.Start(_config.Schedule, RunFullQueueAsync);
        OnPropertyChanged(nameof(NextRunText));
        await _log.WriteAsync(LogLevel.Info, $"程序启动，已加载任务队列（{Tasks.Count} 项）");
    }
    public Task SaveAsync() => _configService.SaveAsync(_config);
    public async Task ApplyScheduleAsync() { await SaveAsync(); OnPropertyChanged(nameof(NextRunText)); }
    public void Reorder(AutomationTaskConfig source, AutomationTaskConfig target)
    {
        if (_queue.IsRunning || source == target) return;
        var oldIndex = Tasks.IndexOf(source); var newIndex = Tasks.IndexOf(target);
        if (oldIndex >= 0 && newIndex >= 0) Tasks.Move(oldIndex, newIndex);
    }
    private Task RunByModeAsync() => RunFullQueueAsync();
    private async Task RunFullQueueAsync()
    {
        var tasks = Tasks.Where(t => t.Enabled).ToList();
        if (!await ValidateBeforeRunAsync(tasks)) return;
        await _queue.RunAsync(tasks, TaskIntervalSeconds, FailurePolicy);
        RaiseCommandStates();
    }
    private async Task RunSingleTaskAsync()
    {
        if (SelectedTask is null) { ValidationFailed?.Invoke("请先选择一个任务。"); return; }
        if (!await ValidateBeforeRunAsync([SelectedTask])) return;
        await _queue.RunSingleAsync(SelectedTask, FailurePolicy);
        RaiseCommandStates();
    }
    private async Task<bool> ValidateBeforeRunAsync(IReadOnlyList<AutomationTaskConfig> tasks)
    {
        if (tasks.Count == 0) { ValidationFailed?.Invoke("没有已启用的任务可执行。"); return false; }
        var issues = _validator.Validate(tasks);
        if (issues.Count == 0) { await SaveAsync(); return true; }
        var message = string.Join(Environment.NewLine, issues.Select(x => $"• {x.TaskName}：{x.Message}"));
        foreach (var issue in issues) await _log.WriteAsync(LogLevel.Error, $"配置校验失败 - {issue.TaskName}：{issue.Message}");
        ValidationFailed?.Invoke(message);
        return false;
    }
    private void UpdateQueueStatus()
    {
        StatusText = _queue.Status switch { QueueRunStatus.Running => "执行中", QueueRunStatus.Stopping => "正在停止", QueueRunStatus.Completed => "已完成", QueueRunStatus.Failed => "执行失败", _ => "准备就绪" };
        RaiseCommandStates();
    }
    private void DeleteTask() { if (SelectedTask is null) return; var index = Tasks.IndexOf(SelectedTask); Tasks.Remove(SelectedTask); SelectedTask = Tasks.ElementAtOrDefault(Math.Min(index, Tasks.Count - 1)); }
    private void DuplicateTask()
    {
        if (SelectedTask is null) return;
        var copy = new AutomationTaskConfig { Name = SelectedTask.Name + " 副本", ProgramPath = SelectedTask.ProgramPath, Arguments = SelectedTask.Arguments, WorkingDirectory = SelectedTask.WorkingDirectory, CompletionMode = SelectedTask.CompletionMode, CompletionProcessName = SelectedTask.CompletionProcessName, CompletionLogPath = SelectedTask.CompletionLogPath, CompletionKeyword = SelectedTask.CompletionKeyword, CompletionFailureKeyword = SelectedTask.CompletionFailureKeyword, MaxRunMinutes = SelectedTask.MaxRunMinutes, CleanupWaitSeconds = SelectedTask.CleanupWaitSeconds, CleanupRetries = SelectedTask.CleanupRetries, TrackChildren = SelectedTask.TrackChildren, UseJobObject = SelectedTask.UseJobObject, RunAsAdministrator = SelectedTask.RunAsAdministrator };
        foreach (var rule in SelectedTask.ProcessRules) copy.ProcessRules.Add(new ProcessRule { ProcessName = rule.ProcessName, ExecutablePath = rule.ExecutablePath, ExecutableDirectory = rule.ExecutableDirectory, Monitor = rule.Monitor, Cleanup = rule.Cleanup, AllowNameFallback = rule.AllowNameFallback });
        Tasks.Insert(Tasks.IndexOf(SelectedTask) + 1, copy); SelectedTask = copy;
    }
    private void MoveSelected(int offset) { if (SelectedTask is null) return; var from = Tasks.IndexOf(SelectedTask); var to = from + offset; if (to >= 0 && to < Tasks.Count) Tasks.Move(from, to); }
    private async Task ResetSelectedTaskAsync()
    {
        if (SelectedTask is null) return;
        var index = Tasks.IndexOf(SelectedTask);
        var saved = (await _configService.LoadAsync()).Tasks.FirstOrDefault(t => t.Id == SelectedTask.Id);
        if (saved is null) { ValidationFailed?.Invoke("该任务尚未保存，无法重置。"); return; }
        Tasks[index] = saved; SelectedTask = saved;
        await _log.WriteAsync(LogLevel.Info, $"已重置任务配置：{saved.Name}");
    }
    private async Task ApplyKnownToolsAsync()
    {
        var discovered = _toolProfiles.Discover();
        var added = 0;
        var updated = 0;
        foreach (var profile in discovered)
        {
            var aliases = profile.Name switch { "MAA" => new[] { "MAA", "MMA" }, "MFA" => new[] { "MFA", "MAN" }, _ => new[] { profile.Name } };
            var existing = Tasks.FirstOrDefault(task => aliases.Contains(task.Name, StringComparer.OrdinalIgnoreCase));
            if (existing is not null)
            {
                KnownToolProfileService.ApplyRecommendedSettings(existing, profile);
                updated++;
            }
            else
            {
                Tasks.Add(profile);
                added++;
            }
        }
        if (added + updated > 0)
        {
            await SaveAsync();
            await _log.WriteAsync(LogLevel.Success, $"五项目适配已应用：新增 {added} 项，更新 {updated} 项");
        }
        NoticeRequested?.Invoke(added + updated > 0
            ? $"已应用本机工具适配：新增 {added} 项，更新 {updated} 项。"
            : "未发现 BGI、MAA、ZOG、MFA 或 M7A。");
    }
    public IReadOnlyList<AutomationTaskConfig> DiscoverKnownTools() => _toolProfiles.Discover();
    public bool ContainsKnownTool(string name) => Tasks.Any(task => NormalizeKnownToolName(task.Name) == NormalizeKnownToolName(name));
    public async Task<bool> AddKnownToolAsync(AutomationTaskConfig profile)
    {
        if (_queue.IsRunning || ContainsKnownTool(profile.Name)) return false;
        Tasks.Add(profile);
        SelectedTask = profile;
        await SaveAsync();
        await _log.WriteAsync(LogLevel.Success, $"已添加适配任务：{profile.Name}");
        return true;
    }
    private static string NormalizeKnownToolName(string name) => name.ToUpperInvariant() switch
    {
        "MMA" => "MAA",
        "MAN" => "MFA",
        _ => name.ToUpperInvariant()
    };
    private void RefreshTaskIndexes()
    {
        for (var index = 0; index < Tasks.Count; index++) Tasks[index].DisplayIndex = index + 1;
    }
    private void RaiseCommandStates()
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.BeginInvoke(RaiseCommandStates);
            return;
        }
        ((AsyncRelayCommand)StartCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)RunOnceCommand).RaiseCanExecuteChanged();
        ((RelayCommand)StopCommand).RaiseCanExecuteChanged();
        ((RelayCommand)DeleteTaskCommand).RaiseCanExecuteChanged();
        ((RelayCommand)DuplicateTaskCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)ResetTaskCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)ImportToolsCommand).RaiseCanExecuteChanged();
    }
    private void TickUi()
    {
        if (CurrentSession is { } session) Elapsed = ((session.EndTime ?? DateTimeOffset.Now) - session.StartTime).ToString(@"hh\:mm\:ss");
        OnPropertyChanged(nameof(NextRunText)); RaiseRuntimeProperties();
    }
    private void RaiseRuntimeProperties()
    {
        OnPropertyChanged(nameof(HasActiveTask));
        OnPropertyChanged(nameof(CurrentTaskName));
        OnPropertyChanged(nameof(CurrentStage));
        OnPropertyChanged(nameof(RootPid));
        OnPropertyChanged(nameof(RelatedCount));
        OnPropertyChanged(nameof(CurrentStatusHeadline));
        OnPropertyChanged(nameof(CurrentStatusHint));
    }
    private void OpenLogs() { Directory.CreateDirectory(_log.LogDirectory); Process.Start(new ProcessStartInfo("explorer.exe", _log.LogDirectory) { UseShellExecute = true }); }
    public void Dispose() { _scheduler.Dispose(); _uiTimer.Stop(); }
}
