using System.Diagnostics;
using System.Text.Json;
using GameOrchestrator.Events;
using GameOrchestrator.Models;
using GameOrchestrator.Services;

internal static class MuMuCleanupTests
{
    public static async Task RunAsync(Action<bool, string> assert)
    {
        var log = new LoggingService { FileLoggingEnabled = false };
        var fixture = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "GameOrchestrator-MuMuFixture-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(fixture);
        try
        {
            var root = Path.Combine(fixture, "MuMu");
            var hypervisor = Path.Combine(fixture, "Hypervisor", "MuMuVMMManage.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(hypervisor)!);
            await File.WriteAllTextAsync(hypervisor, "fixture only");
            Directory.CreateDirectory(Path.Combine(root, "shell"));
            await File.WriteAllTextAsync(Path.Combine(root, "shell", "MuMuManager.exe"), "fixture only");
            var vm = Path.Combine(root, "vms", "MuMuPlayer-12.0-0"); Directory.CreateDirectory(vm);
            var uuid = Guid.NewGuid();
            await File.WriteAllTextAsync(Path.Combine(vm, "MuMuPlayer-12.0-0.nemu"), $"<VirtualBox><Machine uuid='{{{uuid}}}'><Forwarding hostport='16384'/></Machine></VirtualBox>");
            var target = new MuMuTarget(root, "0", uuid, hypervisor);
            var platform = new FakeMuMuPlatform(target);
            var resolver = new MuMuTargetResolver();

            var maa = Path.Combine(fixture, "MAA"); Directory.CreateDirectory(Path.Combine(maa, "config"));
            var maaFile = Path.Combine(maa, "config", "gui.new.json");
            await File.WriteAllTextAsync(maaFile, JsonSerializer.Serialize(new
            {
                Current = "Selected", Configurations = new Dictionary<string, object>
                {
                    ["Selected"] = new { Gui = new { ConnectSettings = new { Config = "MuMuEmulator12", Address = "127.0.0.1:16384" }, StartUpSettings = new { EmulatorPath = Path.Combine(root, "shell", "MuMuPlayer.exe") } } }
                }
            }));
            var maaTask = new AutomationTaskConfig { ToolType = "MAA", ProgramPath = Path.Combine(maa, "MAA.exe") };
            assert(resolver.Resolve(maaTask, await platform.ProcessesAsync(default))?.VmId == uuid, "MAA 应从当前配置的 ADB 端口绑定具体 MuMu 虚拟机");
            maaTask.ToolType = "MMA";
            assert(resolver.Resolve(maaTask, await platform.ProcessesAsync(default))?.VmId == uuid, "MMA 别名应使用 MAA 的 MuMu 适配");
            maaTask.ToolType = "自定义任务"; maaTask.Name = "MAA";
            assert(resolver.Resolve(maaTask, await platform.ProcessesAsync(default)) is null, "不得仅凭任务名称对自定义任务执行 MuMu 清理");

            var mfa = Path.Combine(fixture, "MFA"); var instances = Path.Combine(mfa, "config", "instances"); Directory.CreateDirectory(instances);
            var settings = Path.Combine(mfa, "appsettings.json");
            await File.WriteAllTextAsync(settings, "{\"Instances.LastActiveName\":\"Selected\"}");
            await File.WriteAllTextAsync(Path.Combine(instances, "one.json"), JsonSerializer.Serialize(new
            {
                InstanceName = "Selected", AdbDevice = new { Name = "MuMu模拟器12", AdbSerial = "127.0.0.1:16385", Config = JsonSerializer.Serialize(new { extras = new { mumu = new { enable = true, index = 0, path = root } } }) }
            }));
            await File.WriteAllTextAsync(Path.Combine(instances, "other.json"), "{\"InstanceName\":\"Other\"}");
            var mfaTask = new AutomationTaskConfig { ToolType = "MFA", ProgramPath = Path.Combine(mfa, "MFAAvalonia.exe") };
            assert(resolver.Resolve(mfaTask, await platform.ProcessesAsync(default))?.VmId == uuid, "MFA 应读取当前实例和明确的 MuMu index，兼容16385连接");
            await File.WriteAllTextAsync(settings, "{}");
            var ambiguousRejected = false;
            try { resolver.Resolve(mfaTask, await platform.ProcessesAsync(default)); } catch (InvalidOperationException) { ambiguousRejected = true; }
            assert(ambiguousRejected, "MFA 多配置无法确定当前实例时必须拒绝猜测");

            var service = new MuMuCleanupService(log, platform);
            var closed = await service.CloseInstanceAsync(target, default);
            assert(closed.Success && !platform.TargetRunning && platform.SharedRunning, "正常关闭先退出目标虚拟机，不提前结束共用服务");
            var finished = await service.FinishCleanupAsync(target, default);
            assert(finished.Success && !platform.SharedRunning, "全部虚拟机退出后应清理任务前已存在的共用服务");
            assert(platform.Commands.Any(args => args.SequenceEqual(new[] { "control", "-v", "0", "shutdown" })), "正常关闭必须指定实例，不使用 all");

            platform = new(target) { ShutdownWorks = false, AcpiWorks = true };
            service = new(log, platform);
            assert((await service.CloseInstanceAsync(target, default)).Success && platform.Commands.Any(args => args.Last() == "acpipowerbutton"),
                "管理器假报成功但虚拟机仍运行时应使用 ACPI 并核实退出");

            platform = new(target) { ShutdownWorks = false, AcpiWorks = false, PowerOffWorks = false, OtherRunning = true };
            service = new(log, platform);
            assert((await service.CloseInstanceAsync(target, default)).Success && platform.Terminated.SequenceEqual(new[] { FakeMuMuPlatform.VmPid }), "兜底仅终止匹配目标 UUID 和完整路径的进程");
            assert((await service.FinishCleanupAsync(target, default)).Success && platform.OtherRunning && platform.SharedRunning, "其他实例运行时必须保留其他虚拟机及共用服务");

            platform = new(target) { ShutdownWorks = false, AcpiWorks = false, PowerOffWorks = true };
            service = new(log, platform);
            assert((await service.CloseInstanceAsync(target, default)).Success && platform.Terminated.Count == 0, "poweroff 成功并验证退出时不再强杀进程");

            platform = new(target) { ShutdownWorks = false, AcpiWorks = false, PowerOffWorks = false, KillAllowed = false };
            service = new(log, platform);
            assert(!(await service.CloseInstanceAsync(target, default)).Success && !(await service.FinishCleanupAsync(target, default)).Success,
                "关闭及精确清理都失败时不得报告清理完成");
            assert(platform.SharedRunning, "目标虚拟机未退出时不得清理辅助服务");

            platform = new(target) { TargetRunning = false, ListWorks = false };
            service = new(log, platform);
            assert(!(await service.CloseInstanceAsync(target, default)).Success && platform.Terminated.Count == 0, "无法读取虚拟机状态时不得仅凭进程消失判定成功");
            platform = new(target) { TargetRunning = false, InvalidListOutput = true };
            service = new(log, platform);
            assert(!(await service.CloseInstanceAsync(target, default)).Success, "控制工具返回零退出码但输出格式异常时不得判定虚拟机退出");

            platform = new(target) { TargetRunning = false };
            service = new(log, platform);
            assert((await service.CloseInstanceAsync(target, default)).Success, "任务已经自行关闭虚拟机时清理应幂等");
            platform.TargetRunning = true;
            assert(!(await service.FinishCleanupAsync(target, default)).Success && platform.SharedRunning, "通用清理后虚拟机重新启动必须报告验证失败");

            platform = new(target) { TargetRunning = false, SharedPathOverride = Path.Combine(fixture, "OtherInstall", "MuMuPlayerService.exe") };
            service = new(log, platform);
            assert((await service.FinishCleanupAsync(target, default)).Success && platform.SharedRunning && platform.Terminated.Count == 0, "不得按进程名结束其他安装目录的辅助服务");

            platform = new(target) { TargetRunning = false, KillAllowed = false };
            service = new(log, platform);
            assert(!(await service.FinishCleanupAsync(target, default)).Success, "辅助服务身份无法确认或退出失败时必须报告失败");

            // Use only a short-lived cmd owned by this test; all MuMu operations remain fake.
            var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
            if (File.Exists(cmd))
            {
                platform = new(target);
                var events = new TaskEventBus();
                events.Subscribe<TaskStartedEvent>(message => platform.RealTaskPid = message.Session.RootPid);
                var runner = new TaskRunnerService(new ProcessMonitorService(), new ProcessCleanupService(new ProcessMonitorService(), log), log, events,
                    mumu: new MuMuCleanupService(log, platform, new FixedResolver(target)));
                var task = new AutomationTaskConfig { ToolType = "MFA", ProgramPath = cmd, Arguments = "/d /s /c \"ping.exe 127.0.0.1 -n 30 > nul\"", UseJobObject = false, CleanupWaitSeconds = 1, CleanupRetries = 1 };
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                var result = await runner.RunAsync(task, stop.Token);
                assert(result.Session.Status == TaskRunStatus.Stopped && result.Session.MuMuVmId == uuid, "手动停止流程应保留绑定的 MuMu 实例记录");
                assert(!platform.TargetRunning && !platform.SharedRunning && !platform.SharedStoppedBeforeTaskExit, "任务停止时先关虚拟机，再清理任务进程，最后结束共用服务");
                assert(result.Session.SnapshotTerminatedProcesses().Any(item => item.Method == "MuMuSharedService"), "MuMu 清理结果必须写入本次运行的终止记录");
            }
        }
        finally
        {
            var expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (Path.GetDirectoryName(fixture) != expectedParent || !Path.GetFileName(fixture).StartsWith("GameOrchestrator-MuMuFixture-", StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup path is outside the exact temp test scope");
            Directory.Delete(fixture, true);
        }
    }

    private sealed class FixedResolver(MuMuTarget target) : IMuMuTargetResolver
    {
        public MuMuTarget? Resolve(AutomationTaskConfig task, IReadOnlyList<MuMuProcess> processes) => target;
    }

    private sealed class FakeMuMuPlatform(MuMuTarget target) : IMuMuPlatform
    {
        public const int VmPid = 41001;
        public bool TargetRunning = true, OtherRunning, SharedRunning = true;
        public bool ShutdownWorks = true, AcpiWorks, PowerOffWorks, KillAllowed = true, ListWorks = true;
        public bool InvalidListOutput;
        public string? SharedPathOverride;
        public int RealTaskPid;
        public bool SharedStoppedBeforeTaskExit;
        public List<int> Terminated { get; } = [];
        public List<string[]> Commands { get; } = [];
        private readonly DateTimeOffset _start = DateTimeOffset.UtcNow.AddDays(-3);
        private readonly Guid _otherId = Guid.NewGuid();
        public Task<IReadOnlyList<MuMuProcess>> ProcessesAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var result = new List<MuMuProcess>();
            var executable = Path.Combine(Path.GetDirectoryName(target.Hypervisor)!, "MuMuVMMHeadless.exe");
            if (TargetRunning) result.Add(new(VmPid, "MuMuVMMHeadless.exe", executable, $"--startvm {target.VmId:D}", _start));
            if (OtherRunning) result.Add(new(41002, "MuMuVMMHeadless.exe", executable, $"--startvm {_otherId:D}", _start));
            if (SharedRunning) result.Add(new(51001, "MuMuPlayerService.exe", SharedPathOverride ?? Path.Combine(target.Root, "shell", "MuMuPlayerService.exe"), "service", _start));
            return Task.FromResult<IReadOnlyList<MuMuProcess>>(result);
        }
        public Task<MuMuCommandResult> CommandAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Commands.Add(arguments.ToArray());
            if (arguments.SequenceEqual(new[] { "list", "runningvms" }))
                return Task.FromResult(new MuMuCommandResult(ListWorks, InvalidListOutput ? "unexpected error" : ListWorks ? (TargetRunning ? $"\"VM\" {{{target.VmId:D}}}\n" : "") + (OtherRunning ? $"\"Other\" {{{_otherId:D}}}\n" : "") : "read error"));
            if (arguments.Last() == "shutdown" && ShutdownWorks) TargetRunning = false;
            if (arguments.Last() == "acpipowerbutton" && AcpiWorks) TargetRunning = false;
            if (arguments.Last() == "poweroff" && PowerOffWorks) TargetRunning = false;
            return Task.FromResult(new MuMuCommandResult(true, ""));
        }
        public Task<bool> TerminateAsync(MuMuProcess expected, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!KillAllowed) return Task.FromResult(false);
            Terminated.Add(expected.Pid);
            if (expected.Pid == VmPid) TargetRunning = false;
            if (expected.Pid == 51001)
            {
                if (RealTaskPid > 0)
                {
                    try { using var process = Process.GetProcessById(RealTaskPid); if (!process.HasExited) SharedStoppedBeforeTaskExit = true; }
                    catch (ArgumentException) { }
                }
                SharedRunning = false;
            }
            return Task.FromResult(true);
        }
        public Task DelayAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    }
}
