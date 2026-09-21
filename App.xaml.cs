using System.Windows;
using GameOrchestrator.Views;

namespace GameOrchestrator;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    internal bool ServiceManaged { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var sessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        _singleInstanceMutex = new Mutex(true, $"Local\\GameDayWork.Desktop.{sessionId}", out var createdNew);
        if (!createdNew)
        {
            Shutdown(0);
            return;
        }
        ServiceManaged = e.Args.Contains("--service-managed", StringComparer.OrdinalIgnoreCase);
        var afterLogin = e.Args.Contains("--after-login", StringComparer.OrdinalIgnoreCase);
        var afterUnlock = e.Args.Contains("--after-unlock", StringComparer.OrdinalIgnoreCase);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "未处理错误", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        try
        {
            var window = new MainWindow(ServiceManaged, afterLogin, afterUnlock, sessionId);
            MainWindow = window;
            await window.InitializeAsync();
        }
        catch (Exception ex)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "startup-error.log");
            File.WriteAllText(path, ex.ToString());
            MessageBox.Show($"程序窗口初始化失败：{ex.Message}\n\n详细信息已写入：{path}", "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
