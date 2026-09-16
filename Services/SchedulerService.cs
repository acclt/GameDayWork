using System.Globalization;
using System.Windows.Threading;
using GameOrchestrator.Models;

namespace GameOrchestrator.Services;

public sealed class SchedulerService : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly HashSet<string> _executedSlots = [];
    private Func<IReadOnlyList<AutomationTaskConfig>>? _tasksProvider;
    private Func<IReadOnlyList<AutomationTaskConfig>, Task>? _callback;
    private bool _tickRunning;

    public event Action<Exception>? Error;

    public SchedulerService()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(20)
        };
        _timer.Tick += Timer_Tick;
    }

    public void Start(Func<IReadOnlyList<AutomationTaskConfig>> tasksProvider, Func<IReadOnlyList<AutomationTaskConfig>, Task> callback)
    {
        _tasksProvider = tasksProvider;
        _callback = callback;
        _timer.Start();
        _ = TickSafelyAsync();
    }

    public DateTime? NextRun
    {
        get
        {
            if (_tasksProvider is null) return null;
            var now = DateTime.Now;
            return _tasksProvider()
                .Where(task => task.Enabled && TryParseTime(task.ScheduledStartTime, out _))
                .SelectMany(task => NextCandidates(now, task))
                .Where(candidate => candidate > now)
                .OrderBy(candidate => candidate)
                .Select(candidate => (DateTime?)candidate)
                .FirstOrDefault();
        }
    }

    public static bool TryParseTime(string? value, out TimeSpan time)
    {
        var formats = new[] { @"h\:mm", @"hh\:mm", @"h\:mm\:ss", @"hh\:mm\:ss" };
        return TimeSpan.TryParseExact(value?.Trim(), formats, CultureInfo.InvariantCulture, out time)
            && time >= TimeSpan.Zero
            && time < TimeSpan.FromDays(1);
    }

    private static IEnumerable<DateTime> NextCandidates(DateTime now, AutomationTaskConfig task)
    {
        if (!TryParseTime(task.ScheduledStartTime, out var time)) yield break;
        yield return now.Date + time;
        yield return now.Date.AddDays(1) + time;
    }

    private async void Timer_Tick(object? sender, EventArgs e) => await TickSafelyAsync();

    private async Task TickSafelyAsync()
    {
        if (_tickRunning) return;
        _tickRunning = true;
        try
        {
            Tick();
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex);
        }
        finally
        {
            _tickRunning = false;
        }
    }

    private void Tick()
    {
        if (_tasksProvider is null || _callback is null) return;
        var now = DateTime.Now;
        var due = new List<AutomationTaskConfig>();
        foreach (var task in _tasksProvider().Where(task => task.Enabled))
        {
            if (!TryParseTime(task.ScheduledStartTime, out var time)) continue;
            var delay = now.TimeOfDay - time;
            var slot = $"{task.Id:N}:{now:yyyyMMdd}:{time.Ticks}";
            if (delay < TimeSpan.Zero || delay >= TimeSpan.FromSeconds(40) || _executedSlots.Contains(slot)) continue;
            _executedSlots.Add(slot);
            due.Add(task);
        }

        _executedSlots.RemoveWhere(slot => !slot.Contains(now.ToString("yyyyMMdd", CultureInfo.InvariantCulture), StringComparison.Ordinal));
        if (due.Count > 0) _ = InvokeCallbackSafelyAsync(due);
    }

    private async Task InvokeCallbackSafelyAsync(IReadOnlyList<AutomationTaskConfig> tasks)
    {
        try { await _callback!(tasks); }
        catch (Exception ex) { Error?.Invoke(ex); }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
    }
}
