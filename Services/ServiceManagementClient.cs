using System.Diagnostics;
using System.IO.Pipes;
using Microsoft.Win32;

namespace GameOrchestrator.Services;

public sealed record ServiceOperationResult(bool Success, string Message);

public static class ServiceManagementClient
{
    public static string ServiceExecutablePath => Path.Combine(AppContext.BaseDirectory, "GameDayWork.Service.exe");

    public static bool NeedsRepair()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\GameDayWorkService");
            var imagePath = key?.GetValue("ImagePath") as string;
            return string.IsNullOrWhiteSpace(imagePath)
                || !string.Equals(Path.GetFullPath(ServicePathSafety.ParseExecutablePath(imagePath)), Path.GetFullPath(ServiceExecutablePath), StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }
    }

    public static async Task NotifyIntentionalExitAsync(int sessionId)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", "GameDayWork.Service.Control", PipeDirection.Out, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(1500);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            await writer.WriteLineAsync($"intentional-exit {sessionId}");
        }
        catch { /* 退出码 77 仍是由服务启动实例的后备信号。 */ }
    }

    public static async Task<ServiceOperationResult> RunElevatedAsync(bool install, bool lockEnabled, int acSeconds, int dcSeconds)
    {
        if (!File.Exists(ServiceExecutablePath))
            return new(false, "当前便携包缺少 GameDayWork.Service.exe，无法管理系统服务。");
        var arguments = install
            ? $"install --desktop=\"{Environment.ProcessPath}\" --lock-enabled={(lockEnabled ? 1 : 0)} --lock-ac={Math.Clamp(acSeconds, 10, 3600)} --lock-dc={Math.Clamp(dcSeconds, 10, 3600)}"
            : "uninstall";
        using var process = Process.Start(new ProcessStartInfo(ServiceExecutablePath, arguments) { UseShellExecute = true, Verb = "runas" })
            ?? throw new InvalidOperationException("无法启动管理员服务管理程序。");
        await process.WaitForExitAsync();
        return process.ExitCode == 0
            ? new(true, install ? "系统服务和登录/锁屏息屏策略已更新。" : "系统服务已卸载，登录/锁屏息屏策略已恢复。")
            : new(false, $"服务管理程序失败（退出码 {process.ExitCode}）。请查看 {MachineServiceConfigStore.DirectoryPath} 中的管理日志。");
    }
}
