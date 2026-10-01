using System.Diagnostics;
using System.Management;

namespace GameOrchestrator.Services;

public sealed record MuMuProcess(int Pid, string Name, string Path, string CommandLine, DateTimeOffset? StartedAt);
public sealed record MuMuCommandResult(bool Success, string Output);

public interface IMuMuPlatform
{
    Task<IReadOnlyList<MuMuProcess>> ProcessesAsync(CancellationToken token);
    Task<MuMuCommandResult> CommandAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token);
    Task<bool> TerminateAsync(MuMuProcess expected, CancellationToken token);
    Task DelayAsync(CancellationToken token);
}

public sealed class MuMuPlatform : IMuMuPlatform
{
    public Task<IReadOnlyList<MuMuProcess>> ProcessesAsync(CancellationToken token) => Task.Run<IReadOnlyList<MuMuProcess>>(() =>
    {
        var processes = new List<MuMuProcess>();
        using var searcher = new ManagementObjectSearcher("SELECT ProcessId,Name,ExecutablePath,CommandLine,CreationDate FROM Win32_Process WHERE Name LIKE 'MuMu%'");
        using var results = searcher.Get();
        foreach (ManagementObject item in results)
        {
            using (item)
            {
                token.ThrowIfCancellationRequested();
                DateTimeOffset? started = item["CreationDate"] is string date ? ManagementDateTimeConverter.ToDateTime(date) : null;
                processes.Add(new(Convert.ToInt32(item["ProcessId"]), item["Name"]?.ToString() ?? "",
                    item["ExecutablePath"]?.ToString() ?? "", item["CommandLine"]?.ToString() ?? "", started));
            }
        }
        return processes;
    }, token);

    public async Task<MuMuCommandResult> CommandAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
    {
        if (!File.Exists(executable)) return new(false, "控制工具不存在");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("MuMu 控制工具启动失败");
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            // Only terminate the controller process started by this call.
            if (!process.HasExited) process.Kill();
            try
            {
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
                await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch { /* The timed-out controller is disposable; its output is not a VM result. */ }
            token.ThrowIfCancellationRequested();
            return new(false, "控制工具超时");
        }
        return new(process.ExitCode == 0, (await output) + (await error));
    }

    public async Task<bool> TerminateAsync(MuMuProcess expected, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(expected.Path) || expected.StartedAt is null) return false;
        var current = (await ProcessesAsync(token)).FirstOrDefault(item => item.Pid == expected.Pid);
        if (current is null) return true;
        if (!string.Equals(current.Path, expected.Path, StringComparison.OrdinalIgnoreCase)
            || current.StartedAt != expected.StartedAt || current.CommandLine != expected.CommandLine) return false;
        try
        {
            using var process = Process.GetProcessById(expected.Pid);
            if (process.HasExited) return true;
            if (!string.Equals(process.MainModule?.FileName, expected.Path, StringComparison.OrdinalIgnoreCase)
                || Math.Abs((new DateTimeOffset(process.StartTime) - expected.StartedAt.Value).TotalSeconds) > 1) return false;
            process.Kill();
            await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(5), token);
            return process.HasExited;
        }
        catch (ArgumentException) { return true; }
    }

    public Task DelayAsync(CancellationToken token) => Task.Delay(500, token);
}
