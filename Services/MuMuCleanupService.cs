using System.Text.RegularExpressions;
using GameOrchestrator.Models;

namespace GameOrchestrator.Services;

public sealed class MuMuCleanupService(LoggingService log, IMuMuPlatform? platform = null, IMuMuTargetResolver? resolver = null)
{
    private readonly IMuMuPlatform _platform = platform ?? new MuMuPlatform();
    private readonly IMuMuTargetResolver _resolver = resolver ?? new MuMuTargetResolver();

    public async Task<MuMuTarget?> CaptureAsync(AutomationTaskConfig task, CancellationToken token)
    {
        if (!MuMuTargetResolver.Supports(task)) return null;
        var target = _resolver.Resolve(task, await _platform.ProcessesAsync(token));
        if (target is not null) await log.WriteAsync(LogLevel.Info, $"已绑定 MuMu 实例 {target.Index}，虚拟机 {target.VmId:D}");
        return target;
    }

    private sealed record Probe(bool Running, bool Certain, IReadOnlyList<MuMuProcess> Processes);
    private async Task<Probe> ProbeAsync(MuMuTarget target, CancellationToken token)
    {
        var processes = await _platform.ProcessesAsync(token);
        var headless = processes.Where(item => MuMuTargetResolver.IsHeadless(item.Name)).ToList();
        var matching = headless.Where(item => MuMuTargetResolver.VmId(item.CommandLine) == target.VmId).ToList();
        var expectedPath = Path.Combine(Path.GetDirectoryName(target.Hypervisor)!, "MuMuVMMHeadless.exe");
        var readable = headless.All(item => MuMuTargetResolver.VmId(item.CommandLine) is not null)
            && matching.All(item => string.Equals(item.Path, expectedPath, StringComparison.OrdinalIgnoreCase));
        var state = await _platform.CommandAsync(target.Hypervisor, ["list", "runningvms"], token);
        var lines = state.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var validState = state.Success && lines.All(line => Regex.IsMatch(line, @"^\s*""[^""]*""\s+\{[0-9a-fA-F-]{36}\}\s*$"));
        var listed = Regex.Matches(state.Output, @"\{([0-9a-fA-F-]{36})\}")
            .Any(match => Guid.TryParse(match.Groups[1].Value, out var id) && id == target.VmId);
        // A CLI success alone is insufficient: orphaned headless processes still count.
        return new(matching.Count > 0 || (validState && listed), readable && validState, matching);
    }

    private async Task<bool> WaitForExitAsync(MuMuTarget target, CancellationToken token)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var state = await ProbeAsync(target, token);
            if (state.Certain && !state.Running) return true;
            await _platform.DelayAsync(token);
        }
        return false;
    }

    private async Task<ProcessCleanupResult> FinalResultAsync(MuMuTarget target, IReadOnlyList<ProcessTerminationRecord> records, CancellationToken token)
    {
        var state = await ProbeAsync(target, token);
        var success = state.Certain && !state.Running;
        return new(success, records, success ? 0 : Math.Max(1, state.Processes.Count));
    }

    public async Task<ProcessCleanupResult> CloseInstanceAsync(MuMuTarget target, CancellationToken token)
    {
        var records = new List<ProcessTerminationRecord>();
        try
        {
            var before = await ProbeAsync(target, token);
            if (before.Certain && !before.Running) return new(true, [], 0);
            await log.WriteAsync(LogLevel.Info, $"请求正常关闭 MuMu 实例 {target.Index}");
            await _platform.CommandAsync(Path.Combine(target.Root, "shell", "MuMuManager.exe"), ["control", "-v", target.Index, "shutdown"], token);
            var method = "MuMuShutdown";
            if (!await WaitForExitAsync(target, token))
            {
                method = "MuMuACPI";
                await _platform.CommandAsync(target.Hypervisor, ["controlvm", target.VmId.ToString("D"), "acpipowerbutton"], token);
                if (!await WaitForExitAsync(target, token))
                {
                    method = "MuMuPowerOff";
                    await log.WriteAsync(LogLevel.Warning, $"MuMu 实例 {target.Index} 正常退出失败，关闭已绑定虚拟机");
                    await _platform.CommandAsync(target.Hypervisor, ["controlvm", target.VmId.ToString("D"), "poweroff"], token);
                    if (!await WaitForExitAsync(target, token))
                    {
                        var remaining = await ProbeAsync(target, token);
                        if (remaining.Certain)
                        {
                            method = "MuMuExactPid";
                            foreach (var process in remaining.Processes) await _platform.TerminateAsync(process, token);
                        }
                    }
                }
            }
            var final = await ProbeAsync(target, token);
            var success = final.Certain && !final.Running;
            foreach (var process in before.Processes)
                records.Add(new(process.Pid, process.Name, TrackedProcessSource.RuleMatched, method,
                    success, success ? "" : "MuMu 虚拟机退出无法验证"));
            await log.WriteAsync(success ? LogLevel.Success : LogLevel.Error,
                success ? $"MuMu 实例 {target.Index} 虚拟机已退出" : $"MuMu 实例 {target.Index} 虚拟机仍运行或状态无法确认");
            return new(success, records, success ? 0 : Math.Max(1, final.Processes.Count));
        }
        catch (Exception ex)
        {
            await log.WriteAsync(LogLevel.Error, $"MuMu 清理失败：{ex.Message}");
            return new(false, records, 1);
        }
    }

    public async Task<ProcessCleanupResult> FinishCleanupAsync(MuMuTarget target, CancellationToken token)
    {
        var records = new List<ProcessTerminationRecord>();
        try
        {
            var targetState = await ProbeAsync(target, token);
            if (!targetState.Certain || targetState.Running) return new(false, [], Math.Max(1, targetState.Processes.Count));
            var processes = await _platform.ProcessesAsync(token);
            var running = await _platform.CommandAsync(target.Hypervisor, ["list", "runningvms"], token);
            // The helper is shared, including VMs that existed before this task.
            if (processes.Any(item => MuMuTargetResolver.IsHeadless(item.Name))
                || !running.Success || !string.IsNullOrWhiteSpace(running.Output))
            {
                await log.WriteAsync(LogLevel.Info, "其他 MuMu 实例仍运行或状态无法确认，保留共用辅助服务");
                return await FinalResultAsync(target, records, token);
            }
            var servicePath = Path.GetFullPath(Path.Combine(target.Root, "shell", "MuMuPlayerService.exe"));
            foreach (var service in processes.Where(item => item.Name.Equals("MuMuPlayerService.exe", StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Path, servicePath, StringComparison.OrdinalIgnoreCase)))
            {
                // Recheck immediately before terminating a shared service.
                var live = await _platform.ProcessesAsync(token);
                var state = await _platform.CommandAsync(target.Hypervisor, ["list", "runningvms"], token);
                if (live.Any(item => MuMuTargetResolver.IsHeadless(item.Name)) || !state.Success || !string.IsNullOrWhiteSpace(state.Output))
                    return await FinalResultAsync(target, records, token);
                var killed = await _platform.TerminateAsync(service, token);
                var gone = !(await _platform.ProcessesAsync(token)).Any(item => string.Equals(item.Path, servicePath, StringComparison.OrdinalIgnoreCase));
                records.Add(new(service.Pid, service.Name, TrackedProcessSource.RuleMatched, "MuMuSharedService", killed && gone,
                    killed && gone ? "" : "辅助服务未退出或身份无法确认"));
                if (!killed || !gone) return new(false, records, 1);
                await log.WriteAsync(LogLevel.Success, $"MuMu 辅助服务已退出，PID {service.Pid}");
            }
            return await FinalResultAsync(target, records, token);
        }
        catch (Exception ex)
        {
            await log.WriteAsync(LogLevel.Error, $"MuMu 最终验证失败：{ex.Message}");
            return new(false, records, 1);
        }
    }
}
