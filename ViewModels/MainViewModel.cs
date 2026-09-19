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
    private readonly StartupService _startupService = new();
    private readonly LoggingService _log = new();
    private readonly SchedulerService _scheduler = new();
    private readonly TaskValidationService _validator = new();
    private readonly KnownToolProfileService _toolProfiles = new();
    private readonly TaskQueueService _queue;
    private readonly BrightnessManager _brightnessManager;
    private readonly ScreenManager _screenManager;
    private readonly TaskLaunchCoordinator _launchCoordinator;
    private readonly WeComNotificationService _notificationService;
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
    public string WeComWebhookUrl
    {
        get => _config.Notifications.WeComWebhookUrl;
        set
        {
            if (_config.Notifications.WeComWebhookUrl == value) return;
            _config.Notifications.WeComWebhookUrl = value ?? "";
            _config.Notifications.Enabled = !string.IsNullOrWhiteSpace(value);
            OnPropertyChanged();
        }
    }
    public bool NotifyOnStart { get => _config.Notifications.NotifyOnStart; set { _config.Notifications.NotifyOnStart = value; OnPropertyChanged(); } }
    public bool NotifyOnComplete { get => _config.Notifications.NotifyOnComplete; set { _config.Notifications.NotifyOnComplete = value; OnPropertyChanged(); } }
    public bool NotifyOnFailure
    {
        get => _config.Notifications.NotifyOnFailure;
        set
        {
            _config.Notifications.NotifyOnFailure = value;
            OnPropertyChanged();
        }
    }
    public bool NotifyOnForcedStop
    {
        get => _config.Notifications.NotifyOnForcedStop;
        set
        {
            _config.Notifications.NotifyOnForcedStop = value;
            _config.Notifications.NotifyOnTimeout = value;
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
    public bool StartWithWindows
    {
        get => _config.StartWithWindows;
        set
        {
            if (_config.StartWithWindows == value) return;
            _config.StartWithWindows = value;
            OnPropertyChanged();
        }
    }
    public bool EnableScreenManager
    {
        get => _config.EnableScreenManager;
        set
        {
            if (_config.EnableScreenManager == value) return;
            _config.EnableScreenManager = value;
            _screenManager.Enabled = value;
            OnPropertyChanged();
        }
    }
    public int IdleTimeoutMinutes
    {
        get => _config.IdleTimeoutMinutes;
        set
        {
            var normalized = Math.Clamp(value, 1, 1440);
            if (_config.IdleTimeoutMinutes == normalized) return;
            _config.IdleTimeoutMinutes = normalized;
            _screenManager.IdleTimeout = TimeSpan.FromMinutes(normalized);
            OnPropertyChanged();
        }
    }
    public int WakeBeforeTaskSeconds
    {
        get => _config.WakeBeforeTaskSeconds;
        set
        {
            var normalized = Math.Clamp(value, 0, 3600);
            if (_config.WakeBeforeTaskSeconds == normalized) return;
            _config.WakeBeforeTaskSeconds = normalized;
            OnPropertyChanged();
        }
    }
    public Array FailurePolicies => Enum.GetValues(typeof(FailurePolicy));
    public Array CompletionModes => Enum.GetValues(typeof(CompletionDetectionMode));
    public Array CompletionActions => Enum.GetValues(typeof(TaskCompletionAction));
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
        _ => "选择左侧任务后点击「开始」，或等待定时任务"
    };
    public string RecentEvent => Logs.LastOrDefault()?.Message ?? "选择左侧任务后点击“开始”，或等待定时任务";

    public string ManualActionText => _launchCoordinator.IsBusy ? "■  停止" : "▶  开始";
    public ScreenManagerState ScreenState => _screenManager.State;
    public string ScreenStateText => ScreenState switch
    {
        ScreenManagerState.IdleMonitoring => "空闲监控",
        ScreenManagerState.Blackout => "假息屏中",
        ScreenManagerState.PreparingTask => "准备任务",
        ScreenManagerState.RunningTask => "执行任务",
        _ => ScreenState.ToString()
    };
    public ICommand ToggleExecutionCommand { get; }
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
        _notificationService = new WeComNotificationService(bus, () => _config.Notifications, _log);
        var runner = new TaskRunnerService(monitor, cleanup, _log, bus);
        _brightnessManager = new BrightnessManager(_log);
        _screenManager = new ScreenManager(_log, _brightnessManager);
        _queue = new TaskQueueService(runner, _log, bus);
        _launchCoordinator = new TaskLaunchCoordinator(_screenManager, _queue, _log);
        _scheduler.Error += ex => _ = _log.WriteAsync(LogLevel.Error, $"定时任务执行异常：{ex.Message}");
        runner.SessionChanged += session => Application.Current.Dispatcher.Invoke(() => CurrentSession = session);
        _log.EntryWritten += entry => Application.Current.Dispatcher.Invoke(() => { Logs.Add(entry); if (Logs.Count > 2000) Logs.RemoveAt(0); OnPropertyChanged(nameof(RecentEvent)); });
        _queue.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TaskQueueService.Status)) Application.Current.Dispatcher.Invoke(UpdateQueueStatus); };
        _screenManager.StateChanged += _ => Application.Current.Dispatcher.Invoke(() =>
        {
            OnPropertyChanged(nameof(ScreenState));
            OnPropertyChanged(nameof(ScreenStateText));
        });
        _launchCoordinator.BusyChanged += _ => Application.Current.Dispatcher.Invoke(() =>
        {
            OnPropertyChanged(nameof(ManualActionText));
            RaiseCommandStates();
        });
        ToggleExecutionCommand = new RelayCommand(ToggleSelectedTask, () => _launchCoordinator.IsBusy || SelectedTask is not null);
        DeleteTaskCommand = new RelayCommand(DeleteTask, () => SelectedTask is not null && !_launchCoordinator.IsBusy);
        DuplicateTaskCommand = new RelayCommand(DuplicateTask, () => SelectedTask is not null && !_launchCoordinator.IsBusy);
        AddRuleCommand = new RelayCommand(() => SelectedTask?.ProcessRules.Add(new ProcessRule { ProcessName = "Process.exe" }));
        DeleteRuleCommand = new RelayCommand(() => { if (SelectedTask?.ProcessRules.Count > 0) SelectedTask.ProcessRules.RemoveAt(SelectedTask.ProcessRules.Count - 1); });
        MoveUpCommand = new RelayCommand(() => MoveSelected(-1));
        MoveDownCommand = new RelayCommand(() => MoveSelected(1));
        OpenLogsCommand = new RelayCommand(OpenLogs);
        ResetTaskCommand = new AsyncRelayCommand(ResetSelectedTaskAsync, () => SelectedTask is not null && !_launchCoordinator.IsBusy);
        ImportToolsCommand = new AsyncRelayCommand(ApplyKnownToolsAsync, () => !_launchCoordinator.IsBusy);
        _uiTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => TickUi(), Application.Current.Dispatcher);
    }

    public async Task InitializeAsync()
    {
        _config = await _configService.LoadAsync();
        _config.Notifications ??= new NotificationConfig();
        _config.Notifications.Enabled = !string.IsNullOrWhiteSpace(_config.Notifications.WeComWebhookUrl);
        _config.ExecutionMode = QueueExecutionMode.Sequential;
        _config.AutoBlackoutAfterTask = false;
        _config.IdleTimeoutMinutes = Math.Clamp(_config.IdleTimeoutMinutes, 1, 1440);
        _config.WakeBeforeTaskSeconds = Math.Clamp(_config.WakeBeforeTaskSeconds, 0, 3600);
        _screenManager.Enabled = _config.EnableScreenManager;
        _screenManager.IdleTimeout = TimeSpan.FromMinutes(_config.IdleTimeoutMinutes);
        _log.FileLoggingEnabled = _config.GenerateExecutionLog;
        var logCleanup = await _log.CleanupAsync();
        if (logCleanup.DeletedFiles > 0)
            await _log.WriteAsync(LogLevel.Info, $"日志自动清理完成：删除 {logCleanup.DeletedFiles} 个文件，释放 {logCleanup.FreedBytes / 1024d / 1024d:F1} MB");
        if (logCleanup.FailedFiles > 0)
            await _log.WriteAsync(LogLevel.Warning, $"日志自动清理有 {logCleanup.FailedFiles} 个文件无法删除");
        try { _startupService.Apply(_config.StartWithWindows); }
        catch (Exception ex) { await _log.WriteAsync(LogLevel.Warning, $"同步开机启动项失败：{ex.Message}"); }
        await _screenManager.RecoverDisplayStateAsync();
        Tasks.CollectionChanged += (_, _) => RefreshTaskIndexes();
        RefreshTaskIndexes();
        OnPropertyChanged(nameof(Tasks)); OnPropertyChanged(nameof(TaskIntervalSeconds)); OnPropertyChanged(nameof(FailurePolicy)); OnPropertyChanged(nameof(GenerateExecutionLog)); OnPropertyChanged(nameof(StartWithWindows));
        OnPropertyChanged(nameof(EnableScreenManager)); OnPropertyChanged(nameof(IdleTimeoutMinutes)); OnPropertyChanged(nameof(WakeBeforeTaskSeconds));
        OnPropertyChanged(nameof(WeComWebhookUrl)); OnPropertyChanged(nameof(NotifyOnStart)); OnPropertyChanged(nameof(NotifyOnComplete)); OnPropertyChanged(nameof(NotifyOnFailure)); OnPropertyChanged(nameof(NotifyOnForcedStop));
        SelectedTask = Tasks.FirstOrDefault();
        _screenManager.StartMonitoring();
        _scheduler.Start(() => Tasks.ToList(), () => _config.WakeBeforeTaskSeconds, RunScheduledTasksAsync);
        OnPropertyChanged(nameof(NextRunText));
        await _log.WriteAsync(LogLevel.Info, $"程序启动，已加载任务队列（{Tasks.Count} 项）");
    }
    public Task SaveAsync()
    {
        _startupService.Apply(_config.StartWithWindows);
        return _configService.SaveAsync(_config);
    }
    public Task<NotificationTestResult> TestWeComNotificationAsync() => _notificationService.SendTestAsync();
    public Task<bool> EnterBlackoutAsync() => _screenManager.EnterBlackoutAsync();
    public Task ExitBlackoutAsync() => _screenManager.ExitBlackoutAsync();
    public void Reorder(AutomationTaskConfig source, AutomationTaskConfig target)
    {
        if (_launchCoordinator.IsBusy || source == target) return;
        var oldIndex = Tasks.IndexOf(source); var newIndex = Tasks.IndexOf(target);
        if (oldIndex >= 0 && newIndex >= 0) Tasks.Move(oldIndex, newIndex);
    }
    private async void ToggleSelectedTask()
    {
        if (_launchCoordinator.IsBusy)
        {
            _launchCoordinator.Stop();
            return;
        }

        try { await RunSingleTaskAsync(); }
        catch (OperationCanceledException)
        {
            await _log.WriteAsync(LogLevel.Warning, "任务启动已取消");
        }
        catch (Exception ex)
        {
            await _log.WriteAsync(LogLevel.Error, $"启动任务失败：{ex.Message}");
            ValidationFailed?.Invoke(ex.Message);
        }
    }
    private async Task RunSingleTaskAsync()
    {
        if (SelectedTask is null) { ValidationFailed?.Invoke("请先选择一个任务。"); return; }
        if (!await ValidateBeforeRunAsync([SelectedTask])) return;
        await _launchCoordinator.RunSingleAsync(SelectedTask, FailurePolicy);
        RaiseCommandStates();
    }
    private async Task RunScheduledTasksAsync(ScheduledLaunchBatch batch)
    {
        var scheduled = new List<AutomationTaskConfig>();
        foreach (var anchor in batch.Anchors.OrderBy(task => Tasks.IndexOf(task)))
        {
            if (scheduled.Contains(anchor)) continue;
            var index = Tasks.IndexOf(anchor);
            while (index >= 0 && index < Tasks.Count)
            {
                var task = Tasks[index];
                if (task.Enabled && !scheduled.Contains(task)) scheduled.Add(task);
                if (task.CompletionAction == TaskCompletionAction.None) break;
                index++;
                while (index < Tasks.Count && !Tasks[index].Enabled) index++;
            }
        }

        if (!await ValidateBeforeRunAsync(scheduled)) return;
        await _log.WriteAsync(LogLevel.Info, $"定时任务准备：{string.Join(" → ", scheduled.Select(task => task.Name))}；PrepareAt={batch.PrepareAt:HH:mm:ss}，LaunchAt={batch.ScheduledAt:HH:mm:ss}");
        await _launchCoordinator.RunAsync(scheduled, batch.ScheduledAt, TaskIntervalSeconds, FailurePolicy);
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
        OnPropertyChanged(nameof(ManualActionText));
        RaiseCommandStates();
    }
    private void DeleteTask() { if (SelectedTask is null) return; var index = Tasks.IndexOf(SelectedTask); Tasks.Remove(SelectedTask); SelectedTask = Tasks.ElementAtOrDefault(Math.Min(index, Tasks.Count - 1)); }
    private void DuplicateTask()
    {
        if (SelectedTask is null) return;
        var copy = new AutomationTaskConfig { Name = SelectedTask.Name + " 副本", ProgramPath = SelectedTask.ProgramPath, Arguments = SelectedTask.Arguments, WorkingDirectory = SelectedTask.WorkingDirectory, CompletionMode = SelectedTask.CompletionMode, CompletionProcessName = SelectedTask.CompletionProcessName, CompletionLogPath = SelectedTask.CompletionLogPath, CompletionKeyword = SelectedTask.CompletionKeyword, CompletionFailureKeyword = SelectedTask.CompletionFailureKeyword, MaxRunMinutes = SelectedTask.MaxRunMinutes, CleanupWaitSeconds = SelectedTask.CleanupWaitSeconds, CleanupRetries = SelectedTask.CleanupRetries, TrackChildren = SelectedTask.TrackChildren, UseJobObject = SelectedTask.UseJobObject, RunAsAdministrator = SelectedTask.RunAsAdministrator, ScheduledStartTime = SelectedTask.ScheduledStartTime, WakeBeforeTaskSeconds = SelectedTask.WakeBeforeTaskSeconds, CompletionAction = SelectedTask.CompletionAction };
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
    public async Task<bool> AddKnownToolAsync(AutomationTaskConfig profile)
    {
        if (_launchCoordinator.IsBusy) return false;
        Tasks.Add(profile);
        SelectedTask = profile;
        await SaveAsync();
        await _log.WriteAsync(LogLevel.Success, $"已添加适配任务：{profile.Name}");
        return true;
    }
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
        ((RelayCommand)ToggleExecutionCommand).RaiseCanExecuteChanged();
        ((RelayCommand)DeleteTaskCommand).RaiseCanExecuteChanged();
        ((RelayCommand)DuplicateTaskCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)ResetTaskCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)ImportToolsCommand).RaiseCanExecuteChanged();
    }
    private void TickUi()
    {
        if (CurrentSession is { } session) Elapsed = ((session.EndTime ?? DateTimeOffset.Now) - session.StartTime).ToString(@"hh\:mm\:ss");
        foreach (var task in Tasks) task.RefreshNextExecutionText();
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
    public async Task ShutdownAsync()
    {
        Dispose();
        await _launchCoordinator.DisposeAsync();
        await _notificationService.DisposeAsync();
        await SaveAsync();
        await _screenManager.DisposeAsync();
    }
}
