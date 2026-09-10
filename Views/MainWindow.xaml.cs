using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using GameOrchestrator.Models;
using GameOrchestrator.ViewModels;

namespace GameOrchestrator.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private Point _dragStart;
    public MainWindow()
    {
        InitializeComponent(); DataContext = _viewModel;
        Loaded += async (_, _) => { await _viewModel.InitializeAsync(); _viewModel.Logs.CollectionChanged += LogsChanged; };
        Closing += (_, _) => { _viewModel.SaveAsync().GetAwaiter().GetResult(); _viewModel.Dispose(); };
    }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*" };
        if (dialog.ShowDialog(this) == true && _viewModel.SelectedTask is { } task) task.ProgramPath = dialog.FileName;
    }
    private async void Schedule_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ScheduleWindow(_viewModel.Schedule) { Owner = this };
        if (dialog.ShowDialog() == true) await _viewModel.ApplyScheduleAsync();
    }
    private void Policy_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this, "请在左下角“异常时”下拉框中选择执行策略。", "执行策略", MessageBoxButton.OK, MessageBoxImage.Information);
    private void ClearLogs_Click(object sender, RoutedEventArgs e) => _viewModel.Logs.Clear();
    private async void SaveConfig_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.SaveAsync();
        MessageBox.Show(this, "任务配置已保存。", "保存配置", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private void ExportLogs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "文本日志 (*.txt)|*.txt", FileName = $"GameOrchestrator-{DateTime.Now:yyyyMMdd-HHmmss}.txt" };
        if (dialog.ShowDialog(this) == true) File.WriteAllLines(dialog.FileName, _viewModel.Logs.Select(x => $"{x.Time:O} [{x.LevelText}] {x.Message}"));
    }
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void LogsChanged(object? sender, NotifyCollectionChangedEventArgs e) { if (_viewModel.Logs.Count > 0) LogList.ScrollIntoView(_viewModel.Logs[^1]); }
    private void TaskList_MouseDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(TaskList);
    private void TaskList_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || (e.GetPosition(TaskList) - _dragStart).Length < SystemParameters.MinimumHorizontalDragDistance) return;
        if (FindItem(e.OriginalSource as DependencyObject) is { } item) DragDrop.DoDragDrop(TaskList, item, DragDropEffects.Move);
    }
    private void TaskList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(AutomationTaskConfig)) is AutomationTaskConfig source && FindItem(e.OriginalSource as DependencyObject) is { } target) _viewModel.Reorder(source, target);
    }
    private static AutomationTaskConfig? FindItem(DependencyObject? source)
    {
        while (source is not null && source is not ListBoxItem) source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        return (source as ListBoxItem)?.DataContext as AutomationTaskConfig;
    }
}
