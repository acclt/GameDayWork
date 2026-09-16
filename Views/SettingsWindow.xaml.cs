using System.Windows;
using GameOrchestrator.Services;
using GameOrchestrator.ViewModels;

namespace GameOrchestrator.Views;

public partial class SettingsWindow : Window
{
    private sealed record SettingsSnapshot(
        bool EnableScreenManager,
        int IdleTimeoutMinutes,
        int WakeBeforeTaskSeconds,
        bool AutoBlackoutAfterTask,
        string WeComWebhookUrl,
        bool NotifyOnStart,
        bool NotifyOnComplete,
        bool NotifyOnFailure,
        bool NotifyOnForcedStop);

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
            viewModel.EnableScreenManager,
            viewModel.IdleTimeoutMinutes,
            viewModel.WakeBeforeTaskSeconds,
            viewModel.AutoBlackoutAfterTask,
            viewModel.WeComWebhookUrl,
            viewModel.NotifyOnStart,
            viewModel.NotifyOnComplete,
            viewModel.NotifyOnFailure,
            viewModel.NotifyOnForcedStop);
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
            await _viewModel.SaveAsync();
            _saved = true;
            DialogResult = true;
        }
        catch (Exception ex)
        {
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
        _viewModel.EnableScreenManager = _snapshot.EnableScreenManager;
        _viewModel.IdleTimeoutMinutes = _snapshot.IdleTimeoutMinutes;
        _viewModel.WakeBeforeTaskSeconds = _snapshot.WakeBeforeTaskSeconds;
        _viewModel.AutoBlackoutAfterTask = _snapshot.AutoBlackoutAfterTask;
        _viewModel.WeComWebhookUrl = _snapshot.WeComWebhookUrl;
        _viewModel.NotifyOnStart = _snapshot.NotifyOnStart;
        _viewModel.NotifyOnComplete = _snapshot.NotifyOnComplete;
        _viewModel.NotifyOnFailure = _snapshot.NotifyOnFailure;
        _viewModel.NotifyOnForcedStop = _snapshot.NotifyOnForcedStop;
    }
}
