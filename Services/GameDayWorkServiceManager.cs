using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace GameOrchestrator.Services;

public static class GameDayWorkServiceManager
{
    public const string ServiceName = "GameDayWorkService";
    public const string DisplayName = "GameDayWork system service";
    private static readonly string[] ScheduledTaskNames = ["GameDayWork", "GameDayWork Startup", "GameDayWork 开机自启"];

    public static bool IsAdministrator() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public static int InstallFromCommandLine(string[] args)
    {
        EnsureAdministrator();
        var serviceExecutable = Path.GetFullPath(Environment.ProcessPath ?? throw new InvalidOperationException("无法确定服务程序路径。"));
        var desktopExecutable = GetString(args, "--desktop") ?? throw new InvalidOperationException("缺少桌面端路径。");
        desktopExecutable = Path.GetFullPath(desktopExecutable);
        if (!File.Exists(desktopExecutable) || !string.Equals(Path.GetFileName(desktopExecutable), "GameDayWork.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"桌面端路径无效：{desktopExecutable}");
        var config = MachineServiceConfigStore.Load();
        config.DesktopExecutablePath = desktopExecutable;
        config.LockScreenTimeoutEnabled = GetInt(args, "--lock-enabled", 0) == 1;
        config.AcSeconds = Math.Clamp(GetInt(args, "--lock-ac", 60), 10, 3600);
        config.DcSeconds = Math.Clamp(GetInt(args, "--lock-dc", 30), 10, 3600);
        var policy = new LockScreenPowerPolicy(new WindowsLockScreenPowerApi());
        if (config.LockScreenTimeoutEnabled) policy.ApplyActiveScheme(config);
        else
        {
            var failures = policy.RestoreAll(config);
            if (failures.Count > 0) throw new AggregateException(failures.Select(message => new InvalidOperationException(message)));
        }
        MachineServiceConfigStore.Save(config);

        RemoveStartupShortcuts();
        foreach (var name in ScheduledTaskNames)
            if (ScheduledTaskExists(name)) RunProcess("schtasks.exe", ["/Delete", "/TN", name, "/F"], allowFailure: false);
        var serviceImagePath = $"\"{serviceExecutable}\" run";
        if (ServiceExists())
        {
            RunSc("stop", ServiceName, allowFailure: true);
            RunSc("config", ServiceName, "start=", "auto", "binPath=", serviceImagePath, "DisplayName=", DisplayName);
        }
        else
        {
            RunSc("create", ServiceName, "start=", "auto", "binPath=", serviceImagePath, "DisplayName=", DisplayName);
        }
        RunSc("description", ServiceName, "维护 GameDayWork 系统级电源策略，并在交互用户会话中保活桌面端。");
        RunSc("failure", ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/15000/restart/60000");
        RunSc("start", ServiceName);
        return 0;
    }

    public static int Uninstall()
    {
        EnsureAdministrator();
        var failures = new List<string>();
        var servicePathValidated = true;
        try
        {
            if (ServiceExists())
            {
                ValidateInstalledServicePath();
                TryStep(() => RunSc("stop", ServiceName, allowFailure: true), "停止服务", failures);
                TryStep(() => RunSc("delete", ServiceName), "删除服务", failures);
            }
        }
        catch (Exception ex)
        {
            servicePathValidated = false;
            failures.Add($"校验服务路径失败：{ex.Message}");
        }

        if (!servicePathValidated)
        {
            WriteManagementLog(failures);
            return 2;
        }

        foreach (var name in ScheduledTaskNames)
            if (ScheduledTaskExists(name))
                TryStep(() => RunProcess("schtasks.exe", ["/Delete", "/TN", name, "/F"], allowFailure: false), $"移除计划任务 {name}", failures);
        foreach (var path in GetStartupShortcutPaths())
            TryStep(() => { if (File.Exists(path)) File.Delete(path); }, $"移除旧开机快捷方式 {path}", failures);

        try
        {
            var config = MachineServiceConfigStore.Load();
            failures.AddRange(new LockScreenPowerPolicy(new WindowsLockScreenPowerApi()).RestoreAll(config));
            config.LockScreenTimeoutEnabled = false;
            config.DesktopExecutablePath = "";
            MachineServiceConfigStore.Save(config);
        }
        catch (Exception ex) { failures.Add($"恢复登录/锁屏息屏时间失败：{ex.Message}"); }

        if (failures.Count == 0)
        {
            TryDeleteMachineConfigDirectoryIfEmpty();
            return 0;
        }
        WriteManagementLog(failures);
        return 2;
    }

    public static int PrintStatus()
    {
        Console.WriteLine(ServiceExists() ? "installed" : "not-installed");
        if (ServiceExists())
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
            Console.WriteLine(key?.GetValue("ImagePath") as string ?? "");
        }
        return ServiceExists() ? 0 : 3;
    }

    private static void ValidateInstalledServicePath()
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}")
            ?? throw new InvalidOperationException("服务注册表项不存在。");
        var imagePath = key.GetValue("ImagePath") as string ?? "";
        var expected = Path.GetFullPath(Environment.ProcessPath ?? throw new InvalidOperationException("无法确定当前服务管理程序路径。"));
        var parsed = ServicePathSafety.ParseExecutablePath(imagePath);
        if (!string.Equals(Path.GetFullPath(parsed), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"已安装服务指向其他路径，拒绝删除。已安装：{parsed}；当前：{expected}");
    }

    private static bool ServiceExists()
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
        return key is not null;
    }

    private static void RemoveStartupShortcuts()
    {
        foreach (var path in GetStartupShortcutPaths())
            if (File.Exists(path)) File.Delete(path);
    }

    private static IEnumerable<string> GetStartupShortcutPaths()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "GameDayWork 开机自启.lnk");
        var users = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "..");
        var usersRoot = Path.GetFullPath(users);
        if (!Directory.Exists(usersRoot)) yield break;
        foreach (var profile in Directory.EnumerateDirectories(usersRoot))
            yield return Path.Combine(profile, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs", "Startup", "GameDayWork 开机自启.lnk");
    }

    private static int GetInt(string[] args, string name, int fallback)
    {
        var prefix = name + "=";
        var value = args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return value is not null && int.TryParse(value[prefix.Length..], out var parsed) ? parsed : fallback;
    }

    private static string? GetString(string[] args, string name)
    {
        var prefix = name + "=";
        var value = args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return value?[prefix.Length..].Trim('"');
    }

    private static void RunSc(params string[] arguments) => RunProcess("sc.exe", arguments, false);
    private static void RunSc(string command, string service, bool allowFailure) => RunProcess("sc.exe", [command, service], allowFailure);
    private static int RunProcess(string fileName, IReadOnlyList<string> arguments, bool allowFailure)
    {
        var start = new ProcessStartInfo(fileName) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"无法启动 {fileName}");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (!allowFailure && process.ExitCode != 0) throw new InvalidOperationException($"{fileName} 退出码 {process.ExitCode}：{stdout} {stderr}".Trim());
        return process.ExitCode;
    }

    private static bool ScheduledTaskExists(string name) =>
        RunProcess("schtasks.exe", ["/Query", "/TN", name], allowFailure: true) == 0;

    private static void EnsureAdministrator()
    {
        if (!IsAdministrator()) throw new UnauthorizedAccessException("此操作需要管理员权限。");
    }

    private static void TryStep(Action action, string description, List<string> failures)
    {
        try { action(); } catch (Exception ex) { failures.Add($"{description}失败：{ex.Message}"); }
    }

    private static void WriteManagementLog(IEnumerable<string> messages)
    {
        Directory.CreateDirectory(MachineServiceConfigStore.DirectoryPath);
        File.AppendAllLines(Path.Combine(MachineServiceConfigStore.DirectoryPath, "service-management.log"),
            messages.Select(message => $"{DateTimeOffset.Now:O} {message}"));
    }

    private static void TryDeleteMachineConfigDirectoryIfEmpty()
    {
        try
        {
            if (File.Exists(MachineServiceConfigStore.ConfigPath)) File.Delete(MachineServiceConfigStore.ConfigPath);
            if (Directory.Exists(MachineServiceConfigStore.DirectoryPath) && !Directory.EnumerateFileSystemEntries(MachineServiceConfigStore.DirectoryPath).Any())
                Directory.Delete(MachineServiceConfigStore.DirectoryPath);
        }
        catch { }
    }
}
