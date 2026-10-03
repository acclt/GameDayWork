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

    public bool HasChanges => _snapshot is not null && _snapshot != CaptureSnapshot();

    public async Task SaveChangesAsync()
    {
        if (MainWindow.HasInputErrors(this)) throw new InvalidOperationException("请修正标红的设置项。");
        if (!string.IsNullOrWhiteSpace(_viewModel.WeComWebhookUrl)
            && !WeComNotificationService.TryValidateWebhook(_viewModel.WeComWebhookUrl, out _, out var error))
            throw new InvalidOperationException(error);
        var savedSnapshot = CaptureSnapshot();
        IsSaving = true;
        try
        {
            await _viewModel.SaveSettingsAsync();
            _snapshot = savedSnapshot;
        }
        finally { IsSaving = false; }
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

    private void OpenWindowsPowerSettings_Click(object sender, RoutedEventArgs e) => _viewModel.OpenWindowsPowerSettings();
}
