namespace GameOrchestrator.Services;

public sealed class RestartPolicy
{
    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)
    ];
    private readonly Queue<DateTimeOffset> _attempts = new();

    public TimeSpan? RegisterUnexpectedExit(DateTimeOffset now)
    {
        while (_attempts.TryPeek(out var oldest) && now - oldest > TimeSpan.FromMinutes(10)) _attempts.Dequeue();
        if (_attempts.Count >= Delays.Length) return null;
        var delay = Delays[_attempts.Count];
        _attempts.Enqueue(now);
        return delay;
    }

    public void Reset() => _attempts.Clear();
}
