using System.Text;

namespace GameOrchestrator.Services;

public sealed class LogKeywordCompletionDetector
{
    private readonly string _pathPattern;
    private readonly string _keyword;
    private string? _currentPath;
    private long _position;

    public LogKeywordCompletionDetector(string path, string keyword)
    {
        _pathPattern = Path.GetFullPath(path);
        _keyword = keyword;
        _currentPath = ResolveLatestFile();
        _position = _currentPath is not null ? new FileInfo(_currentPath).Length : 0;
    }

    public async Task<bool> CheckAsync(CancellationToken token)
    {
        var path = ResolveLatestFile();
        if (path is null) return false;
        if (!string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase))
        {
            _currentPath = path;
            _position = 0;
        }
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < _position) _position = 0; // 日志轮转或截断
        stream.Position = _position;
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
        while (await reader.ReadLineAsync(token) is { } line)
            if (line.Contains(_keyword, StringComparison.OrdinalIgnoreCase)) { _position = stream.Position; return true; }
        _position = stream.Position;
        return false;
    }

    private string? ResolveLatestFile()
    {
        if (!ContainsWildcard(_pathPattern)) return File.Exists(_pathPattern) ? _pathPattern : null;
        var directory = Path.GetDirectoryName(_pathPattern);
        var pattern = Path.GetFileName(_pathPattern);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return null;
        return Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Select(file => file.FullName)
            .FirstOrDefault();
    }

    public static bool HasMatchingFile(string pathPattern)
    {
        var fullPath = Path.GetFullPath(pathPattern);
        if (!ContainsWildcard(fullPath)) return File.Exists(fullPath);
        var directory = Path.GetDirectoryName(fullPath);
        var pattern = Path.GetFileName(fullPath);
        return !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory) &&
               Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).Any();
    }

    private static bool ContainsWildcard(string path) => path.IndexOfAny(['*', '?']) >= 0;
}
