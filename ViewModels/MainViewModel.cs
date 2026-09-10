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
    private readonly TaskQueueService _queue;
    private AppConfig _config = new();
    private AutomationTaskConfig? _selectedTask;
    private RuntimeSession? _currentSession;
    private string _statusText = "空闲中";
    private string _elapsed = "00:00:00";
    private readonly DispatcherTimer _uiTimer;

    public ObservableCollection<AutomationTaskConfig> Tasks => _config.Tasks;
    public ObservableCollection<LogEntry> Logs { get; } = [];
    public AutomationTaskConfig? SelectedTask { get => _selectedTask; set => SetProperty(ref _selectedTask, value); }
    public RuntimeSession? CurrentSession { get => _currentSession; private set { if (SetProperty(ref _currentSession, value)) RaiseRuntimeProperties(); } }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string Elapsed { get => _elapsed; private set => SetProperty(ref _elapsed, value); }
    public string NextRunText => _scheduler.NextRun is { } next ? next.ToString("yyyy/MM/dd HH:mm") : "未启用";
    public int TaskIntervalSeconds { get => _config.TaskIntervalSeconds; set { _config.TaskIntervalSeconds = Math.Max(0, value); OnPropertyChanged(); } }
    public FailurePolicy FailurePolicy { get => _config.FailurePolicy; set { _config.FailurePolicy = value; OnPropertyChanged(); } }
    public ScheduleConfig Schedule => _config.Schedule;
    public Array FailurePolicies => Enum.GetValues(typeof(FailurePolicy));
    public Array CompletionModes => Enum.GetValues(typeof(CompletionDetectionMode));
    public string CurrentTaskName => CurrentSession?.TaskName ?? "暂无任务运行";
    public string CurrentStage => CurrentSession?.Status.ToString().ToUpperInvariant() ?? "IDLE";
    public string RootPid => CurrentSession?.RootPid > 0 ? CurrentSession.RootPid.ToString() : "—";
    public int RelatedCount => CurrentSession?.TrackedProcesses.Count ?? 0;
    public string RecentEvent => Logs.LastOrDefault()?.Message ?? "点击“开始执行”或等待定时任务";

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand AddTaskCommand { get; }
    public ICommand DeleteTaskCommand { get; }
    public ICommand DuplicateTaskCommand { get; }
    public ICommand AddRuleCommand { get; }
    public ICommand DeleteRuleCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand OpenLogsCommand { get; }

    public MainViewModel()
    {
        var bus = new TaskEventBus();
        var monitor = new ProcessMonitorService();
        var cleanup = new ProcessCleanupService(monitor, _log);
        var runner = new TaskRunnerService(monitor, cleanup, _log, bus);
        _queue = new TaskQueueService(runner, _log, bus);
        runner.SessionChanged += session => Application.Current.Dispatcher.Invoke(() => CurrentSession = session);
        _log.EntryWritten += entry => Application.Current.Dispatcher.Invoke(() => { Logs.Add(entry); if (Logs.Count > 2000) Logs.RemoveAt(0); OnPropertyChanged(nameof(RecentEvent)); });
        _queue.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TaskQueueService.Status)) Application.Current.Dispatcher.Invoke(UpdateQueueStatus); };
        StartCommand = new AsyncRelayCommand(RunQueueAsync, () => !_queue.IsRunning);
        StopCommand = new RelayCommand(() => _queue.Stop(), () => _queue.IsRunning);
        AddTaskCommand = new RelayCommand(AddTask);
        DeleteTaskCommand = new RelayCommand(DeleteTask, () => SelectedTask is not null && !_queue.IsRunning);
        DuplicateTaskCommand = new RelayCommand(DuplicateTask, () => SelectedTask is not null && !_queue.IsRunning);
        AddRuleCommand = new RelayCommand(() => SelectedTask?.ProcessRules.Add(new ProcessRule { ProcessName = "Process.exe" }));
        DeleteRuleCommand = new RelayCommand(() => { if (SelectedTask?.ProcessRules.Count > 0) SelectedTask.ProcessRules.RemoveAt(SelectedTask.ProcessRules.Count - 1); });
        MoveUpCommand = new RelayCommand(() => MoveSelected(-1));
        MoveDownCommand = new RelayCommand(() => MoveSelected(1));
        OpenLogsCommand = new RelayCommand(OpenLogs);
        _uiTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => TickUi(), Application.Current.Dispatcher);
    }

    public async Task InitializeAsync()
    {
        _config = await _configService.LoadAsync();
        OnPropertyChanged(nameof(Tasks)); OnPropertyChanged(nameof(TaskIntervalSeconds)); OnPropertyChanged(nameof(FailurePolicy)); OnPropertyChanged(nameof(Schedule));
        SelectedTask = Tasks.FirstOrDefault();
        _scheduler.Start(_config.Schedule, RunQueueAsync);
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
    private async Task RunQueueAsync()
    {
        await SaveAsync(); await _queue.RunAsync(Tasks, TaskIntervalSeconds, FailurePolicy);
        ((AsyncRelayCommand)StartCommand).RaiseCanExecuteChanged(); ((RelayCommand)StopCommand).RaiseCanExecuteChanged();
    }
    private void UpdateQueueStatus()
    {
        StatusText = _queue.Status switch { QueueRunStatus.Running => "执行中", QueueRunStatus.Stopping => "正在停止", QueueRunStatus.Completed => "已完成", QueueRunStatus.Failed => "执行失败", _ => "空闲中" };
        ((AsyncRelayCommand)StartCommand).RaiseCanExecuteChanged(); ((RelayCommand)StopCommand).RaiseCanExecuteChanged();
    }
    private void AddTask() { var task = new AutomationTaskConfig(); Tasks.Add(task); SelectedTask = task; }
    private void DeleteTask() { if (SelectedTask is null) return; var index = Tasks.IndexOf(SelectedTask); Tasks.Remove(SelectedTask); SelectedTask = Tasks.ElementAtOrDefault(Math.Min(index, Tasks.Count - 1)); }
    private void DuplicateTask()
    {
        if (SelectedTask is null) return;
        var copy = new AutomationTaskConfig { Name = SelectedTask.Name + " 副本", ProgramPath = SelectedTask.ProgramPath, Arguments = SelectedTask.Arguments, WorkingDirectory = SelectedTask.WorkingDirectory, CompletionMode = SelectedTask.CompletionMode, CompletionProcessName = SelectedTask.CompletionProcessName, MaxRunMinutes = SelectedTask.MaxRunMinutes, CleanupWaitSeconds = SelectedTask.CleanupWaitSeconds, CleanupRetries = SelectedTask.CleanupRetries, TrackChildren = SelectedTask.TrackChildren, UseJobObject = SelectedTask.UseJobObject };
        foreach (var rule in SelectedTask.ProcessRules) copy.ProcessRules.Add(new ProcessRule { ProcessName = rule.ProcessName, ExecutablePath = rule.ExecutablePath, Monitor = rule.Monitor, Cleanup = rule.Cleanup, AllowNameFallback = rule.AllowNameFallback });
        Tasks.Insert(Tasks.IndexOf(SelectedTask) + 1, copy); SelectedTask = copy;
    }
    private void MoveSelected(int offset) { if (SelectedTask is null) return; var from = Tasks.IndexOf(SelectedTask); var to = from + offset; if (to >= 0 && to < Tasks.Count) Tasks.Move(from, to); }
    private void TickUi()
    {
        if (CurrentSession is { } session) Elapsed = ((session.EndTime ?? DateTimeOffset.Now) - session.StartTime).ToString(@"hh\:mm\:ss");
        OnPropertyChanged(nameof(NextRunText)); RaiseRuntimeProperties();
    }
    private void RaiseRuntimeProperties() { OnPropertyChanged(nameof(CurrentTaskName)); OnPropertyChanged(nameof(CurrentStage)); OnPropertyChanged(nameof(RootPid)); OnPropertyChanged(nameof(RelatedCount)); }
    private void OpenLogs() { Directory.CreateDirectory(_log.LogDirectory); Process.Start(new ProcessStartInfo("explorer.exe", _log.LogDirectory) { UseShellExecute = true }); }
    public void Dispose() { _scheduler.Dispose(); _uiTimer.Stop(); }
}
