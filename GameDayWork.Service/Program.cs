using System.ServiceProcess;
using GameOrchestrator.Services;

try
{
    var command = args.FirstOrDefault()?.ToLowerInvariant();
    return command switch
    {
        "install" => GameDayWorkServiceManager.InstallFromCommandLine(args),
        "uninstall" => GameDayWorkServiceManager.Uninstall(),
        "status" => GameDayWorkServiceManager.PrintStatus(),
        "run" => RunService(),
        _ => PrintUsage()
    };
}
catch (Exception ex)
{
    ServiceFileLog.Write($"服务管理失败：{ex}");
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static int RunService()
{
    ServiceBase.Run(new GameDayWorkWindowsService());
    return 0;
}

static int PrintUsage()
{
    Console.WriteLine("GameDayWork.Service install|uninstall|status|run");
    return 64;
}
