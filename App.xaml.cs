using System.Windows;
using GameOrchestrator.Services;
using GameOrchestrator.Views;

namespace GameOrchestrator;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "未处理错误", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        try
        {
            var startMinimized = e.Args.Any(argument => argument.Equals(StartupService.StartupArgument, StringComparison.OrdinalIgnoreCase));
            var window = new MainWindow(startMinimized);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "startup-error.log");
            File.WriteAllText(path, ex.ToString());
            MessageBox.Show($"程序窗口初始化失败：{ex.Message}\n\n详细信息已写入：{path}", "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }
}
