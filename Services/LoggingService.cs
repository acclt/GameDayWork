using GameOrchestrator.Models;

namespace GameOrchestrator.Services;

public sealed class LoggingService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "logs");
    public bool FileLoggingEnabled { get; set; } = true;
    public event Action<LogEntry>? EntryWritten;
    public async Task WriteAsync(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, message);
        EntryWritten?.Invoke(entry);
        if (!FileLoggingEnabled) return;
        try
        {
            await _gate.WaitAsync();
            Directory.CreateDirectory(_directory);
            await File.AppendAllTextAsync(Path.Combine(_directory, $"{DateTime.Now:yyyy-MM-dd}.log"), $"{entry.Time:O} [{entry.LevelText}] {message}{Environment.NewLine}");
        }
        finally { if (_gate.CurrentCount == 0) _gate.Release(); }
    }
    public string LogDirectory => _directory;
}
