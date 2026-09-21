using System.Text.Json;
using System.Text.Json.Serialization;
using GameOrchestrator.Models;

namespace GameOrchestrator.Services;

public sealed class ConfigService
{
    private const int CurrentSchemaVersion = 2;
    private readonly string _directory;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public ConfigService(string? baseDirectory = null) =>
        _directory = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "data");
    public string ConfigPath => Path.Combine(_directory, "config.json");
    public async Task<AppConfig> LoadAsync()
    {
        Directory.CreateDirectory(_directory);
        if (!File.Exists(ConfigPath)) return CreateDefault();
        AppConfig config;
        try { config = JsonSerializer.Deserialize<AppConfig>(await File.ReadAllTextAsync(ConfigPath), _json) ?? CreateDefault(); }
        catch { return CreateDefault(); }
        if (Migrate(config))
        {
            try { await SaveAsync(config); }
            catch { /* 迁移后的配置仍可用于当前会话，下次保存时会再次持久化。 */ }
        }
        return config;
    }
    public async Task SaveAsync(AppConfig config)
    {
        Directory.CreateDirectory(_directory);
        var temp = ConfigPath + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(config, _json));
        File.Move(temp, ConfigPath, true);
    }
    private static AppConfig CreateDefault()
    {
        var config = new AppConfig { ConfigSchemaVersion = CurrentSchemaVersion };
        foreach (var name in new[] { "BGI", "MAA", "ZOG", "MFA", "M7A" })
            config.Tasks.Add(new AutomationTaskConfig { Name = name });
        return config;
    }

    private static bool Migrate(AppConfig config)
    {
        var changed = false;
        if (config.Notifications is null)
        {
            config.Notifications = new NotificationConfig();
            changed = true;
        }
        if (config.ConfigSchemaVersion < 1)
        {
            // v0.3.6 及更早版本的 60 秒是固定值，并非用户选择；升级后采用新的 120 秒默认值。
            config.Notifications.RunningScreenshotDelaySeconds = 120;
            config.ConfigSchemaVersion = 1;
            changed = true;
        }
        if (config.ConfigSchemaVersion < 2)
        {
            // 新的系统服务、电源策略和登录遮罩均为显式选择，升级时保持关闭。
            config.UseSystemService = false;
            config.LockScreenDisplayTimeoutEnabled = false;
            config.LockScreenDisplayTimeoutAcSeconds = 60;
            config.LockScreenDisplayTimeoutDcSeconds = 30;
            config.ConfigSchemaVersion = 2;
            changed = true;
        }

        var acSeconds = Math.Clamp(config.LockScreenDisplayTimeoutAcSeconds, 10, 3600);
        var dcSeconds = Math.Clamp(config.LockScreenDisplayTimeoutDcSeconds, 10, 3600);
        if (acSeconds != config.LockScreenDisplayTimeoutAcSeconds || dcSeconds != config.LockScreenDisplayTimeoutDcSeconds)
        {
            config.LockScreenDisplayTimeoutAcSeconds = acSeconds;
            config.LockScreenDisplayTimeoutDcSeconds = dcSeconds;
            changed = true;
        }

        var delay = Math.Clamp(config.Notifications.RunningScreenshotDelaySeconds, 1, 3600);
        if (delay != config.Notifications.RunningScreenshotDelaySeconds)
        {
            config.Notifications.RunningScreenshotDelaySeconds = delay;
            changed = true;
        }
        return changed;
    }
}
