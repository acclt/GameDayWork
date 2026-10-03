using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GameOrchestrator.Models;
using GameOrchestrator.Services;
using GameOrchestrator.ViewModels;

namespace GameOrchestrator.Views;

public partial class MainWindow
{
    private readonly DispatcherTimer _autoSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly SemaphoreSlim _autoSaveGate = new(1, 1);
    private readonly HashSet<AutomationTaskConfig> _watchedTasks = [];
    private readonly HashSet<ObservableCollection<AutomationTaskConfig>> _watchedCollections = [];
    private AutomationTaskConfig? _watchedDraft;
    private bool _autoSaveReady;
    private bool _autoSavePending;
    private bool _taskTreeDirty;
    private bool _flushingBindings;
    private static readonly HashSet<string> PersistentTaskProperties = typeof(AutomationTaskConfig).GetProperties()
        .Where(property => property.GetCustomAttribute<JsonIgnoreAttribute>() is null)
        .Select(property => property.Name).ToHashSet();
    private static readonly HashSet<string> SettingsProperties =
    [
        nameof(MainViewModel.StartWithWindows), nameof(MainViewModel.UseSystemService),
        nameof(MainViewModel.StartMinimizedToTray), nameof(MainViewModel.EnableScreenManager),
        nameof(MainViewModel.IdleTimeoutMinutes), nameof(MainViewModel.WakeBeforeTaskSeconds),
        nameof(MainViewModel.LockScreenDisplayTimeoutEnabled), nameof(MainViewModel.LockScreenDisplayTimeoutAcSeconds),
        nameof(MainViewModel.LockScreenDisplayTimeoutDcSeconds), nameof(MainViewModel.WeComWebhookUrl),
        nameof(MainViewModel.NotifyOnStart), nameof(MainViewModel.NotifyOnComplete), nameof(MainViewModel.NotifyOnFailure),
        nameof(MainViewModel.NotifyOnForcedStop), nameof(MainViewModel.CaptureTaskScreenshots),
        nameof(MainViewModel.RunningScreenshotDelaySeconds), nameof(MainViewModel.TaskIntervalSeconds),
        nameof(MainViewModel.FailurePolicy), nameof(MainViewModel.GenerateExecutionLog)
    ];

    private void InitializeAutoSave()
    {
        _autoSaveTimer.Tick += AutoSaveTimer_Tick;
        _viewModel.PropertyChanged += AutoSaveModelChanged;
        AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(AutoSaveTextChanged));
    }

    private void EnableAutoSave()
    {
        _autoSaveReady = true;
        WatchTaskTree();
        WatchDraft();
    }

    private void WatchTaskTree()
    {
        foreach (var task in _watchedTasks) task.PropertyChanged -= AutoSaveTaskChanged;
        foreach (var collection in _watchedCollections) collection.CollectionChanged -= AutoSaveTreeChanged;
        _watchedTasks.Clear();
        _watchedCollections.Clear();
        void WatchCollection(ObservableCollection<AutomationTaskConfig> collection)
        {
            if (!_watchedCollections.Add(collection)) return;
            collection.CollectionChanged += AutoSaveTreeChanged;
            foreach (var task in collection)
            {
                if (!_watchedTasks.Add(task)) continue;
                task.PropertyChanged += AutoSaveTaskChanged;
                WatchCollection(task.Children);
            }
        }
        WatchCollection(_viewModel.Tasks);
    }

    private void WatchDraft()
    {
        if (_watchedDraft is not null) _watchedDraft.PropertyChanged -= AutoSaveDraftChanged;
        _watchedDraft = _viewModel.EditingTask;
        if (_watchedDraft is not null) _watchedDraft.PropertyChanged += AutoSaveDraftChanged;
    }

    private void AutoSaveModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.EditingTask)) WatchDraft();
        if (_autoSaveReady && e.PropertyName == nameof(MainViewModel.Tasks)) WatchTaskTree();
        if (_page == MainPage.Settings && e.PropertyName is { } name && SettingsProperties.Contains(name)) QueueAutoSave();
    }

    private void AutoSaveTreeChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        WatchTaskTree();
        _taskTreeDirty = true;
        QueueAutoSave();
    }

    private void AutoSaveTaskChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not { } name || !PersistentTaskProperties.Contains(name)) return;
        _taskTreeDirty = true;
        QueueAutoSave();
    }

    private void AutoSaveDraftChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is { } name && PersistentTaskProperties.Contains(name)) QueueAutoSave();
    }

    private void AutoSaveTextChanged(object sender, TextChangedEventArgs e)
    {
        if (e.OriginalSource is not TextBox { IsReadOnly: false } box) return;
        var page = _page == MainPage.TaskEdit ? (DependencyObject)TaskEditPage : _page == MainPage.Settings ? GlobalSettingsPage : null;
        for (DependencyObject? parent = box; parent is not null; parent = ParentOf(parent))
            if (parent == page) { QueueAutoSave(); break; }
    }

    private void QueueAutoSave()
    {
        if (!_autoSaveReady || _exitRequested || _flushingBindings) return;
        _autoSavePending = true;
        _viewModel.AutoSaveMessage = "待自动保存";
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private async void AutoSaveTimer_Tick(object? sender, EventArgs e)
    {
        _autoSaveTimer.Stop();
        if (_navigationPending || _closingPending) return;
        await FlushAutoSaveAsync();
    }

    private async Task<bool> FlushAutoSaveAsync(bool showErrors = false)
    {
        _autoSaveTimer.Stop();
        await _autoSaveGate.WaitAsync();
        try
        {
            do
            {
                _autoSavePending = false;
                var treeDirty = _taskTreeDirty;
                _taskTreeDirty = false;
                var root = _page == MainPage.TaskEdit ? (DependencyObject)TaskEditPage : _page == MainPage.Settings ? GlobalSettingsPage : null;
                if (root is not null)
                {
                    _flushingBindings = true;
                    try { UpdateInputBindings(root); }
                    finally { _flushingBindings = false; }
                }
                if (root is not null && HasInputErrors(root)) throw new InvalidOperationException("请修正标红的输入项，修改尚未保存。");
                if (_page == MainPage.TaskEdit && _viewModel.EditingTask is { } draft)
                {
                    if (string.IsNullOrWhiteSpace(draft.Name)) throw new InvalidOperationException("任务名称不能为空。");
                    if (!string.IsNullOrWhiteSpace(draft.ScheduledStartTime) && !SchedulerService.TryParseTime(draft.ScheduledStartTime, out _))
                        throw new InvalidOperationException("请选择有效的定时启动时间。");
                }
                var taskEdited = _page == MainPage.TaskEdit && _viewModel.HasTaskEdits;
                var settingsEdited = _page == MainPage.Settings && GlobalSettingsPage.HasChanges;
                if (taskEdited || settingsEdited || treeDirty) _viewModel.AutoSaveMessage = "保存中…";
                if (taskEdited) await _viewModel.SaveTaskEditAsync(keepEditing: true);
                else if (settingsEdited) await GlobalSettingsPage.SaveChangesAsync();
                else if (treeDirty) await _viewModel.SaveAsync();
                if (taskEdited || settingsEdited || treeDirty) _viewModel.AutoSaveMessage = "已自动保存";
            } while (_autoSavePending);
            return true;
        }
        catch (Exception ex)
        {
            _taskTreeDirty = true;
            _viewModel.AutoSaveMessage = "未保存：" + ex.Message;
            if (showErrors) MessageBox.Show(this, ex.Message, "修改尚未保存", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally { _autoSaveGate.Release(); }
    }

    private void StopAutoSave()
    {
        _autoSaveReady = false;
        _autoSaveTimer.Stop();
        _viewModel.PropertyChanged -= AutoSaveModelChanged;
        foreach (var task in _watchedTasks) task.PropertyChanged -= AutoSaveTaskChanged;
        foreach (var collection in _watchedCollections) collection.CollectionChanged -= AutoSaveTreeChanged;
        if (_watchedDraft is not null) _watchedDraft.PropertyChanged -= AutoSaveDraftChanged;
    }
}
