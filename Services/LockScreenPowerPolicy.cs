using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace GameOrchestrator.Services;

public sealed class MachineServiceConfig
{
    public string DesktopExecutablePath { get; set; } = "";
    public bool LockScreenTimeoutEnabled { get; set; }
    public int AcSeconds { get; set; } = 60;
    public int DcSeconds { get; set; } = 30;
    public Dictionary<string, PowerTimeoutValues> OriginalTimeouts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record PowerTimeoutValues(uint AcSeconds, uint DcSeconds);

public interface ILockScreenPowerApi
{
    Guid GetActiveScheme();
    PowerTimeoutValues Read(Guid scheme);
    void Write(Guid scheme, PowerTimeoutValues values, bool activate = true);
}

public sealed class WindowsLockScreenPowerApi : ILockScreenPowerApi
{
    private static readonly Guid SubVideo = new("7516b95f-f776-4464-8c53-06167f40cc99");
    private static readonly Guid VideoConLock = new("8ec4b3a5-6868-48c2-be75-4f3044be88a7");

    public Guid GetActiveScheme()
    {
        var result = PowerGetActiveScheme(nint.Zero, out var pointer);
        ThrowIfFailed(result, "读取当前电源方案");
        try { return Marshal.PtrToStructure<Guid>(pointer); }
        finally { LocalFree(pointer); }
    }

    public PowerTimeoutValues Read(Guid scheme)
    {
        var ac = ReadValue(scheme, true);
        var dc = ReadValue(scheme, false);
        return new(ac, dc);
    }

    public void Write(Guid scheme, PowerTimeoutValues values, bool activate = true)
    {
        var subgroup = SubVideo;
        var setting = VideoConLock;
        var previous = Read(scheme);
        try
        {
            ThrowIfFailed(PowerWriteACValueIndex(nint.Zero, ref scheme, ref subgroup, ref setting, values.AcSeconds), "写入登录/锁屏接通电源息屏时间");
            ThrowIfFailed(PowerWriteDCValueIndex(nint.Zero, ref scheme, ref subgroup, ref setting, values.DcSeconds), "写入登录/锁屏电池息屏时间");
            if (activate) ThrowIfFailed(PowerSetActiveScheme(nint.Zero, ref scheme), "应用电源方案");
        }
        catch
        {
            // 防止 AC 成功而 DC 失败时留下半应用状态。
            _ = PowerWriteACValueIndex(nint.Zero, ref scheme, ref subgroup, ref setting, previous.AcSeconds);
            _ = PowerWriteDCValueIndex(nint.Zero, ref scheme, ref subgroup, ref setting, previous.DcSeconds);
            if (activate) _ = PowerSetActiveScheme(nint.Zero, ref scheme);
            throw;
        }
    }

    private static uint ReadValue(Guid scheme, bool ac)
    {
        var subgroup = SubVideo;
        var setting = VideoConLock;
        var result = ac
            ? PowerReadACValueIndex(nint.Zero, ref scheme, ref subgroup, ref setting, out var value)
            : PowerReadDCValueIndex(nint.Zero, ref scheme, ref subgroup, ref setting, out value);
        ThrowIfFailed(result, ac ? "读取登录/锁屏接通电源息屏时间" : "读取登录/锁屏电池息屏时间");
        return value;
    }

    private static void ThrowIfFailed(uint error, string operation)
    {
        if (error != 0) throw new Win32Exception((int)error, $"{operation}失败");
    }

    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(nint rootPowerKey, out nint activePolicyGuid);
    [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(nint rootPowerKey, ref Guid schemeGuid, ref Guid subgroupGuid, ref Guid settingGuid, out uint valueIndex);
    [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(nint rootPowerKey, ref Guid schemeGuid, ref Guid subgroupGuid, ref Guid settingGuid, out uint valueIndex);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteACValueIndex(nint rootPowerKey, ref Guid schemeGuid, ref Guid subgroupGuid, ref Guid settingGuid, uint valueIndex);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteDCValueIndex(nint rootPowerKey, ref Guid schemeGuid, ref Guid subgroupGuid, ref Guid settingGuid, uint valueIndex);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(nint rootPowerKey, ref Guid schemeGuid);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}

public sealed class LockScreenPowerPolicy(ILockScreenPowerApi api)
{
    public bool ApplyActiveScheme(MachineServiceConfig config)
    {
        if (!config.LockScreenTimeoutEnabled) return false;
        var scheme = api.GetActiveScheme();
        var key = scheme.ToString("D");
        var current = api.Read(scheme);
        var capturedOriginal = false;
        if (!config.OriginalTimeouts.ContainsKey(key))
        {
            config.OriginalTimeouts[key] = current;
            capturedOriginal = true;
        }
        var desired = new PowerTimeoutValues((uint)Math.Clamp(config.AcSeconds, 10, 3600), (uint)Math.Clamp(config.DcSeconds, 10, 3600));
        if (current != desired) api.Write(scheme, desired, activate: true);
        return capturedOriginal;
    }

    public IReadOnlyList<string> RestoreAll(MachineServiceConfig config)
    {
        var failures = new List<string>();
        Guid? activeScheme = null;
        try { activeScheme = api.GetActiveScheme(); } catch (Exception ex) { failures.Add($"读取当前电源方案失败：{ex.Message}"); }
        foreach (var entry in config.OriginalTimeouts.ToArray())
        {
            if (!Guid.TryParse(entry.Key, out var scheme))
            {
                failures.Add($"无效的电源方案标识：{entry.Key}");
                continue;
            }
            try { api.Write(scheme, entry.Value, activeScheme == scheme); config.OriginalTimeouts.Remove(entry.Key); }
            catch (Exception ex) { failures.Add($"恢复电源方案 {entry.Key} 失败：{ex.Message}"); }
        }
        return failures;
    }
}

public static class MachineServiceConfigStore
{
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GameDayWork");
    public static string ConfigPath => Path.Combine(DirectoryPath, "service.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static MachineServiceConfig Load() => File.Exists(ConfigPath)
        ? JsonSerializer.Deserialize<MachineServiceConfig>(File.ReadAllText(ConfigPath), Json) ?? new()
        : new();

    public static void Save(MachineServiceConfig config)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temp = ConfigPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, Json));
        File.Move(temp, ConfigPath, true);
    }
}
