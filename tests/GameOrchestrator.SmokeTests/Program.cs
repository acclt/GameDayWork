using GameOrchestrator.Models;
using GameOrchestrator.Events;
using GameOrchestrator.Infrastructure;
using GameOrchestrator.Services;
using System.Globalization;
using System.Windows;

var validator = new TaskValidationService();
var failures = new List<string>();
void Assert(bool condition, string message) { if (!condition) failures.Add(message); }

Assert(!new AppConfig().AutoBlackoutAfterTask, "任务链结束后应返回空闲监控，不应立即进入假息屏");
Assert(!new AppConfig().StartWithWindows, "新安装默认不应自行创建开机启动项");

var empty = new AutomationTaskConfig { Name = "测试任务" };
Assert(validator.Validate([empty]).Any(x => x.Message.Contains("程序路径")), "空程序路径应校验失败");

var inferredDirectoryTask = new AutomationTaskConfig { WorkingDirectory = "旧目录" };
var inferredProgramPath = Path.Combine(Path.GetTempPath(), "tool", "sample.exe");
inferredDirectoryTask.ProgramPath = inferredProgramPath;
Assert(inferredDirectoryTask.WorkingDirectory == Path.GetDirectoryName(inferredProgramPath), "更换程序路径时应自动同步工作目录");

var valid = new AutomationTaskConfig
{
    Name = "有效任务", ProgramPath = Environment.ProcessPath!, WorkingDirectory = AppContext.BaseDirectory,
    CompletionMode = CompletionDetectionMode.MainProcessExit
};
Assert(validator.Validate([valid]).Count == 0, "有效任务不应产生校验错误");

valid.CompletionMode = CompletionDetectionMode.SpecifiedProcessExit;
Assert(validator.Validate([valid]).Any(x => x.Message.Contains("进程名称")), "指定进程模式必须填写进程名称");
valid.CompletionProcessName = "sample.exe";
Assert(validator.Validate([valid]).Count == 0, "填写指定进程名称后应通过校验");
valid.ProcessRules.Add(new ProcessRule());
Assert(validator.Validate([valid]).Any(x => x.Message.Contains("空进程规则")), "空进程规则应校验失败");

var logDirectory = Path.Combine(Path.GetTempPath(), $"GameOrchestrator-Test-{Guid.NewGuid():N}");
Directory.CreateDirectory(logDirectory);
var logPath = Path.Combine(logDirectory, "tool-20260915.log");
await File.WriteAllTextAsync(logPath, "历史完成标志\n");
var detector = new LogKeywordCompletionDetector(Path.Combine(logDirectory, "tool-*.log"), "全部完成");
await File.AppendAllTextAsync(logPath, "任务已全部完成！\n");
Assert(await detector.CheckAsync(CancellationToken.None), "日志检测器应识别本次新增的完成关键字");
var rollingDetector = new LogKeywordCompletionDetector(Path.Combine(logDirectory, "rolling-*.log"), "游戏终止");
var nextLogPath = Path.Combine(logDirectory, "rolling-20260916.log");
await File.WriteAllTextAsync(nextLogPath, "游戏终止：TestGame\n");
Assert(await rollingDetector.CheckAsync(CancellationToken.None), "日志检测器应识别运行后新建的轮转日志");
var failureDetector = new LogKeywordCompletionDetector(Path.Combine(logDirectory, "rolling-*.log"), "指令[ 一条龙 ] 执行失败");
await File.AppendAllTextAsync(nextLogPath, "指令[ 一条龙 ] 执行失败 返回状态 异常\n");
Assert(await failureDetector.CheckAsync(CancellationToken.None), "日志检测器应识别本次新增的失败关键字");
Directory.Delete(logDirectory, true);

var profiles = new KnownToolProfileService().Discover();
Assert(profiles.Select(profile => profile.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == profiles.Count, "本机工具识别结果不应重复");
var expectedProcesses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["BGI"] = "BetterGI.exe",
    ["MAA"] = "MAA.exe",
    ["ZOG"] = "OneDragon-Launcher.exe",
    ["MFA"] = "MFAAvalonia.exe",
    ["M7A"] = "March7th Assistant.exe"
};
foreach (var profile in profiles)
{
    Assert(File.Exists(profile.ProgramPath), $"识别出的 {profile.Name} 程序必须存在");
    Assert(profile.CompletionMode == CompletionDetectionMode.SpecifiedProcessExit, $"识别出的 {profile.Name} 应使用指定进程退出检测");
    Assert(expectedProcesses.TryGetValue(profile.Name, out var expectedProcess) && profile.CompletionProcessName == expectedProcess,
        $"识别出的 {profile.Name} 应监控正确的任务进程");
    Assert(profile.ProcessRules.Any(rule => rule.Monitor && rule.Cleanup && rule.AllowNameFallback &&
        string.Equals(rule.ProcessName, profile.CompletionProcessName, StringComparison.OrdinalIgnoreCase)),
        $"识别出的 {profile.Name} 应具有安全的进程监控与清理规则");
}

if (profiles.FirstOrDefault(profile => profile.Name == "MFA") is { } mfaProfile)
{
    var existingId = Guid.NewGuid();
    var existing = new AutomationTaskConfig
    {
        Id = existingId,
        Name = "MAN",
        Enabled = false,
        ProgramPath = Environment.ProcessPath!,
        CompletionMode = CompletionDetectionMode.MainProcessExit
    };
    existing.ProcessRules.Add(new ProcessRule { ProcessName = "custom.exe", AllowNameFallback = true });
    KnownToolProfileService.ApplyRecommendedSettings(existing, mfaProfile);
    Assert(existing.Id == existingId && !existing.Enabled, "应用推荐适配时应保留任务 ID 和启用状态");
    Assert(existing.Name == "MFA" && existing.CompletionMode == CompletionDetectionMode.SpecifiedProcessExit, "已有 MFA 任务也应更新为指定进程退出检测");
    Assert(existing.CompletionProcessName == "MFAAvalonia.exe", "MFA 应监控其实际任务进程");
    Assert(existing.WorkingDirectory == mfaProfile.WorkingDirectory, "应用推荐适配时应同步程序工作目录");
    Assert(existing.ProcessRules.Any(rule => rule.ProcessName == "custom.exe") && existing.ProcessRules.Any(rule => rule.ExecutableDirectory == existing.WorkingDirectory), "应用推荐适配时应保留自定义规则并加入安装目录规则");
}

var processMonitor = new ProcessMonitorService();
var emptySession = new RuntimeSession { RootPid = 0 };
Assert(processMonitor.Scan(emptySession, valid).Count == 0, "未启动成功时不得把 PID 0 当作任务进程");

var commandProcessor = Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
if (File.Exists(commandProcessor))
{
    AutomationTaskConfig CreateShortProcessTask(string name) => new()
    {
        Name = name,
        ProgramPath = commandProcessor,
        Arguments = "/d /s /c \"ping.exe 127.0.0.1 -n 3 > nul\"",
        WorkingDirectory = Environment.SystemDirectory,
        CompletionMode = CompletionDetectionMode.SpecifiedProcessExit,
        CompletionProcessName = "cmd.exe",
        MaxRunMinutes = 1,
        CleanupWaitSeconds = 1,
        CleanupRetries = 1,
        TrackChildren = true,
        UseJobObject = false
    };

    var firstProcessTask = CreateShortProcessTask("进程退出测试一");
    var secondProcessTask = CreateShortProcessTask("进程退出测试二");
    var processEvents = new TaskEventBus();
    var completionOrder = new List<string>();
    processEvents.Subscribe<TaskCompletedEvent>(message => completionOrder.Add(message.Session.TaskName));
    var processLog = new LoggingService();
    var processRunner = new TaskRunnerService(processMonitor, new ProcessCleanupService(processMonitor, processLog), processLog, processEvents);
    var processQueue = new TaskQueueService(processRunner, processLog, processEvents);
    await processQueue.RunAsync([firstProcessTask, secondProcessTask], 0, FailurePolicy.ForceCleanupAndContinue);
    Assert(processQueue.Status == QueueRunStatus.Completed, "指定进程退出后队列应正常完成");
    Assert(firstProcessTask.Status == TaskRunStatus.Completed && secondProcessTask.Status == TaskRunStatus.Completed,
        "指定进程退出后应自动执行并完成下一项");
    Assert(completionOrder.SequenceEqual([firstProcessTask.Name, secondProcessTask.Name]),
        "指定进程退出后的任务完成顺序应保持不变");
}

var visibilityConverter = new EnumEqualsToVisibilityConverter();
Assert((Visibility)visibilityConverter.Convert(CompletionDetectionMode.LogKeyword, typeof(Visibility), "LogKeyword", CultureInfo.InvariantCulture) == Visibility.Visible, "匹配的完成检测方式应显示对应字段");
Assert((Visibility)visibilityConverter.Convert(CompletionDetectionMode.MainProcessExit, typeof(Visibility), "LogKeyword", CultureInfo.InvariantCulture) == Visibility.Collapsed, "不匹配的完成检测方式应隐藏对应字段");

var integrationName = args.FirstOrDefault(value => value.StartsWith("--integration=", StringComparison.OrdinalIgnoreCase))?.Split('=', 2)[1];
if (!string.IsNullOrWhiteSpace(integrationName))
{
    var task = profiles.FirstOrDefault(profile => string.Equals(profile.Name, integrationName, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"未识别到 {integrationName}，无法执行真实集成测试");
    var integrationLog = new LoggingService();
    integrationLog.EntryWritten += entry => Console.WriteLine($"{entry.TimeText} [{entry.LevelText}] {entry.Message}");
    var integrationMonitor = new ProcessMonitorService();
    var runner = new TaskRunnerService(integrationMonitor, new ProcessCleanupService(integrationMonitor, integrationLog), integrationLog, new TaskEventBus());
    Console.WriteLine($"INTEGRATION: 即将运行 {task.ProgramPath} {task.Arguments}");
    var result = await runner.RunAsync(task, CancellationToken.None);
    Assert(result.Success, $"{task.Name} 真实运行失败：{result.Error ?? result.Session.ExitReason}");
    Console.WriteLine($"INTEGRATION: {task.Name} 结束状态={result.Session.Status}，原因={result.Session.ExitReason}");
}

if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures.Select(x => "FAIL: " + x)));
    return 1;
}
Console.WriteLine("PASS: 任务配置校验冒烟测试全部通过");
return 0;
