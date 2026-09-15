using GameOrchestrator.Models;
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

if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures.Select(x => "FAIL: " + x)));
    return 1;
}
Console.WriteLine("PASS: 任务配置校验冒烟测试全部通过");
return 0;
