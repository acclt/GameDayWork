using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using GameOrchestrator.Models;

namespace GameOrchestrator.Services;

public sealed record MuMuTarget(string Root, string Index, Guid VmId, string Hypervisor);

public interface IMuMuTargetResolver
{
    MuMuTarget? Resolve(AutomationTaskConfig task, IReadOnlyList<MuMuProcess> processes);
}

public sealed class MuMuTargetResolver : IMuMuTargetResolver
{
    public static bool Supports(AutomationTaskConfig task) => task.ToolType is "MAA" or "MMA" or "MFA";

    public MuMuTarget? Resolve(AutomationTaskConfig task, IReadOnlyList<MuMuProcess> processes)
    {
        if (!Supports(task)) return null;
        var directory = Path.GetDirectoryName(Path.GetFullPath(task.ProgramPath))!;
        var candidates = new List<string>();
        string address = ""; int? index = null;
        if (task.ToolType is "MAA" or "MMA")
        {
            var file = Path.Combine(directory, "config", "gui.new.json");
            if (!File.Exists(file)) file = Path.Combine(directory, "config", "gui.json");
            if (!File.Exists(file)) throw new InvalidOperationException("找不到 MAA 当前连接配置，无法确认 MuMu 实例。");
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var root = document.RootElement;
            if (root.TryGetProperty("Configurations", out var profiles))
            {
                var current = String(root, "Current");
                if (string.IsNullOrWhiteSpace(current) || !profiles.TryGetProperty(current, out root))
                    throw new InvalidOperationException("无法确认 MAA 当前配置。");
            }
            var connect = FindObject(root, "ConnectSettings");
            if (connect is null) throw new InvalidOperationException("MAA 连接配置格式无法识别。");
            if (!String(connect.Value, "Config").Contains("MuMu", StringComparison.OrdinalIgnoreCase)) return null;
            address = String(connect.Value, "Address");
            CollectPaths(root, candidates);
        }
        else
        {
            var instanceDirectory = Path.Combine(directory, "config", "instances");
            if (!Directory.Exists(instanceDirectory)) throw new InvalidOperationException("找不到 MFA 实例配置。");
            string active = "";
            var settings = Path.Combine(directory, "appsettings.json");
            if (File.Exists(settings))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(settings));
                active = String(document.RootElement, "Instances.LastActiveName");
            }
            var instances = Directory.EnumerateFiles(instanceDirectory, "*.json").ToList();
            var matching = new List<string>();
            foreach (var file in instances)
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                if (String(document.RootElement, "InstanceName") == active || (active == "" && instances.Count == 1)) matching.Add(file);
            }
            if (matching.Count != 1) throw new InvalidOperationException("无法唯一确认 MFA 当前实例；未对 MuMu 执行清理。");
            using var instance = JsonDocument.Parse(File.ReadAllText(matching[0]));
            if (!instance.RootElement.TryGetProperty("AdbDevice", out var adb)) return null;
            address = String(adb, "AdbSerial");
            var extra = String(adb, "Config");
            if (extra != "")
            {
                using var document = JsonDocument.Parse(extra);
                var mumu = FindObject(document.RootElement, "mumu");
                if (mumu is { } value && value.TryGetProperty("enable", out var enabled) && enabled.ValueKind == JsonValueKind.True)
                {
                    candidates.Add(String(value, "path"));
                    if (value.TryGetProperty("index", out var number) && number.TryGetInt32(out var id)) index = id;
                }
            }
            if (index is null && !String(adb, "Name").Contains("MuMu", StringComparison.OrdinalIgnoreCase)) return null;
            CollectPaths(instance.RootElement, candidates);
        }

        var explicitRoots = candidates.Select(RootFromPath).Where(root => root is not null).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var roots = explicitRoots.Count > 0 ? explicitRoots : InstalledRoots(processes).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var targets = new List<MuMuTarget>();
        var portMatch = Regex.Match(address, @"^(?:127\.0\.0\.1|localhost):(?<port>\d+)$", RegexOptions.IgnoreCase);
        var port = portMatch.Success && int.TryParse(portMatch.Groups["port"].Value, out var parsed) ? parsed : (int?)null;
        foreach (var root in roots)
        foreach (var vmDirectory in Directory.EnumerateDirectories(Path.Combine(root, "vms")))
        {
            var name = Path.GetFileName(vmDirectory);
            var nameMatch = Regex.Match(name, @"^MuMuPlayer-12\.0-(\d+)$");
            if (!nameMatch.Success || (index is not null && nameMatch.Groups[1].Value != index.Value.ToString())) continue;
            var file = Path.Combine(vmDirectory, name + ".nemu");
            if (!File.Exists(file)) continue;
            using var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            var xml = XDocument.Load(reader);
            var machine = xml.Descendants().FirstOrDefault(element => element.Name.LocalName == "Machine");
            if (!Guid.TryParse(machine?.Attribute("uuid")?.Value, out var uuid)) continue;
            if (index is null && (port is null || !xml.Descendants().Any(element => element.Name.LocalName == "Forwarding" && element.Attribute("hostport")?.Value == port.Value.ToString()))) continue;
            var hypervisor = processes.Where(process => IsHeadless(process.Name) && VmId(process.CommandLine) == uuid && process.Path != "")
                .Select(process => Path.Combine(Path.GetDirectoryName(process.Path)!, "MuMuVMMManage.exe")).FirstOrDefault(File.Exists)
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MuMuVMMVbox", "Hypervisor", "MuMuVMMManage.exe");
            if (!File.Exists(hypervisor)) throw new InvalidOperationException("找不到 MuMu 虚拟机管理工具。");
            targets.Add(new(root, nameMatch.Groups[1].Value, uuid, hypervisor));
        }
        return targets.Count == 1 ? targets[0] : throw new InvalidOperationException("无法唯一匹配任务使用的 MuMu 虚拟机；未对其他实例执行清理。");
    }

    public static bool IsHeadless(string name) => name.Equals("MuMuVMMHeadless.exe", StringComparison.OrdinalIgnoreCase);
    public static Guid? VmId(string commandLine)
    {
        var match = Regex.Match(commandLine, @"(?:^|\s)--startvm\s+""?\{?([0-9a-fA-F-]{36})");
        return match.Success && Guid.TryParse(match.Groups[1].Value, out var id) ? id : null;
    }
    private static string String(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static JsonElement? FindObject(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty(name, out var found) && found.ValueKind == JsonValueKind.Object) return found;
        foreach (var property in root.EnumerateObject()) if (FindObject(property.Value, name) is { } child) return child;
        return null;
    }
    private static void CollectPaths(JsonElement root, List<string> paths)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name is "EmulatorPath" or "AdbPath" or "SoftwarePath" && property.Value.ValueKind == JsonValueKind.String)
                paths.Add(property.Value.GetString() ?? "");
            CollectPaths(property.Value, paths);
        }
    }
    private static string? RootFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return null;
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
        {
            object? shell = null; object? link = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
                link = shell!.GetType().InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, [path]);
                path = link!.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, link, null)?.ToString() ?? "";
            }
            finally { if (link is not null) Marshal.FinalReleaseComObject(link); if (shell is not null) Marshal.FinalReleaseComObject(shell); }
        }
        var current = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        for (var depth = 0; depth < 4 && !string.IsNullOrWhiteSpace(current); depth++, current = Path.GetDirectoryName(current))
            if (File.Exists(Path.Combine(current, "shell", "MuMuManager.exe")) && Directory.Exists(Path.Combine(current, "vms"))) return Path.GetFullPath(current);
        return null;
    }
    private static IEnumerable<string> InstalledRoots(IReadOnlyList<MuMuProcess> processes)
    {
        foreach (var process in processes) if (RootFromPath(process.Path) is { } root) yield return root;
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        foreach (var keyName in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            using var key = hive.OpenSubKey(keyName);
            if (key is null) continue;
            foreach (var child in key.GetSubKeyNames())
            {
                using var entry = key.OpenSubKey(child);
                if (entry?.GetValue("DisplayName") is string display && display.Contains("MuMu", StringComparison.OrdinalIgnoreCase)
                    && RootFromPath(entry.GetValue("InstallLocation")?.ToString() ?? "") is { } root) yield return root;
            }
        }
    }
}
