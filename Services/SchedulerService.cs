using GameOrchestrator.Models;

namespace GameOrchestrator.Services;

public sealed class SchedulerService : IDisposable
{
    private readonly System.Threading.Timer _timer;
    private DateTime? _lastRunDate;
    private Func<Task>? _callback;
    private ScheduleConfig? _config;
    public SchedulerService() => _timer = new(async _ => await TickAsync(), null, Timeout.Infinite, Timeout.Infinite);
    public void Start(ScheduleConfig config, Func<Task> callback) { _config = config; _callback = callback; _timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(20)); }
    public DateTime? NextRun
    {
        get
        {
            if (_config?.Enabled != true) return null;
            for (var i = 0; i < 8; i++) { var day = DateTime.Today.AddDays(i); var candidate = day + _config.Time; if (candidate > DateTime.Now && IsAllowed(day.DayOfWeek)) return candidate; }
            return null;
        }
    }
    private async Task TickAsync()
    {
        if (_config?.Enabled != true || _callback is null || !IsAllowed(DateTime.Today.DayOfWeek)) return;
        var now = DateTime.Now;
        var delay = now.TimeOfDay - _config.Time;
        if (delay >= TimeSpan.Zero && delay < TimeSpan.FromSeconds(40) && _lastRunDate != now.Date) { _lastRunDate = now.Date; await _callback(); }
    }
    private bool IsAllowed(DayOfWeek day) => _config!.Repeat switch { ScheduleRepeat.Daily => true, ScheduleRepeat.Weekdays => day is >= DayOfWeek.Monday and <= DayOfWeek.Friday, _ => _config.SelectedDays.Contains(day) };
    public void Dispose() => _timer.Dispose();
}
