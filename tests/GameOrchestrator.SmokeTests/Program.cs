using GameOrchestrator.Models;
using GameOrchestrator.Events;
using GameOrchestrator.Services;

var validator = new TaskValidationService();
var failures = new List<string>();
void Assert(bool condition, string message) { if (!condition) failures.Add(message); }

var empty = new AutomationTaskConfig { Name = "测试任务" };
Assert(validator.Validate([empty]).Any(x => x.Message.Contains("程序路径")), "空程序路径应校验失败");

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
foreach (var profile in profiles)
{
    Assert(File.Exists(profile.ProgramPath), $"识别出的 {profile.Name} 程序必须存在");
    Assert(profile.CompletionMode == CompletionDetectionMode.LogKeyword, $"识别出的 {profile.Name} 应使用日志完成检测");
    Assert(LogKeywordCompletionDetector.HasMatchingFile(profile.CompletionLogPath), $"识别出的 {profile.Name} 日志模式必须有效");
}

var processMonitor = new ProcessMonitorService();
var emptySession = new RuntimeSession { RootPid = 0 };
Assert(processMonitor.Scan(emptySession, valid).Count == 0, "未启动成功时不得把 PID 0 当作任务进程");

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
