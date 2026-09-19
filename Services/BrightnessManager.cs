using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using GameOrchestrator.Models;

namespace GameOrchestrator.Services;

public sealed class BrightnessManager
{
    private const uint MonitorCapsBrightness = 0x00000002;
    private readonly LoggingService _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _recoveryPath;
    private BrightnessRecoveryDocument? _activeRecovery;

    public BrightnessManager(LoggingService log)
    {
        _log = log;
        _recoveryPath = Path.Combine(AppContext.BaseDirectory, "data", "brightness-recovery.json");
    }

    public async Task RecoverPendingAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var recovery = await LoadRecoveryAsync(token);
            if (recovery is null || recovery.Entries.Count == 0) return;

            await _log.WriteAsync(LogLevel.Warning, $"检测到未完成的亮度恢复记录（{recovery.Entries.Count} 项），正在恢复");
            await RestoreCoreAsync(recovery, token);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DimForBlackoutAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var recovery = _activeRecovery ?? await LoadRecoveryAsync(token) ?? new BrightnessRecoveryDocument();
            IReadOnlyList<BrightnessTarget> targets;
            try
            {
                targets = await Task.Run(CaptureTargets, token);
            }
            catch (Exception ex)
            {
                await _log.WriteAsync(LogLevel.Warning, $"读取显示器亮度失败，继续使用黑色遮罩：{ex.Message}");
                return;
            }

            foreach (var target in targets)
            {
                if (recovery.Entries.Any(entry => IsSameTarget(entry.Provider, entry.Key, target.Provider, target.Key))) continue;
                recovery.Entries.Add(new BrightnessRecoveryEntry
                {
                    Provider = target.Provider,
                    Key = target.Key,
                    DisplayName = target.DisplayName,
                    OriginalBrightness = target.CurrentBrightness,
                    MinimumBrightness = target.MinimumBrightness,
                    MaximumBrightness = target.MaximumBrightness
                });
            }

            if (recovery.Entries.Count == 0)
            {
                await _log.WriteAsync(LogLevel.Warning, "未发现可控制亮度的显示器，将仅使用黑色遮罩");
                return;
            }

            recovery.CapturedAt = DateTimeOffset.Now;
            try
            {
                await SaveRecoveryAsync(recovery, token);
            }
            catch (Exception ex)
            {
                await _log.WriteAsync(LogLevel.Error, $"亮度恢复记录写入失败，已取消调暗：{ex.Message}");
                return;
            }

            _activeRecovery = recovery;
            var results = await Task.Run(() => ApplyBrightness(recovery.Entries, restore: false), token);
            foreach (var result in results)
            {
                if (result.Success)
                    await _log.WriteAsync(LogLevel.Info, $"显示器亮度已降至最低：{result.DisplayName}，{result.From} → {result.To}");
                else
                    await _log.WriteAsync(LogLevel.Warning, $"显示器亮度调低失败：{result.DisplayName}；{result.Error}");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await _log.WriteAsync(LogLevel.Warning, $"调低显示器亮度失败，继续使用黑色遮罩：{ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestoreAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var recovery = _activeRecovery ?? await LoadRecoveryAsync(token);
            if (recovery is null || recovery.Entries.Count == 0) return;
            await RestoreCoreAsync(recovery, token);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RestoreCoreAsync(BrightnessRecoveryDocument recovery, CancellationToken token)
    {
        IReadOnlyList<BrightnessOperationResult> results;
        try
        {
            results = await Task.Run(() => ApplyBrightness(recovery.Entries, restore: true), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _activeRecovery = recovery;
            await _log.WriteAsync(LogLevel.Error, $"恢复显示器亮度失败：{ex.Message}");
            return;
        }

        var failedKeys = results.Where(result => !result.Success)
            .Select(result => ComposeKey(result.Provider, result.Key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var result in results)
        {
            if (result.Success)
                await _log.WriteAsync(LogLevel.Info, $"显示器亮度已恢复：{result.DisplayName}，{result.From} → {result.To}");
            else
                await _log.WriteAsync(LogLevel.Warning, $"显示器亮度恢复失败：{result.DisplayName}；{result.Error}");
        }

        recovery.Entries = recovery.Entries
            .Where(entry => failedKeys.Contains(ComposeKey(entry.Provider, entry.Key)))
            .ToList();

        if (recovery.Entries.Count == 0)
        {
            DeleteRecoveryFile();
            _activeRecovery = null;
            return;
        }

        _activeRecovery = recovery;
        try { await SaveRecoveryAsync(recovery, token); }
        catch (Exception ex) { await _log.WriteAsync(LogLevel.Error, $"更新亮度恢复记录失败：{ex.Message}"); }
    }

    private async Task<BrightnessRecoveryDocument?> LoadRecoveryAsync(CancellationToken token)
    {
        if (!File.Exists(_recoveryPath)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(_recoveryPath, token);
            return JsonSerializer.Deserialize<BrightnessRecoveryDocument>(json, _json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _log.WriteAsync(LogLevel.Error, $"读取亮度恢复记录失败：{ex.Message}");
            return null;
        }
    }

    private async Task SaveRecoveryAsync(BrightnessRecoveryDocument recovery, CancellationToken token)
    {
        var directory = Path.GetDirectoryName(_recoveryPath)!;
        Directory.CreateDirectory(directory);
        var temp = _recoveryPath + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(recovery, _json), token);
        File.Move(temp, _recoveryPath, true);
    }

    private void DeleteRecoveryFile()
    {
        try
        {
            if (File.Exists(_recoveryPath)) File.Delete(_recoveryPath);
            var temp = _recoveryPath + ".tmp";
            if (File.Exists(temp)) File.Delete(temp);
        }
        catch (Exception ex)
        {
            _ = _log.WriteAsync(LogLevel.Warning, $"删除亮度恢复记录失败：{ex.Message}");
        }
    }

    private static IReadOnlyList<BrightnessTarget> CaptureTargets()
    {
        var targets = new List<BrightnessTarget>();
        CaptureWmiTargets(targets);
        CaptureDdcTargets(targets);
        return targets;
    }

    private static void CaptureWmiTargets(List<BrightnessTarget> targets)
    {
        try
        {
            var methodKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var methods = new ManagementObjectSearcher("root\\wmi", "SELECT Active, InstanceName FROM WmiMonitorBrightnessMethods"))
            using (var rows = methods.Get())
            {
                foreach (ManagementObject row in rows)
                {
                    using (row)
                    {
                        if (Convert.ToBoolean(row["Active"]) && row["InstanceName"] is string key) methodKeys.Add(key);
                    }
                }
            }

            using var brightness = new ManagementObjectSearcher("root\\wmi", "SELECT Active, InstanceName, CurrentBrightness, Level FROM WmiMonitorBrightness");
            using var brightnessRows = brightness.Get();
            foreach (ManagementObject row in brightnessRows)
            {
                using (row)
                {
                    if (!Convert.ToBoolean(row["Active"]) || row["InstanceName"] is not string key || !methodKeys.Contains(key)) continue;
                    var levels = ((Array?)row["Level"])?.Cast<object>().Select(Convert.ToInt32).ToArray() ?? [];
                    var current = Convert.ToInt32(row["CurrentBrightness"]);
                    targets.Add(new BrightnessTarget("WMI", key, GetWmiDisplayName(key), current,
                        levels.Length > 0 ? levels.Min() : 0,
                        levels.Length > 0 ? levels.Max() : 100));
                }
            }
        }
        catch (ManagementException)
        {
            // Some external monitors and desktop systems do not expose the WMI brightness classes.
        }
    }

    private static void CaptureDdcTargets(List<BrightnessTarget> targets)
    {
        EnumerateDdcMonitors(monitor =>
        {
            if (!TryReadDdcBrightness(monitor.Handle, out var minimum, out var current, out var maximum)) return;
            targets.Add(new BrightnessTarget("DDC", monitor.Key, monitor.DisplayName, (int)current, (int)minimum, (int)maximum));
        });
    }

    private static IReadOnlyList<BrightnessOperationResult> ApplyBrightness(IReadOnlyList<BrightnessRecoveryEntry> entries, bool restore)
    {
        var results = entries.ToDictionary(
            entry => ComposeKey(entry.Provider, entry.Key),
            entry => new BrightnessOperationResult(entry.Provider, entry.Key, entry.DisplayName, false,
                restore ? entry.MinimumBrightness : entry.OriginalBrightness,
                restore ? entry.OriginalBrightness : entry.MinimumBrightness,
                "未找到对应的活动显示器"),
            StringComparer.OrdinalIgnoreCase);

        ApplyWmiBrightness(entries.Where(entry => entry.Provider.Equals("WMI", StringComparison.OrdinalIgnoreCase)).ToList(), restore, results);
        ApplyDdcBrightness(entries.Where(entry => entry.Provider.Equals("DDC", StringComparison.OrdinalIgnoreCase)).ToList(), restore, results);
        return results.Values.ToList();
    }

    private static void ApplyWmiBrightness(
        IReadOnlyList<BrightnessRecoveryEntry> entries,
        bool restore,
        IDictionary<string, BrightnessOperationResult> results)
    {
        if (entries.Count == 0) return;
        try
        {
            var byKey = entries.ToDictionary(entry => entry.Key, StringComparer.OrdinalIgnoreCase);
            using var methods = new ManagementObjectSearcher("root\\wmi", "SELECT Active, InstanceName FROM WmiMonitorBrightnessMethods");
            using var rows = methods.Get();
            foreach (ManagementObject row in rows)
            {
                using (row)
                {
                    if (!Convert.ToBoolean(row["Active"]) || row["InstanceName"] is not string key || !byKey.TryGetValue(key, out var entry)) continue;
                    var target = restore ? entry.OriginalBrightness : entry.MinimumBrightness;
                    try
                    {
                        using var input = row.GetMethodParameters("WmiSetBrightness");
                        input["Timeout"] = 0u;
                        input["Brightness"] = (byte)Math.Clamp(target, 0, 100);
                        using var output = row.InvokeMethod("WmiSetBrightness", input, null);
                        var returnValue = output?["ReturnValue"] is null ? 0u : Convert.ToUInt32(output["ReturnValue"]);
                        results[ComposeKey("WMI", key)] = returnValue == 0
                            ? new BrightnessOperationResult("WMI", key, entry.DisplayName, true,
                                restore ? entry.MinimumBrightness : entry.OriginalBrightness, target, "")
                            : new BrightnessOperationResult("WMI", key, entry.DisplayName, false,
                                restore ? entry.MinimumBrightness : entry.OriginalBrightness, target, $"WMI 返回 {returnValue}");
                    }
                    catch (Exception ex)
                    {
                        results[ComposeKey("WMI", key)] = new BrightnessOperationResult("WMI", key, entry.DisplayName, false,
                            restore ? entry.MinimumBrightness : entry.OriginalBrightness, target, ex.Message);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            foreach (var entry in entries)
                results[ComposeKey("WMI", entry.Key)] = new BrightnessOperationResult("WMI", entry.Key, entry.DisplayName, false,
                    restore ? entry.MinimumBrightness : entry.OriginalBrightness,
                    restore ? entry.OriginalBrightness : entry.MinimumBrightness, ex.Message);
        }
    }

    private static void ApplyDdcBrightness(
        IReadOnlyList<BrightnessRecoveryEntry> entries,
        bool restore,
        IDictionary<string, BrightnessOperationResult> results)
    {
        if (entries.Count == 0) return;
        var byKey = entries.ToDictionary(entry => entry.Key, StringComparer.OrdinalIgnoreCase);
        EnumerateDdcMonitors(monitor =>
        {
            if (!byKey.TryGetValue(monitor.Key, out var entry)) return;
            var target = (uint)Math.Clamp(restore ? entry.OriginalBrightness : entry.MinimumBrightness,
                entry.MinimumBrightness, entry.MaximumBrightness);
            try
            {
                if (!SetMonitorBrightness(monitor.Handle, target))
                {
                    results[ComposeKey("DDC", entry.Key)] = new BrightnessOperationResult("DDC", entry.Key, entry.DisplayName, false,
                        restore ? entry.MinimumBrightness : entry.OriginalBrightness, (int)target,
                        $"SetMonitorBrightness 失败（{Marshal.GetLastWin32Error()}）");
                    return;
                }

                results[ComposeKey("DDC", entry.Key)] = new BrightnessOperationResult("DDC", entry.Key, entry.DisplayName, true,
                    restore ? entry.MinimumBrightness : entry.OriginalBrightness, (int)target, "");
            }
            catch (Exception ex)
            {
                results[ComposeKey("DDC", entry.Key)] = new BrightnessOperationResult("DDC", entry.Key, entry.DisplayName, false,
                    restore ? entry.MinimumBrightness : entry.OriginalBrightness, (int)target, ex.Message);
            }
        });
    }

    private static bool TryReadDdcBrightness(nint handle, out uint minimum, out uint current, out uint maximum)
    {
        minimum = current = maximum = 0;
        return GetMonitorCapabilities(handle, out var capabilities, out _)
            && (capabilities & MonitorCapsBrightness) != 0
            && GetMonitorBrightness(handle, out minimum, out current, out maximum);
    }

    private static void EnumerateDdcMonitors(Action<DdcMonitor> action)
    {
        try
        {
            EnumDisplayMonitors(nint.Zero, nint.Zero, (monitor, _, _, _) =>
            {
                try
                {
                    var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
                    if (!GetMonitorInfo(monitor, ref info)) return true;
                    if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out var count) || count == 0) return true;
                    var physical = new PhysicalMonitor[count];
                    if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, physical)) return true;
                    try
                    {
                        for (var index = 0; index < physical.Length; index++)
                        {
                            var description = string.IsNullOrWhiteSpace(physical[index].Description)
                                ? $"{info.DeviceName} 显示器 {index + 1}"
                                : physical[index].Description;
                            var key = $"{info.DeviceName}|{index}|{description}";
                            action(new DdcMonitor(physical[index].Handle, key, description));
                        }
                    }
                    finally
                    {
                        DestroyPhysicalMonitors(count, physical);
                    }
                }
                catch
                {
                    // Continue enumerating other monitors when one driver rejects DDC/CI.
                }
                return true;
            }, nint.Zero);
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    private static string GetWmiDisplayName(string key)
    {
        var parts = key.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 ? parts[1] : key;
    }

    private static string ComposeKey(string provider, string key) => $"{provider}:{key}";
    private static bool IsSameTarget(string leftProvider, string leftKey, string rightProvider, string rightKey) =>
        leftProvider.Equals(rightProvider, StringComparison.OrdinalIgnoreCase)
        && leftKey.Equals(rightKey, StringComparison.OrdinalIgnoreCase);

    private sealed record BrightnessTarget(
        string Provider,
        string Key,
        string DisplayName,
        int CurrentBrightness,
        int MinimumBrightness,
        int MaximumBrightness);

    private sealed record BrightnessOperationResult(
        string Provider,
        string Key,
        string DisplayName,
        bool Success,
        int From,
        int To,
        string Error);

    private sealed record DdcMonitor(nint Handle, string Key, string DisplayName);

    private delegate bool MonitorEnumProc(nint monitor, nint hdc, nint rect, nint data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PhysicalMonitor
    {
        public nint Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(nint monitor, out uint count);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(nint monitor, uint count, [Out] PhysicalMonitor[] physicalMonitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyPhysicalMonitors(uint count, [In] PhysicalMonitor[] physicalMonitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorCapabilities(nint monitor, out uint capabilities, out uint colorTemperatures);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorBrightness(nint monitor, out uint minimum, out uint current, out uint maximum);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMonitorBrightness(nint monitor, uint newBrightness);
}

internal sealed class BrightnessRecoveryDocument
{
    public DateTimeOffset CapturedAt { get; set; }
    public List<BrightnessRecoveryEntry> Entries { get; set; } = [];
}

internal sealed class BrightnessRecoveryEntry
{
    public string Provider { get; set; } = "";
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int OriginalBrightness { get; set; }
    public int MinimumBrightness { get; set; }
    public int MaximumBrightness { get; set; }
}
