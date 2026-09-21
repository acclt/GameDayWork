using System.Windows;
using GameOrchestrator.Views;

namespace GameOrchestrator;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private int _handlingUnhandledException;
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
        DispatcherUnhandledException += HandleDispatcherUnhandledException;
        try
        {
            var window = new MainWindow(ServiceManaged);
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

    private void HandleDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs args)
    {
        // Continuing after an arbitrary dispatcher exception can repeatedly execute the same
        // failing UI callback. Report the first failure once and shut down cleanly; a nested
        // exception must fall through to the runtime instead of opening another dialog.
        if (Interlocked.Exchange(ref _handlingUnhandledException, 1) != 0)
        {
            args.Handled = false;
            return;
        }

        args.Handled = true;
        var path = Path.Combine(AppContext.BaseDirectory, "unhandled-error.log");
        try { File.WriteAllText(path, $"{DateTimeOffset.Now:O}{Environment.NewLine}{args.Exception}"); }
        catch { /* Do not let diagnostics cause another dispatcher exception. */ }

        try
        {
            MessageBox.Show(
                $"程序遇到无法恢复的错误，将安全退出。\n\n{args.Exception.Message}\n\n详细信息：{path}",
                "GameDayWork 错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            if (MainWindow is MainWindow window) window.PrepareForFatalShutdown();
            Shutdown(-1);
        }
    }
}
