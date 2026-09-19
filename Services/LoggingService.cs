using GameOrchestrator.Models;

namespace GameOrchestrator.Services;

public sealed class LoggingService
{
    private static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(30);
    private const long MaxLogDirectoryBytes = 100L * 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _directory;

    public LoggingService(string? directory = null)
    {
        _directory = directory ?? Path.Combine(AppContext.BaseDirectory, "logs");
    }

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

    public async Task<LogCleanupResult> CleanupAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (!Directory.Exists(_directory)) return default;

            var activeLogPath = Path.GetFullPath(Path.Combine(_directory, $"{DateTime.Now:yyyy-MM-dd}.log"));
            var cutoff = DateTime.UtcNow - RetentionPeriod;
            var deletedFiles = 0;
            var failedFiles = 0;
            long freedBytes = 0;

            var files = new DirectoryInfo(_directory).EnumerateFiles("*.log", SearchOption.TopDirectoryOnly).ToList();
            foreach (var file in files.Where(file =>
                         !Path.GetFullPath(file.FullName).Equals(activeLogPath, StringComparison.OrdinalIgnoreCase)
                         && file.LastWriteTimeUtc < cutoff))
            {
                Delete(file);
            }

            files = new DirectoryInfo(_directory).EnumerateFiles("*.log", SearchOption.TopDirectoryOnly).ToList();
            var totalBytes = files.Sum(file => file.Length);
            foreach (var file in files
                         .Where(file => !Path.GetFullPath(file.FullName).Equals(activeLogPath, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(file => file.LastWriteTimeUtc))
            {
                if (totalBytes <= MaxLogDirectoryBytes) break;
                var length = file.Length;
                if (Delete(file)) totalBytes -= length;
            }

            return new LogCleanupResult(deletedFiles, freedBytes, failedFiles);

            bool Delete(FileInfo file)
            {
                var length = file.Length;
                try
                {
                    file.Delete();
                    deletedFiles++;
                    freedBytes += length;
                    return true;
                }
                catch (IOException)
                {
                    failedFiles++;
                    return false;
                }
                catch (UnauthorizedAccessException)
                {
                    failedFiles++;
                    return false;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public string LogDirectory => _directory;
}

public readonly record struct LogCleanupResult(int DeletedFiles, long FreedBytes, int FailedFiles);
