using GameOrchestrator.Models;

namespace GameOrchestrator.Services;

public sealed record KnownToolProfile(
    string Name, string ExecutablePattern, string Arguments,
    string LogPattern, string CompletionKeyword, string FailureKeyword, int MaxRunMinutes);

public sealed class KnownToolProfileService
{
    private static readonly KnownToolProfile[] Profiles =
    [
        new("BGI", "BGI\\BetterGI.exe", "--startOneDragon", "BGI\\log\\better-genshin-impact*.log", "一条龙和配置组任务结束", "一条龙在启动阶段被取消", 180),
        new("MAA", "MAA\\MAA-*-win-x64\\MAA.exe", "", "MAA\\MAA-*-win-x64\\debug\\gui.log", "任务已全部完成！", "", 120),
        new("ZOG", "ZOG\\OneDragon-Launcher.exe", "-o -c", "ZOG\\.log\\log.txt", "指令[ 一条龙 ] 执行成功 返回状态 全部结束", "指令[ 一条龙 ] 执行失败", 120),
        new("MFA", "MAN\\MaaAutoNaruto-*\\MFAAvalonia.exe", "", "MAN\\MaaAutoNaruto-*\\logs\\log-*.log", "任务已全部完成！", "停止前状态：FAILED", 120),
        new("M7A", "M7A\\March7thAssistant_full\\March7th Launcher.exe", "main -e", "M7A\\March7thAssistant_full\\logs\\*.log", "游戏终止：StarRail", "", 180)
    ];

    public IReadOnlyList<AutomationTaskConfig> Discover()
    {
        var results = new List<AutomationTaskConfig>();
        foreach (var root in SearchRoots())
        foreach (var profile in Profiles)
        {
            if (results.Any(task => task.Name == profile.Name)) continue;
            var executable = ResolveLatest(root, profile.ExecutablePattern);
            if (executable is null) continue;
            var logPattern = ResolvePattern(root, profile.LogPattern);
            var task = new AutomationTaskConfig
            {
                Name = profile.Name,
                ProgramPath = executable,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? "",
                Arguments = profile.Arguments,
                CompletionMode = CompletionDetectionMode.LogKeyword,
                CompletionLogPath = logPattern,
                CompletionKeyword = profile.CompletionKeyword,
                CompletionFailureKeyword = profile.FailureKeyword,
                RunAsAdministrator = profile.Name == "M7A",
                MaxRunMinutes = profile.MaxRunMinutes,
                Description = $"已识别的 {profile.Name} 自动化任务；根据本次新增日志判断完成并清理关联进程。"
            };
            task.ProcessRules.Add(new ProcessRule { ExecutableDirectory = task.WorkingDirectory, Monitor = true, Cleanup = true });
            results.Add(task);
        }
        return results;
    }

    public static void ApplyRecommendedSettings(AutomationTaskConfig target, AutomationTaskConfig profile)
    {
        target.Name = profile.Name;
        target.ProgramPath = profile.ProgramPath;
        target.Arguments = profile.Arguments;
        target.CompletionMode = profile.CompletionMode;
        target.CompletionProcessName = profile.CompletionProcessName;
        target.CompletionLogPath = profile.CompletionLogPath;
        target.CompletionKeyword = profile.CompletionKeyword;
        target.CompletionFailureKeyword = profile.CompletionFailureKeyword;
        target.MaxRunMinutes = profile.MaxRunMinutes;
        target.TrackChildren = true;
        target.UseJobObject = true;
        target.RunAsAdministrator = profile.RunAsAdministrator;
        if (string.IsNullOrWhiteSpace(target.Description) || target.Description.StartsWith("自动化日常任务", StringComparison.Ordinal))
            target.Description = profile.Description;

        var directoryRule = target.ProcessRules.FirstOrDefault(IsGeneratedDirectoryRule);
        if (directoryRule is null)
            target.ProcessRules.Add(new ProcessRule { ExecutableDirectory = target.WorkingDirectory, Monitor = true, Cleanup = true });
        else
            directoryRule.ExecutableDirectory = target.WorkingDirectory;
    }

    private static bool IsGeneratedDirectoryRule(ProcessRule rule) =>
        string.IsNullOrWhiteSpace(rule.ProcessName) &&
        string.IsNullOrWhiteSpace(rule.ExecutablePath) &&
        !string.IsNullOrWhiteSpace(rule.ExecutableDirectory) &&
        rule.Monitor && rule.Cleanup && !rule.AllowNameFallback;

    private static IEnumerable<string> SearchRoots()
    {
        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.IsReady && drive.DriveType == DriveType.Fixed))
        foreach (var name in new[] { "Tool", "Tools" })
        {
            var path = Path.Combine(drive.RootDirectory.FullName, name);
            if (Directory.Exists(path)) yield return path;
        }
    }

    private static string? ResolveLatest(string root, string relativePattern)
    {
        var directoryPattern = Path.GetDirectoryName(relativePattern) ?? "";
        var filePattern = Path.GetFileName(relativePattern);
        var directories = ExpandDirectories(root, directoryPattern);
        return directories.SelectMany(directory => Directory.EnumerateFiles(directory, filePattern, SearchOption.TopDirectoryOnly))
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Select(file => file.FullName)
            .FirstOrDefault();
    }

    private static string ResolvePattern(string root, string relativePattern)
    {
        var directoryPattern = Path.GetDirectoryName(relativePattern) ?? "";
        var filePattern = Path.GetFileName(relativePattern);
        var directory = ExpandDirectories(root, directoryPattern).FirstOrDefault();
        return directory is null ? Path.Combine(root, relativePattern) : Path.Combine(directory, filePattern);
    }

    private static IEnumerable<string> ExpandDirectories(string root, string relativePattern)
    {
        IEnumerable<string> current = [root];
        foreach (var segment in relativePattern.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (string.IsNullOrWhiteSpace(segment)) continue;
            current = current.SelectMany(parent => Directory.Exists(parent)
                ? Directory.EnumerateDirectories(parent, segment, SearchOption.TopDirectoryOnly)
                : []);
        }
        return current;
    }
}
