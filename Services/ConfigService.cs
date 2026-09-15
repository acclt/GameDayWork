using System.Text.Json;
using System.Text.Json.Serialization;
using GameOrchestrator.Models;

namespace GameOrchestrator.Services;

public sealed class ConfigService
{
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "data");
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public string ConfigPath => Path.Combine(_directory, "config.json");
    public async Task<AppConfig> LoadAsync()
    {
        Directory.CreateDirectory(_directory);
        if (!File.Exists(ConfigPath)) return CreateDefault();
        try { return JsonSerializer.Deserialize<AppConfig>(await File.ReadAllTextAsync(ConfigPath), _json) ?? CreateDefault(); }
        catch { return CreateDefault(); }
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
        var config = new AppConfig();
        foreach (var name in new[] { "BGI", "MAA", "ZOG", "MFA", "M7A" })
            config.Tasks.Add(new AutomationTaskConfig { Name = name });
        return config;
    }
}
