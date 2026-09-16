using System.Globalization;
using System.Windows.Threading;
using GameOrchestrator.Models;

namespace GameOrchestrator.Services;

public sealed record ScheduledLaunchBatch(
    IReadOnlyList<AutomationTaskConfig> Anchors,
    DateTime ScheduledAt,
    DateTime PrepareAt);

public sealed class SchedulerService : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, DateTime> _executedSlots = [];
    private Func<IReadOnlyList<AutomationTaskConfig>>? _tasksProvider;
    private Func<int>? _defaultWakeBeforeProvider;
    private Func<ScheduledLaunchBatch, Task>? _callback;
    private bool _tickRunning;

    public event Action<Exception>? Error;

    public SchedulerService()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _timer.Tick += Timer_Tick;
    }

    public void Start(
        Func<IReadOnlyList<AutomationTaskConfig>> tasksProvider,
        Func<int> defaultWakeBeforeProvider,
        Func<ScheduledLaunchBatch, Task> callback)
    {
        _tasksProvider = tasksProvider;
        _defaultWakeBeforeProvider = defaultWakeBeforeProvider;
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
        if (_tasksProvider is null || _defaultWakeBeforeProvider is null || _callback is null) return;
        var now = DateTime.Now;
        var defaultWakeBefore = Math.Clamp(_defaultWakeBeforeProvider(), 0, 3600);
        var occurrences = new List<(AutomationTaskConfig Task, DateTime ScheduledAt, DateTime PrepareAt)>();

        foreach (var task in _tasksProvider().Where(task => task.Enabled))
        {
            if (!TryParseTime(task.ScheduledStartTime, out var time)) continue;
            foreach (var date in new[] { now.Date, now.Date.AddDays(1) })
            {
                var scheduledAt = date + time;
                var wakeBefore = Math.Clamp(task.WakeBeforeTaskSeconds ?? defaultWakeBefore, 0, 3600);
                occurrences.Add((task, scheduledAt, scheduledAt.AddSeconds(-wakeBefore)));
            }
        }

        foreach (var group in occurrences.GroupBy(item => item.ScheduledAt).OrderBy(group => group.Key))
        {
            var prepareAt = group.Min(item => item.PrepareAt);
            if (now < prepareAt || now > group.Key.AddSeconds(40)) continue;

            var pending = group
                .Where(item => !_executedSlots.ContainsKey(CreateSlotKey(item.Task, item.ScheduledAt)))
                .OrderBy(item => item.Task.DisplayIndex)
                .ToList();
            if (pending.Count == 0) continue;

            foreach (var item in pending)
                _executedSlots[CreateSlotKey(item.Task, item.ScheduledAt)] = item.ScheduledAt;

            var batch = new ScheduledLaunchBatch(pending.Select(item => item.Task).ToList(), group.Key, prepareAt);
            _ = InvokeCallbackSafelyAsync(batch);
        }

        foreach (var oldSlot in _executedSlots.Where(pair => pair.Value < now.Date.AddDays(-1)).Select(pair => pair.Key).ToList())
            _executedSlots.Remove(oldSlot);
    }

    private static string CreateSlotKey(AutomationTaskConfig task, DateTime scheduledAt) =>
        $"{task.Id:N}:{scheduledAt:yyyyMMddHHmmss}";

    private async Task InvokeCallbackSafelyAsync(ScheduledLaunchBatch batch)
    {
        try { await _callback!(batch); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error?.Invoke(ex); }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
    }
}
