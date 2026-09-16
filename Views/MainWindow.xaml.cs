using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using GameOrchestrator.Models;
using GameOrchestrator.ViewModels;

namespace GameOrchestrator.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private Point _dragStart;
    private bool _isClosing;
    private bool _closeAfterSave;
    public MainWindow()
    {
        InitializeComponent(); DataContext = _viewModel;
        Loaded += async (_, _) =>
        {
            await _viewModel.InitializeAsync();
            _viewModel.Logs.CollectionChanged += LogsChanged;
            _viewModel.ValidationFailed += ShowValidationErrors;
            _viewModel.NoticeRequested += ShowNotice;
            WirePlaceholderControls();
        };
        Closing += MainWindow_Closing;
    }
    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeAfterSave) return;
        e.Cancel = true;
        if (_isClosing) return;
        _isClosing = true;
        try { await _viewModel.SaveAsync(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"关闭前保存配置失败：{ex}"); }
        finally
        {
            _viewModel.Dispose();
            _closeAfterSave = true;
            Close();
        }
    }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*" };
        if (dialog.ShowDialog(this) == true && _viewModel.SelectedTask is { } task) task.ProgramPath = dialog.FileName;
    }
    private void BrowseLog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "日志文件 (*.log;*.txt)|*.log;*.txt|所有文件 (*.*)|*.*" };
        if (dialog.ShowDialog(this) == true && _viewModel.SelectedTask is { } task) task.CompletionLogPath = dialog.FileName;
    }
    private async void Schedule_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ScheduleWindow(_viewModel.Schedule) { Owner = this };
        if (dialog.ShowDialog() == true) await _viewModel.ApplyScheduleAsync();
    }
    private void ClearLogs_Click(object sender, RoutedEventArgs e) => _viewModel.Logs.Clear();
    private void ShowValidationErrors(string message) => MessageBox.Show(this, message, "无法开始执行", MessageBoxButton.OK, MessageBoxImage.Warning);
    private void ShowNotice(string message) => MessageBox.Show(this, message, "本机工具识别", MessageBoxButton.OK, MessageBoxImage.Information);
    private void WirePlaceholderControls()
    {
        foreach (var button in FindVisualChildren<Button>(this))
        {
            var text = button.Content?.ToString();
            if (text?.Contains("运行一次") == true) button.Command = _viewModel.RunOnceCommand;
            else if (text == "重置") button.Command = _viewModel.ResetTaskCommand;
        }
        foreach (var combo in FindVisualChildren<ComboBox>(this))
        {
            if (combo.Items.Count > 0 && combo.Items[0] is ComboBoxItem item && item.Content?.ToString() == "全部")
            {
                foreach (var level in new[] { "INFO", "SUCCESS", "WARNING", "ERROR" }) combo.Items.Add(new ComboBoxItem { Content = level });
                combo.SelectionChanged += LogFilter_SelectionChanged;
            }
            else if (combo.ItemsSource is not null) ApplyEnumTemplate(combo);
        }
    }
    private void LogFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = ((sender as ComboBox)?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "全部";
        CollectionViewSource.GetDefaultView(_viewModel.Logs).Filter = item => selected == "全部" || item is LogEntry entry && entry.LevelText == selected;
    }
    private static void ApplyEnumTemplate(ComboBox combo)
    {
#pragma warning disable CS0618
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding { Converter = new Infrastructure.EnumDisplayConverter() });
        combo.ItemTemplate = new DataTemplate { VisualTree = text };
#pragma warning restore CS0618
    }
    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }
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
    private void AddTask_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = sender as Button,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            MinWidth = 220
        };
        var profiles = _viewModel.DiscoverKnownTools();
        foreach (var profile in profiles)
        {
            var alreadyAdded = _viewModel.ContainsKnownTool(profile.Name);
            var item = new MenuItem
            {
                Header = alreadyAdded ? $"{profile.Name}（已添加）" : profile.Name,
                IsEnabled = !alreadyAdded,
                Tag = profile
            };
            item.Click += AddKnownTool_Click;
            menu.Items.Add(item);
        }
        if (profiles.Count == 0)
            menu.Items.Add(new MenuItem { Header = "未找到已适配的软件", IsEnabled = false });
        menu.IsOpen = true;
    }
    private async void AddKnownTool_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is AutomationTaskConfig profile)
            await _viewModel.AddKnownToolAsync(profile);
    }
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
