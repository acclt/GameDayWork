using System.Windows;
using GameOrchestrator.Services;
using GameOrchestrator.ViewModels;

namespace GameOrchestrator.Views;

public partial class SettingsWindow : Window
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
        bool BlackoutAfterLogin,
        bool BlackoutAfterUnlock,
        string WeComWebhookUrl,
        bool NotifyOnStart,
        bool NotifyOnComplete,
        bool NotifyOnFailure,
        bool NotifyOnForcedStop,
        bool CaptureTaskScreenshots,
        int RunningScreenshotDelaySeconds);

    private readonly MainViewModel _viewModel;
    private readonly SettingsSnapshot _snapshot;
    private bool _saved;
    private bool _restored;

    public SettingsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _snapshot = new(
            viewModel.StartWithWindows,
            viewModel.UseSystemService,
            viewModel.StartMinimizedToTray,
            viewModel.EnableScreenManager,
            viewModel.IdleTimeoutMinutes,
            viewModel.WakeBeforeTaskSeconds,
            viewModel.LockScreenDisplayTimeoutEnabled,
            viewModel.LockScreenDisplayTimeoutAcSeconds,
            viewModel.LockScreenDisplayTimeoutDcSeconds,
            viewModel.BlackoutAfterLogin,
            viewModel.BlackoutAfterUnlock,
            viewModel.WeComWebhookUrl,
            viewModel.NotifyOnStart,
            viewModel.NotifyOnComplete,
            viewModel.NotifyOnFailure,
            viewModel.NotifyOnForcedStop,
            viewModel.CaptureTaskScreenshots,
            viewModel.RunningScreenshotDelaySeconds);
        Closing += (_, _) => RestoreSnapshotIfNeeded();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_viewModel.WeComWebhookUrl)
                && !WeComNotificationService.TryValidateWebhook(_viewModel.WeComWebhookUrl, out _, out var validationError))
            {
                MessageBox.Show(this, validationError, "Webhook 地址无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            await _viewModel.SaveSettingsAsync();
            _saved = true;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ApplySnapshot();
            MessageBox.Show(this, ex.Message, "保存设置失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        RestoreSnapshotIfNeeded();
        Close();
    }

    private async void TestNotification_Click(object sender, RoutedEventArgs e)
    {
        TestNotificationButton.IsEnabled = false;
        try
        {
            var result = await _viewModel.TestWeComNotificationAsync();
            MessageBox.Show(this, result.Message, result.Success ? "测试成功" : "测试失败", MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "测试失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            TestNotificationButton.IsEnabled = true;
        }
    }

    private void RestoreSnapshotIfNeeded()
    {
        if (_saved || _restored) return;
        _restored = true;
        ApplySnapshot();
    }

    private void ApplySnapshot()
    {
        _viewModel.StartWithWindows = _snapshot.StartWithWindows;
        _viewModel.UseSystemService = _snapshot.UseSystemService;
        _viewModel.StartMinimizedToTray = _snapshot.StartMinimizedToTray;
        _viewModel.EnableScreenManager = _snapshot.EnableScreenManager;
        _viewModel.IdleTimeoutMinutes = _snapshot.IdleTimeoutMinutes;
        _viewModel.WakeBeforeTaskSeconds = _snapshot.WakeBeforeTaskSeconds;
        _viewModel.LockScreenDisplayTimeoutEnabled = _snapshot.LockScreenDisplayTimeoutEnabled;
        _viewModel.LockScreenDisplayTimeoutAcSeconds = _snapshot.LockScreenDisplayTimeoutAcSeconds;
        _viewModel.LockScreenDisplayTimeoutDcSeconds = _snapshot.LockScreenDisplayTimeoutDcSeconds;
        _viewModel.BlackoutAfterLogin = _snapshot.BlackoutAfterLogin;
        _viewModel.BlackoutAfterUnlock = _snapshot.BlackoutAfterUnlock;
        _viewModel.WeComWebhookUrl = _snapshot.WeComWebhookUrl;
        _viewModel.NotifyOnStart = _snapshot.NotifyOnStart;
        _viewModel.NotifyOnComplete = _snapshot.NotifyOnComplete;
        _viewModel.NotifyOnFailure = _snapshot.NotifyOnFailure;
        _viewModel.NotifyOnForcedStop = _snapshot.NotifyOnForcedStop;
        _viewModel.CaptureTaskScreenshots = _snapshot.CaptureTaskScreenshots;
        _viewModel.RunningScreenshotDelaySeconds = _snapshot.RunningScreenshotDelaySeconds;
    }

    private void OpenWindowsPowerSettings_Click(object sender, RoutedEventArgs e) => _viewModel.OpenWindowsPowerSettings();
}
