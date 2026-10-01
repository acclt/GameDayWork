using System.Windows;
using System.Windows.Controls;
using GameOrchestrator.Models;
using GameOrchestrator.Services;
using GameOrchestrator.ViewModels;

namespace GameOrchestrator.Views;

public partial class SettingsPage : UserControl
{
    private sealed record SettingsSnapshot(
        bool StartWithWindows,
        bool UseSystemService,
        bool StartMinimizedToTray,
        bool EnableScreenManager,
        int IdleTimeoutMinutes,
        int WakeBeforeTaskSeconds,
        bool LockScreenDisplayTimeoutEnabled,
        int LockScreenDisplayTimeoutAcSeconds,
        int LockScreenDisplayTimeoutDcSeconds,
        string WeComWebhookUrl,
        bool NotifyOnStart,
        bool NotifyOnComplete,
        bool NotifyOnFailure,
        bool NotifyOnForcedStop,
        bool CaptureTaskScreenshots,
        int RunningScreenshotDelaySeconds,
        int TaskIntervalSeconds,
        FailurePolicy FailurePolicy,
        bool GenerateExecutionLog);

    private MainViewModel _viewModel = null!;
    private SettingsSnapshot? _snapshot;
    public bool IsSaving { get; private set; }
    public event Action? ReturnRequested;

    public SettingsPage() => InitializeComponent();

    public void BeginEdit(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        _snapshot = CaptureSnapshot();
    }

    private SettingsSnapshot CaptureSnapshot() => new(
        _viewModel.StartWithWindows, _viewModel.UseSystemService, _viewModel.StartMinimizedToTray,
        _viewModel.EnableScreenManager, _viewModel.IdleTimeoutMinutes, _viewModel.WakeBeforeTaskSeconds,
        _viewModel.LockScreenDisplayTimeoutEnabled, _viewModel.LockScreenDisplayTimeoutAcSeconds,
        _viewModel.LockScreenDisplayTimeoutDcSeconds, _viewModel.WeComWebhookUrl,
        _viewModel.NotifyOnStart, _viewModel.NotifyOnComplete, _viewModel.NotifyOnFailure,
        _viewModel.NotifyOnForcedStop, _viewModel.CaptureTaskScreenshots,
        _viewModel.RunningScreenshotDelaySeconds, _viewModel.TaskIntervalSeconds,
        _viewModel.FailurePolicy, _viewModel.GenerateExecutionLog);

    public async Task<bool> TryLeaveAsync()
    {
        if (IsSaving) return false;
        MainWindow.UpdateInputBindings(this);
        if (_snapshot is null) return true;
        if (_snapshot != CaptureSnapshot() || MainWindow.HasInputErrors(this))
        {
            var choice = MessageBox.Show(Window.GetWindow(this), "全局设置尚未保存，是否保存后返回？", "未保存的修改",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Cancel) return false;
            if (choice == MessageBoxResult.Yes) return await TrySaveAsync();
            ApplySnapshot();
        }
        _snapshot = null;
        return true;
    }

    private async Task<bool> TrySaveAsync()
    {
        if (IsSaving) return false;
        MainWindow.UpdateInputBindings(this);
        if (MainWindow.HasInputErrors(this))
        {
            MessageBox.Show(Window.GetWindow(this), "请检查标红的输入项，填写有效数值后再保存。", "输入无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        IsSaving = true;
        try
        {
            if (!string.IsNullOrWhiteSpace(_viewModel.WeComWebhookUrl)
                && !WeComNotificationService.TryValidateWebhook(_viewModel.WeComWebhookUrl, out _, out var error))
            {
                MessageBox.Show(Window.GetWindow(this), error, "Webhook 地址无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            await _viewModel.SaveSettingsAsync();
            _snapshot = null;
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "保存设置失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally { IsSaving = false; }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        IsEnabled = false;
        try { if (await TrySaveAsync()) ReturnRequested?.Invoke(); }
        finally { IsEnabled = true; }
    }

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (await TryLeaveAsync()) ReturnRequested?.Invoke();
    }

    private async void TestNotification_Click(object sender, RoutedEventArgs e)
    {
        TestNotificationButton.IsEnabled = false;
        try
        {
            var result = await _viewModel.TestWeComNotificationAsync();
            MessageBox.Show(Window.GetWindow(this), result.Message, result.Success ? "测试成功" : "测试失败", MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "测试失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            TestNotificationButton.IsEnabled = true;
        }
    }

    private void ApplySnapshot()
    {
        if (_snapshot is null) return;
        _viewModel.StartWithWindows = _snapshot.StartWithWindows;
        _viewModel.UseSystemService = _snapshot.UseSystemService;
        _viewModel.StartMinimizedToTray = _snapshot.StartMinimizedToTray;
        _viewModel.EnableScreenManager = _snapshot.EnableScreenManager;
        _viewModel.IdleTimeoutMinutes = _snapshot.IdleTimeoutMinutes;
        _viewModel.WakeBeforeTaskSeconds = _snapshot.WakeBeforeTaskSeconds;
        _viewModel.LockScreenDisplayTimeoutEnabled = _snapshot.LockScreenDisplayTimeoutEnabled;
        _viewModel.LockScreenDisplayTimeoutAcSeconds = _snapshot.LockScreenDisplayTimeoutAcSeconds;
        _viewModel.LockScreenDisplayTimeoutDcSeconds = _snapshot.LockScreenDisplayTimeoutDcSeconds;
        _viewModel.WeComWebhookUrl = _snapshot.WeComWebhookUrl;
        _viewModel.NotifyOnStart = _snapshot.NotifyOnStart;
        _viewModel.NotifyOnComplete = _snapshot.NotifyOnComplete;
        _viewModel.NotifyOnFailure = _snapshot.NotifyOnFailure;
        _viewModel.NotifyOnForcedStop = _snapshot.NotifyOnForcedStop;
        _viewModel.CaptureTaskScreenshots = _snapshot.CaptureTaskScreenshots;
        _viewModel.RunningScreenshotDelaySeconds = _snapshot.RunningScreenshotDelaySeconds;
        _viewModel.TaskIntervalSeconds = _snapshot.TaskIntervalSeconds;
        _viewModel.FailurePolicy = _snapshot.FailurePolicy;
        _viewModel.GenerateExecutionLog = _snapshot.GenerateExecutionLog;
    }

    private void OpenWindowsPowerSettings_Click(object sender, RoutedEventArgs e) => _viewModel.OpenWindowsPowerSettings();
}
